using System.Numerics;
using Meitou.Simulation;

namespace Rivalry;

/// <summary>
/// Something a rule or a director can make the world do (docs/modding-rivalry.md "Behaviours"). It keeps no state of its own: one that lasts
/// begins a <see cref="BehaviourRun"/>, which the framework saves, hashes and steps. Serial steps only; randomness from
/// <see cref="BehaviourContext.Roll"/>.
/// </summary>
public interface IBehaviour
{
    /// <summary>
    /// The behaviour's name, prefixed with its mod's id; it names its runs in saves.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Starts it for a target; returns a log line saying what happened.
    /// </summary>
    string Start(BehaviourContext context, BehaviourRequest request);

    /// <summary>
    /// Steps one run of it, at every slow step of the world; false ends the run.
    /// </summary>
    bool Step(BehaviourContext context, BehaviourRun run) => false;
}

/// <summary>
/// A start of a behaviour: who asked (a rule's or a director's name), the target and the arguments.
/// </summary>
public sealed record BehaviourRequest(string Source, RuleTarget Target, BehaviourArgs Args);

/// <summary>
/// A behaviour under way (docs/modding-rivalry.md "Save data"): all its state, saved and hashed by the framework. <see cref="Platoon"/> is found
/// again after a load by its template, faction and position (platoon ids change on load).
/// </summary>
public sealed class BehaviourRun
{
    /// <summary>
    /// The run's number in this world, in the order runs began.
    /// </summary>
    public required int Id { get; init; }

    /// <summary>
    /// The behaviour's name.
    /// </summary>
    public required string Behaviour { get; init; }

    /// <summary>
    /// The rule or director that started it.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// The target's key (<see cref="RuleTarget.Key"/>).
    /// </summary>
    public string Key { get; init; } = "";

    /// <summary>
    /// The faction that acts; -1: none.
    /// </summary>
    public int Faction { get; init; } = -1;

    /// <summary>
    /// The player town's number; 0: none.
    /// </summary>
    public int Town { get; init; }

    /// <summary>
    /// The platoon the run spawned or drives; -1: none (yet, or not found again after a load).
    /// </summary>
    public int Platoon { get; set; } = -1;

    /// <summary>
    /// The behaviour's own step counter.
    /// </summary>
    public int Phase { get; set; }

    /// <summary>
    /// The game hour the run began.
    /// </summary>
    public double Started { get; init; }

    /// <summary>
    /// The game hour the current phase gives up or ends.
    /// </summary>
    public double Until { get; set; }

    /// <summary>
    /// The request's arguments, and whatever else the behaviour keeps.
    /// </summary>
    public BehaviourArgs Args { get; init; } = new();
}

/// <summary>
/// What a behaviour works with in a serial step: the world, the data, the latest report, the tuning, and the framework's own actions.
/// </summary>
public sealed class BehaviourContext
{
    /// <summary>
    /// The RNG purpose of the mod.
    /// </summary>
    readonly RngPurpose _purpose;

    /// <summary>
    /// The framework's actions.
    /// </summary>
    readonly Actions _actions;

    /// <summary>
    /// The runs under way.
    /// </summary>
    readonly BehaviourRuns _runs;

    internal BehaviourContext(World world, PopulationData data, PlayerReport report, RivalryOptions options, RngPurpose purpose, Actions actions,
        BehaviourRuns runs, PopulationSystem? population)
    {
        World = world;
        Data = data;
        Report = report;
        Options = options;
        Population = population;
        _purpose = purpose;
        _actions = actions;
        _runs = runs;
    }

    /// <summary>
    /// The running world; a behaviour may change it as a serial phase may, deterministically.
    /// </summary>
    public World World { get; }

    /// <summary>
    /// The game's data.
    /// </summary>
    public PopulationData Data { get; }

    /// <summary>
    /// The latest inspection.
    /// </summary>
    public PlayerReport Report { get; }

    /// <summary>
    /// The tuning.
    /// </summary>
    public RivalryOptions Options { get; }

    /// <summary>
    /// The population system; null in a world without one.
    /// </summary>
    public PopulationSystem? Population { get; }

