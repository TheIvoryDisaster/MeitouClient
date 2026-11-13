using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Ai;

namespace Rivalry;

/// <summary>
/// What Rivalry Core registers for itself (docs/modding-rivalry.md "Built-ins"): target kinds, conditions, behaviours and the two rules that fit
/// one (reinforce, opportunist raid). The grudge hunts stay a director (<see cref="GrudgeDirector"/>): they rank across the whole ledger.
/// </summary>
public static class BuiltIn
{
    /// <summary>
    /// Target kinds: each player town (key <c>town{number}</c>); each standing player character (key: its name).
    /// </summary>
    public const string TownTarget = "rivalry.town", CharacterTarget = "rivalry.character";

    /// <summary>
    /// Conditions on a town target.
    /// </summary>
    public const string UnderAttack = "rivalry.under-attack", Calm = "rivalry.calm", Losing = "rivalry.losing", Tempting = "rivalry.tempting",
        TownPublic = "rivalry.town-public";

    /// <summary>
    /// Behaviours.
    /// </summary>
    public const string Raid = "rivalry.raid", Reinforce = "rivalry.reinforce", Hunt = "rivalry.hunt", Visit = "rivalry.visit";

    /// <summary>
    /// Registers the built-ins.
    /// </summary>
    internal static void Register(IRivalryRegistry r)
    {
        r.AddTarget(TownTarget, a => a.Report.Towns.Select(t => new RuleTarget($"town{t.Number}") { Town = t.Number }));
        r.AddTarget(CharacterTarget, a => a.Report.Characters.Where(c => !c.Down).Select(c => new RuleTarget(c.Name) { Character = c.Id, Town = c.Town }));

        r.AddCondition(UnderAttack, (a, t) => a.Report.TownOf(t.Town) is { UnderAttack: true });
        r.AddCondition(Calm, (a, t) => a.Report.TownOf(t.Town) is { UnderAttack: false });
        r.AddCondition(Losing, (a, t) => a.Report.TownOf(t.Town) is { } town && IsLosing(town, a.Options));
        r.AddCondition(Tempting, (a, t) => a.Report.TownOf(t.Town) is { } town && IsTempting(a, town));
        r.AddCondition(TownPublic, (a, t) => a.Report.TownOf(t.Town) is { Public: true });

        r.AddBehaviour(new RaidBehaviour());
        r.AddBehaviour(new ReinforceBehaviour());
        r.AddBehaviour(new HuntBehaviour());
        r.AddBehaviour(new VisitBehaviour());

        r.AddRule(ReinforceRule());
        r.AddRule(OpportunistRule());
        r.AddDirector(new GrudgeDirector());
    }

    /// <summary>
    /// Allies come to a player town whose defenders are outmatched: the closest ally (highest relation; those with a DEFEND_TOWN campaign first)
    /// is asked, once per town per cooldown.
    /// </summary>
    static Rule ReinforceRule() => new()
    {
        Name = "rivalry.reinforce",
        Conditions = [Losing],
        Behaviour = Reinforce,
        Enabled = o => o.Reinforcements,
        CooldownHours = o => o.ReinforceCooldownHours,
        Faction = (a, _) => a.OtherFactions()
            .Where(a.IsAlly)
            .OrderByDescending(f => a.CampaignFor(f, CampaignKey.DefendTown) is not null)
            .ThenByDescending(a.Relation)
            .ThenBy(f => f)
            .DefaultIfEmpty(-1)
            .First(),
        Notice = (a, t) => $"{a.Faction(t.Faction)?.Name} answers the call to {a.Report.TownOf(t.Town)?.Name}",
    };

