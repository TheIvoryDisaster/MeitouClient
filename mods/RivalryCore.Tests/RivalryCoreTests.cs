using System.Numerics;
using System.Text;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;
using SimWorld = Meitou.Simulation.World;

namespace Rivalry.Tests;

/// <summary>
/// Rivalry Core on synthetic worlds: the built-in rules and director, a dependent's pieces, the save and the hash.
/// </summary>
public class RivalryCoreTests
{
    /// <summary>
    /// Where the player's town stands.
    /// </summary>
    static readonly Vector3 TownAt = new(2000, 150, 2000);

    /// <summary>
    /// A new record, as a mod file adds one.
    /// </summary>
    static FcsRecord Rec(string id, FcsRecordType type, string name) => new() { StringId = id, Flags = 0x10, Name = name, RecordType = type };

    /// <summary>
    /// The player's faction, an ally (Friends) and an enemy (Foes).
    /// </summary>
    static PopulationData Data()
    {
        var file = new FcsFile();
        file.Records.AddRange([
            Rec("204-gamedata.base", FcsRecordType.Faction, "Nameless"),
            Rec("60-a", FcsRecordType.Faction, "Friends"),
            Rec("61-a", FcsRecordType.Faction, "Foes")]);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        var data = PopulationData.Create(db, []);
        int p = data.FactionIndex("204-gamedata.base"), f = data.FactionIndex("60-a"), e = data.FactionIndex("61-a");
        data.Relations.Add(f, p, 90 - data.Relations.Get(f, p));
        data.Relations.Add(e, p, -90 - data.Relations.Get(e, p));
        return data;
    }

    /// <summary>
    /// A world with one player town and Rivalry Core as its only system.
    /// </summary>
    static (SimWorld World, RivalrySystem System, RivalryOptions Options) Make(PopulationData data, int threads = 1,
        Action<IRivalryRegistry>? register = null)
    {
        var options = new RivalryOptions();
        var system = RivalrySystem.Create(new SystemBuildContext(data, []), options, (RngPurpose)0x80001234u, _ => { }, "test", register);
        var world = new SimWorld(new WorldSettings { Seed = 3, Threads = threads, PublishSnapshots = false, MinPartitionSize = 1 },
            new OpenGroundWalkability((_, _) => 150), [system]);
        world.Player.Faction = data.FactionIndex("204-gamedata.base");
        world.Buildings.AddTown(TownAt, world.Player.Faction, "Home");
        return (world, system, options);
    }

    static CharacterId Spawn(SimWorld world, string name, int faction, Vector3 at, int squad = -1)
    {
        var hot = new CharacterHot { Alive = true, Position = at };
        var cold = new CharacterCold
        {
            Name = name,
            Faction = faction,
            SquadId = squad,
            IsPlayer = faction == world.Player.Faction,
            RecordId = "r-" + name,
        };
        return world.Characters.Spawn(hot, cold, world.Tick);
    }