    /// <summary>
    /// A uniform number in [0, 1), the same for the same world seed, tick and key.
    /// </summary>
    public float Roll(string key) => Rng.Float(Rng.Hash(World.Seed, (ulong)World.Tick, _purpose, Rng.StableHash(key)));

    /// <summary>
    /// Carries out one of the framework's decisions now and returns its log line.
    /// </summary>
    public string Apply(Decision decision) => _actions.Apply(World, decision, Options, Report);

    /// <summary>
    /// Begins a run of <paramref name="behaviour"/> for the request, in phase 0, whose first phase lasts until <paramref name="until"/>.
    /// </summary>
    public BehaviourRun Begin(IBehaviour behaviour, BehaviourRequest request, double until) => _runs.Begin(World, behaviour.Name, request, until);
}

/// <summary>
/// The behaviours under way in one world: begun, stepped in id order, saved and hashed.
/// </summary>
internal sealed class BehaviourRuns(PopulationData data, IReadOnlyDictionary<string, IBehaviour> behaviours, Action<string> log)
{
    /// <summary>
    /// A loaded run's platoon, to be found again: its template and where it stood.
    /// </summary>
    readonly record struct LostPlatoon(string Template, Vector2 Position);

    /// <summary>
    /// The runs, in id order.
    /// </summary>
    readonly List<BehaviourRun> _runs = [];

    /// <summary>
    /// The platoons of loaded runs not found again yet, by run id.
    /// </summary>
    readonly SortedDictionary<int, LostPlatoon> _lost = [];

    /// <summary>
    /// The next run's id.
    /// </summary>
    int _nextId = 1;

    /// <summary>
    /// The runs under way, in id order.
    /// </summary>
    public IReadOnlyList<BehaviourRun> All => _runs;

    /// <summary>
    /// Adds a run in phase 0.
    /// </summary>
    public BehaviourRun Begin(World world, string behaviour, BehaviourRequest request, double until)
    {
        var run = new BehaviourRun
        {
            Id = _nextId++,
            Behaviour = behaviour,
            Source = request.Source,
            Key = request.Target.Key,
            Faction = request.Target.Faction,
            Town = request.Target.Town,
            Started = world.GameHours,
            Until = until,
            Args = request.Args,
        };
        _runs.Add(run);
        return run;
    }

    /// <summary>
    /// Starts a behaviour by name; the log line.
    /// </summary>
    public string Start(BehaviourContext context, string name, BehaviourRequest request) =>
        behaviours.TryGetValue(name, out var behaviour) ? behaviour.Start(context, request) : $"no behaviour {name}";

    /// <summary>
    /// Steps every run; one that ends, throws or whose behaviour is gone is removed.
    /// </summary>
    public void Step(BehaviourContext context)
    {
        FindLost(context.World);
        foreach (var run in _runs.ToList())
        {
            if (!Goes(context, run)) _runs.Remove(run);
        }
    }