    /// <summary>
    /// A hostile faction raids a rich, weakly defended player town that is not under attack: the most hostile faction with an assault campaign
    /// is asked, with a chance per day, once per town per cooldown.
    /// </summary>
    static Rule OpportunistRule() => new()
    {
        Name = "rivalry.opportunist",
        Conditions = [Tempting],
        Behaviour = Raid,
        Enabled = o => o.OpportunistRaids,
        ChancePerDay = o => o.RaidChancePerDay,
        CooldownHours = o => o.RaidCooldownHours,
        Faction = (a, _) => a.OtherFactions()
            .Where(f => a.IsHostile(f) && a.CampaignFor(f, CampaignKey.AssaultTown) is not null)
            .OrderBy(a.Relation)
            .ThenBy(f => f)
            .DefaultIfEmpty(-1)
            .First(),
        Notice = (a, t) => $"{a.Faction(t.Faction)?.Name} has its eye on {a.Report.TownOf(t.Town)?.Name}",
    };

    /// <summary>
    /// Under attack with its balance below <see cref="RivalryOptions.ReinforceBelowBalance"/>, or half its defenders down.
    /// </summary>
    static bool IsLosing(TownReport town, RivalryOptions options)
    {
        if (!town.UnderAttack) return false;

        bool halfDown = town.Defenders.Count > 0 && town.DefendersDown * 2 >= town.Defenders.Count;
        return town.Balance < options.ReinforceBelowBalance || halfDown;
    }

    /// <summary>
    /// Not under attack, and its containers and its people's belongings are worth <see cref="RivalryOptions.RaidWealthMin"/> and more than
    /// <see cref="RivalryOptions.RaidValuePerDefence"/> per point of defence.
    /// </summary>
    static bool IsTempting(Assessment a, TownReport town)
    {
        if (town.UnderAttack) return false;

        long value = town.StoredValue + a.Report.Characters.Where(c => c.Town == town.Number).Sum(c => c.GearValue + c.CarriedValue);
        return value >= a.Options.RaidWealthMin && value > a.Options.RaidValuePerDefence * Math.Max(1, town.Defence);
    }
}

/// <summary>
/// Asks the faction for an ASSAULT_TOWN campaign against the town. Texts: <c>campaign</c> (its own pick when unset). Numbers: <c>delay</c> hours.
/// </summary>
public sealed class RaidBehaviour : IBehaviour
{
    public string Name => BuiltIn.Raid;

    public string Start(BehaviourContext context, BehaviourRequest request) => context.Apply(
        new RaidDecision(request.Source, request.Target.Faction, request.Target.Town, request.Args.Text("campaign"), (float)request.Args.Number("delay")));
}

/// <summary>
/// Asks the faction for a DEFEND_TOWN campaign at the town and sends its loaded squads nearby. Texts: <c>campaign</c>. Numbers: <c>delay</c> hours,
/// <c>nearby</c> (0 turns the nearby squads off).
/// </summary>
public sealed class ReinforceBehaviour : IBehaviour
{
    public string Name => BuiltIn.Reinforce;

    public string Start(BehaviourContext context, BehaviourRequest request) => context.Apply(new ReinforceDecision(
        request.Source, request.Target.Faction, request.Target.Town, request.Args.Text("campaign"), (float)request.Args.Number("delay"),
        request.Args.Number("nearby", 1) != 0));
}

/// <summary>
/// Squads of the faction mark the target character as an enemy and, with a package, chase it. Texts: <c>package</c>. Numbers: <c>squads</c>,
/// <c>hours</c>.
/// </summary>
public sealed class HuntBehaviour : IBehaviour
{
    public string Name => BuiltIn.Hunt;

    public string Start(BehaviourContext context, BehaviourRequest request) => context.Apply(new HuntDecision(
        request.Source, request.Target.Faction, request.Target.Character, Math.Max(1, (int)request.Args.Number("squads", 1)),
        request.Args.Text("package"), (float)request.Args.Number("hours", context.Options.HuntHours)));
}

