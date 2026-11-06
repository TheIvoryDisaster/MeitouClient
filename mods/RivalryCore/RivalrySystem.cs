using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Dialogue;

namespace Rivalry;

/// <summary>Assess at the next serial step, whatever the cadence (the debug row's "assess now").</summary>
public sealed record AssessNowCommand : SimCommand;

/// <summary>Adds to the grudge a faction (its name or string id) holds against characters (the debug row; dependents use <see cref="Decisions.Grudge"/>).</summary>
public sealed record GrudgeCommand(IReadOnlyList<CharacterId> Characters, string Faction, float Amount) : SimCommand;

/// <summary>
/// Rivalry Core in one world (docs/modding-rivalry.md): the grudge ledger fed by hits, and at each assessment the inspection, the directors and their decisions,
/// all in serial steps. Its state goes into the world's hash and the save.
/// </summary>
public sealed partial class RivalrySystem : ICombatHitObserver, IStateHashed, IModSaveData, IRivalryState
{
    public const int HistoryLength = 64;

    readonly PopulationData data;
    readonly IReadOnlyList<IDirector> directors;
    readonly RivalryOptions settings;
    readonly RngPurpose purpose;
    readonly Action<string> log;
    readonly Inspector inspector;
    readonly Catalogue catalogue;
    readonly Actions actions;
    readonly Cooldowns cooldowns = new();
    readonly HashSet<string> faulted = new(StringComparer.Ordinal);
    readonly List<string> history = [];
    RivalryOptions options;
    double nextAssess = double.NaN;
    double lastAssess = double.NaN;
    bool assessNow;

    public RivalrySystem(SystemBuildContext context, IReadOnlyList<IDirector> directors, RivalryOptions settings, RngPurpose purpose, Action<string> log)
    {
        data = context.Data;
        this.directors = directors;
        this.settings = settings;
        this.purpose = purpose;
        this.log = log;
        options = settings.Copy();
        inspector = new Inspector(data);
        var population = context.Find<PopulationSystem>();
        catalogue = new Catalogue(data, population);
        actions = new Actions(data, population, context.Find<DialogueSystem>()?.Memory, Notoriety, catalogue);
    }

    public PlayerReport Report { get; private set; } = PlayerReport.Empty;
    public NotorietyLedger Notoriety { get; } = new();

    /// <summary>A copy, safe to read from the host's thread.</summary>
    public IReadOnlyList<string> History { get; private set; } = [];

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        options = settings.Copy();
        foreach (var command in commands)
        {
            if (command is AssessNowCommand) assessNow = true;
            else if (command is GrudgeCommand g) AddGrudge(g);
        }
    }

    public void SlowWorld(World world)
    {
        ResolveSaved(world);
        CheckKills(world);
        if (double.IsNaN(nextAssess)) nextAssess = world.GameHours;
        if (!assessNow && world.GameHours < nextAssess) return;
        assessNow = false;
        Assess(world);
    }

    void AddGrudge(GrudgeCommand g)
    {
        int faction = FactionByName(g.Faction);
        if (faction < 0) return;
        foreach (var who in g.Characters) Notoriety.Add(faction, who, g.Amount);
    }

    int FactionByName(string text)
    {
        if (data.FactionIndex(text) is >= 0 and var byId) return byId;
        for (int f = 0; f < data.Factions.Count; f++)
            if (string.Equals(data.Factions[f].Name, text.Trim(), StringComparison.OrdinalIgnoreCase)) return f;
        return -1;
    }

    void Assess(World world)
    {
        double hours = double.IsNaN(lastAssess) ? 0 : world.GameHours - lastAssess;
        lastAssess = world.GameHours;
        nextAssess = world.GameHours + Math.Max(1, options.AssessEveryHours);
        Notoriety.Decay(world.Characters, hours, options.GrudgeHalfLifeDays * 24);
        cooldowns.Expire(world.GameHours);
        Report = inspector.Inspect(world);
        if (Report.PlayerFaction < 0) return;

        var decided = Decide(world);
        foreach (var decision in decided) Carry(world, decision);
    }

    List<Decision> Decide(World world)
    {
        var assessment = new Assessment(world, data, Report, Notoriety, options, purpose, catalogue);
        var decided = new List<Decision>();
        foreach (var director in directors)
        {
            if (faulted.Contains(director.Name)) continue;
            try
            {
                director.Assess(assessment, new Decisions(director.Name, decided, cooldowns, world.GameHours));
            }
            catch (Exception e)
            {
                faulted.Add(director.Name);
                log($"director {director.Name} threw and is off for this world: {e.Message}");
            }
        }
        return decided;
    }

    void Carry(World world, Decision decision)
    {
        string line;
        try
        {
            line = actions.Apply(world, decision, options, Report);
        }
        catch (Exception e)
        {
            line = $"{decision.GetType().Name} failed: {e.Message}";
        }
        Remember($"{decision.Director}: {line}");
    }

    void Remember(string line)
    {
        log(line);
        history.Add(line);
        if (history.Count > HistoryLength) history.RemoveAt(0);
        History = [.. history];
    }

    public void Hash(ref StateHasher h)
    {
        Notoriety.Hash(ref h);
        cooldowns.Hash(ref h);
        h.Add(nextAssess);
        h.Add(faulted.Count);
        HashVictims(ref h);
    }
}