    /// <summary>
    /// Whether the run goes on after this step.
    /// </summary>
    bool Goes(BehaviourContext context, BehaviourRun run)
    {
        if (!behaviours.TryGetValue(run.Behaviour, out var behaviour))
        {
            log($"run {run.Id}: behaviour {run.Behaviour} is not registered; dropped");
            return false;
        }

        try
        {
            return behaviour.Step(context, run);
        }
        catch (Exception e)
        {
            log($"run {run.Id} of {run.Behaviour} threw and is dropped: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gives loaded runs their platoons back: the nearest platoon of the saved template and faction no other run holds, in run order.
    /// </summary>
    void FindLost(World world)
    {
        if (_lost.Count == 0) return;

        var taken = _runs.Where(r => r.Platoon >= 0).Select(r => r.Platoon).ToHashSet();
        foreach (var (id, lost) in _lost)
        {
            if (_runs.FirstOrDefault(r => r.Id == id) is not { } run) continue;

            var found = world.Platoons.All
                .Where(p => p.TemplateId == lost.Template && p.Faction == run.Faction && !taken.Contains(p.Id))
                .OrderBy(p => Vector2.DistanceSquared(p.Position, lost.Position))
                .ThenBy(p => p.Id)
                .FirstOrDefault();
            if (found is null) continue;

            run.Platoon = found.Id;
            taken.Add(found.Id);
        }

        _lost.Clear();
    }

    /// <summary>
    /// The runs and the id counter, into the world's hash.
    /// </summary>
    public void Hash(ref StateHasher h)
    {
        h.Add(_runs.Count);
        h.Add(_nextId);
        foreach (var run in _runs)
        {
            h.Add(run.Id);
            h.Add(Rng.StableHash(run.Behaviour));
            h.Add(run.Faction);
            h.Add(run.Town);
            h.Add(run.Platoon);
            h.Add(run.Phase);
            h.Add(run.Until);
            foreach (var (name, value) in run.Args.Numbers)
            {
                h.Add(Rng.StableHash(name));
                h.Add(value);
            }
        }
    }

    /// <summary>
    /// Writes the runs: factions by string id, platoons by template and position.
    /// </summary>
    public void Write(World? world, BinaryWriter w)
    {
        w.Write(_nextId);
        w.Write(_runs.Count);
        foreach (var run in _runs)
        {
            w.Write(run.Id);
            w.Write(run.Behaviour);
            w.Write(run.Source);
            w.Write(run.Key);
            w.Write(run.Faction >= 0 && run.Faction < data.Factions.Count ? data.Factions[run.Faction].Id : "");
            w.Write(run.Town);
            w.Write(run.Phase);
            w.Write(run.Started);
            w.Write(run.Until);
            WritePlatoon(world, run, w);
            WriteArgs(run.Args, w);
        }
    }

    /// <summary>
    /// The run's platoon as its template and position; an empty template for none.
    /// </summary>
    void WritePlatoon(World? world, BehaviourRun run, BinaryWriter w)
    {
        var lost = _lost.TryGetValue(run.Id, out var waiting) ? waiting
            : world?.Platoons.Find(run.Platoon) is { } p ? new LostPlatoon(p.TemplateId, p.Position)
            : new LostPlatoon("", default);
        w.Write(lost.Template);
        w.Write(lost.Position.X);
        w.Write(lost.Position.Y);
    }

    /// <summary>
    /// Writes a run's arguments.
    /// </summary>
    static void WriteArgs(BehaviourArgs args, BinaryWriter w)
    {
        w.Write(args.Numbers.Count);
        foreach (var (name, value) in args.Numbers)
        {
            w.Write(name);
            w.Write(value);
        }

        w.Write(args.Texts.Count);
        foreach (var (name, value) in args.Texts)
        {
            w.Write(name);
            w.Write(value);
        }
    }

    /// <summary>
    /// Reads the runs a save holds, replacing the ones here.
    /// </summary>
    public void Read(BinaryReader r)
    {
        _runs.Clear();
        _lost.Clear();
        _nextId = r.ReadInt32();
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++) ReadRun(r);
    }

    /// <summary>
    /// Reads one run and notes its platoon to be found again.
    /// </summary>
    void ReadRun(BinaryReader r)
    {
        int id = r.ReadInt32();
        string behaviour = r.ReadString(), source = r.ReadString(), key = r.ReadString(), faction = r.ReadString();
        var run = new BehaviourRun
        {
            Id = id,
            Behaviour = behaviour,
            Source = source,
            Key = key,
            Faction = faction.Length > 0 ? data.FactionIndex(faction) : -1,
            Town = r.ReadInt32(),
            Phase = r.ReadInt32(),
            Started = r.ReadDouble(),
            Until = r.ReadDouble(),
        };
        string template = r.ReadString();
        var at = new Vector2(r.ReadSingle(), r.ReadSingle());
        if (template.Length > 0) _lost[id] = new LostPlatoon(template, at);

        ReadArgs(run.Args, r);
        _runs.Add(run);
    }

    /// <summary>
    /// Reads a run's arguments.
    /// </summary>
    static void ReadArgs(BehaviourArgs args, BinaryReader r)
    {
        int numbers = r.ReadInt32();
        for (int i = 0; i < numbers; i++) args.Numbers[r.ReadString()] = r.ReadDouble();

        int texts = r.ReadInt32();
        for (int i = 0; i < texts; i++) args.Texts[r.ReadString()] = r.ReadString();
    }
}