/// <summary>
/// A squad of a wandering trader template appears out of sight of a player town, walks in, stays and leaves (docs/modding-rivalry.md "Visit").
/// Texts: <c>template</c> (a SQUAD_TEMPLATE whose package is a wandering trader's). Numbers: <c>hours</c> of stay (48), <c>minRadius</c> (600),
/// <c>maxRadius</c> (1200), <c>money</c> in its purse. Phases: 0 spawning (tried at each step for <see cref="SpawnWindowHours"/>), 1 waiting for
/// the platoon to load (<see cref="LoadWindowHours"/>), 2 visiting.
/// </summary>
public sealed class VisitBehaviour : IBehaviour
{
    /// <summary>
    /// How long a spawn that finds no walkable spot is tried again, how long a spawned platoon may take to load, and the slack after the stay
    /// before the run is forgotten, in game hours.
    /// </summary>
    public const double SpawnWindowHours = 24, LoadWindowHours = 24, LeaveSlackHours = 24;

    /// <summary>
    /// The phases.
    /// </summary>
    public const int Spawning = 0, Loading = 1, Visiting = 2;

    public string Name => BuiltIn.Visit;

    public string Start(BehaviourContext context, BehaviourRequest request)
    {
        if (context.Population is null) return "visit: no population";
        if (request.Args.Text("template") is null) return "visit: no template";
        if (context.Report.TownOf(request.Target.Town) is null) return $"visit: no town {request.Target.Town}";

        var run = context.Begin(this, request, context.World.GameHours + SpawnWindowHours);
        Step(context, run);
        return $"visit: run {run.Id}, {request.Args.Text("template")} to town {request.Target.Town}"
            + (run.Phase == Spawning ? ", waiting for a spot" : $", platoon {run.Platoon}");
    }

    public bool Step(BehaviourContext context, BehaviourRun run) => run.Phase switch
    {
        Spawning => Spawn(context, run),
        Loading => Load(context, run),
        _ => context.World.GameHours < run.Until && context.World.Platoons.Find(run.Platoon) is not null,
    };

    /// <summary>
    /// Phase 0: spawns the platoon in the ring round the town; no spot yet: tried again until the window closes.
    /// </summary>
    static bool Spawn(BehaviourContext context, BehaviourRun run)
    {
        var world = context.World;
        if (context.Population is not { } population || context.Report.TownOf(run.Town) is not { } town) return false;

        var at = new Vector2(town.Position.X, town.Position.Z);
        int site = NearestSite(context.Data, at);
        int platoon = site < 0 ? -1 : population.SpawnPlatoon(world, run.Faction, run.Args.Text("template")!, site, at,
            (float)run.Args.Number("minRadius", 600), (float)run.Args.Number("maxRadius", 1200), (long)run.Args.Number("money"));
        if (platoon < 0) return site >= 0 && world.GameHours < run.Until;

        run.Platoon = platoon;
        run.Phase = Loading;
        run.Until = world.GameHours + LoadWindowHours;
        return true;
    }

    /// <summary>
    /// Phase 1: once the platoon's squad is in the world it is sent to the town for the stay (<see cref="VisitTown"/>, at the next tick).
    /// </summary>
    static bool Load(BehaviourContext context, BehaviourRun run)
    {
        var world = context.World;
        if (world.Platoons.Find(run.Platoon) is not { } platoon) return false;
        if (platoon.SquadId < 0) return world.GameHours < run.Until;

        float hours = (float)run.Args.Number("hours", 48);
        world.Defer(new VisitTown(platoon.SquadId, run.Town, hours) { Tick = world.Tick + 1 });
        run.Phase = Visiting;
        run.Until = world.GameHours + hours + LeaveSlackHours;
        return true;
    }

    /// <summary>
    /// The town site nearest to a point (ties by index): a spawned platoon's home; -1 without sites.
    /// </summary>
    static int NearestSite(PopulationData data, Vector2 at)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        foreach (var site in data.Sites)
        {
            float d = Vector2.DistanceSquared(new Vector2(site.Position.X, site.Position.Z), at);
            if (d < bestDistance) (best, bestDistance) = (site.Index, d);
        }

        return best;
    }
}