    static void AddSquad(SimWorld world, int faction, CharacterId leader)
    {
        var s = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = faction };
        s.Members.Add(leader);
        s.Leader = leader;
        world.Squads.Add(s);
    }

    static MemoryStream Saved(RivalrySystem system)
    {
        var stream = new MemoryStream();
        system.Write(stream);
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// A behaviour that begins a run lasting <c>hours</c> and counts its steps.
    /// </summary>
    sealed class Waiting : IBehaviour
    {
        public int Steps;

        public string Name => "test.wait";

        public string Start(BehaviourContext context, BehaviourRequest request)
        {
            var run = context.Begin(this, request, context.World.GameHours + request.Args.Number("hours"));
            return $"waiting as run {run.Id}";
        }

        public bool Step(BehaviourContext context, BehaviourRun run)
        {
            Steps++;
            return context.World.GameHours < run.Until;
        }
    }

    [Fact]
    public void An_outmatched_town_calls_its_ally_and_the_allys_nearby_squad()
    {
        var data = Data();
        var (world, system, _) = Make(data);
        int foes = data.FactionIndex("61-a"), friends = data.FactionIndex("60-a");
        Spawn(world, "Me", world.Player.Faction, TownAt);
        for (int i = 0; i < 3; i++) Spawn(world, "Foe" + i, foes, TownAt + new Vector3(50 * i, 0, 0));
        AddSquad(world, friends, Spawn(world, "Ally", friends, TownAt + new Vector3(3000, 0, 0)));

        world.RunTicks(2);

        var town = Assert.Single(system.Report.Towns);
        Assert.True(town.UnderAttack);
        Assert.True(town.Balance < 1);
        Assert.Contains(system.History, l => l.StartsWith("rivalry.reinforce: reinforce: Friends to town 1") && l.Contains("1 squad(s) nearby"));
    }

    [Fact]
    public void A_grudge_sends_hunters_and_comes_back_from_a_save()
    {
        var data = Data();
        var (world, system, _) = Make(data);
        int foes = data.FactionIndex("61-a");
        var me = Spawn(world, "Me", world.Player.Faction, TownAt + new Vector3(9000, 0, 0));
        AddSquad(world, foes, Spawn(world, "Foe", foes, TownAt + new Vector3(12000, 0, 0)));
        world.Commands.Enqueue(new GrudgeCommand([me], "Foes", 100) { Tick = 0 });

        world.RunTicks(2);

        Assert.Contains(system.History, l => l.Contains("hunt: Foes sends 1 squad(s) after Me"));

        var (world2, system2, _) = Make(data);
        Spawn(world2, "Other", world2.Player.Faction, TownAt);
        var me2 = Spawn(world2, "Me", world2.Player.Faction, TownAt);
        system2.Read(Saved(system));
        world2.RunTicks(1);

        Assert.True(system2.Notoriety.Grudge(foes, me2) > 99);
        Assert.Empty(system2.History);   // the hunt's cooldown came back too
    }

    [Fact]
    public void A_dependents_rule_fires_on_its_probe_once_per_cooldown()
    {
        var data = Data();
        var (world, system, _) = Make(data, register: r =>
        {
            r.AddTownProbe("test.rating", (_, town) => town.Defenders.Count * 2);
            r.AddCondition("test.rated", (a, t) => a.Report.TownOf(t.Town)?.Value("test.rating") >= 2);
            r.AddBehaviour(new Waiting());
            r.AddRule(new Rule
            {
                Name = "test.guests",
                Conditions = ["test.rated"],
                Behaviour = "test.wait",
                CooldownHours = _ => 1000,
                Args = (_, _) => new BehaviourArgs().Set("hours", 500),
            });
        });

        world.RunTicks(2);
        Assert.DoesNotContain(system.History, l => l.StartsWith("test.guests"));   // no defender: the rating is 0

        Spawn(world, "Me", world.Player.Faction, TownAt);
        world.Commands.Enqueue(new AssessNowCommand { Tick = world.Tick });
        world.RunTicks(2);
        world.Commands.Enqueue(new AssessNowCommand { Tick = world.Tick });
        world.RunTicks(2);

        Assert.Single(system.History, l => l.StartsWith("test.guests: waiting as run 1"));
        Assert.Single(system.Runs);
    }

    [Fact]
    public void A_run_under_way_comes_back_from_a_save_and_goes_on()
    {
        var data = Data();
        var first = new Waiting();
        var (world, system, _) = Make(data, register: r => r.AddBehaviour(first));
        world.Commands.Enqueue(new StartBehaviourCommand("test.wait", 1, "Friends", new BehaviourArgs().Set("hours", 500)) { Tick = 0 });
        world.RunTicks(2);
        Assert.Single(system.Runs);

        var second = new Waiting();
        var (world2, system2, _) = Make(data, register: r => r.AddBehaviour(second));
        system2.Read(Saved(system));
        world2.RunTicks(2);

        Assert.Contains("run 1 test.wait town 1", Assert.Single(system2.Runs));
        Assert.True(second.Steps > 0);
    }

    [Fact]
    public void A_version_1_save_still_loads()
    {
        var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(1);
            w.Write(5.0);
            w.Write(4.0);
            w.Write(0);                          // no grudges
            w.Write(1);                          // one cooldown
            w.Write("rivalry.reinforce/town1");
            w.Write(900.0);
        }

        stream.Position = 0;
        var data = Data();
        var (world, system, _) = Make(data);
        int foes = data.FactionIndex("61-a");
        Spawn(world, "Me", world.Player.Faction, TownAt);
        for (int i = 0; i < 3; i++) Spawn(world, "Foe" + i, foes, TownAt + new Vector3(50 * i, 0, 0));
        AddSquad(world, data.FactionIndex("60-a"), Spawn(world, "Ally", data.FactionIndex("60-a"), TownAt + new Vector3(3000, 0, 0)));
        system.Read(stream);
        world.Commands.Enqueue(new AssessNowCommand { Tick = 0 });

        world.RunTicks(2);

        Assert.True(Assert.Single(system.Report.Towns).UnderAttack);
        Assert.DoesNotContain(system.History, l => l.StartsWith("rivalry.reinforce"));   // the saved cooldown holds the rule back
    }

    [Fact]
    public void Names_carry_the_mods_id_and_a_failed_mods_pieces_are_dropped()
    {
        var registry = new Registry();
        var mine = registry.For("inn", "inn.");
        Assert.Throws<ArgumentException>(() => mine.AddCondition("other.open", (_, _) => true));

        mine.AddCondition("inn.open", (_, _) => true);
        Assert.Throws<ArgumentException>(() => mine.AddCondition("inn.open", (_, _) => true));
        registry.For("bar", "bar.").AddCondition("bar.open", (_, _) => true);

        registry.Drop("inn");

        Assert.Equal("bar.open", Assert.Single(registry.Conditions).Name);
    }

    [Fact]
    public void The_world_hash_is_the_same_on_one_thread_and_four()
    {
        static ulong Run(int threads)
        {
            var data = Data();
            var (world, _, _) = Make(data, threads, r => r.AddBehaviour(new Waiting()));
            int foes = data.FactionIndex("61-a");
            var me = Spawn(world, "Me", world.Player.Faction, TownAt);
            for (int i = 0; i < 70; i++) Spawn(world, "Foe" + i, foes, TownAt + new Vector3(10 * i, 0, 0));
            world.Commands.Enqueue(new GrudgeCommand([me], "Foes", 100) { Tick = 0 });
            world.Commands.Enqueue(new StartBehaviourCommand("test.wait", 1, "Foes", new BehaviourArgs().Set("hours", 500)) { Tick = 1 });
            world.RunTicks(5);
            return world.StateHash();
        }

        Assert.Equal(Run(1), Run(4));
    }
}
