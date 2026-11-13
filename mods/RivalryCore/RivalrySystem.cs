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

    readonly PopulationData _data;
    readonly IReadOnlyList<IDirector> _directors;
    readonly RivalryOptions _settings;
    readonly RngPurpose _purpose;
    readonly Action<string> _log;
    readonly Inspector _inspector;
    readonly Catalogue _catalogue;
    readonly Actions _actions;
    readonly Cooldowns _cooldowns = new();
    readonly HashSet<string> _faulted = new(StringComparer.Ordinal);
    readonly List<string> _history = [];
    RivalryOptions _options;
    double _nextAssess = double.NaN;
    double _lastAssess = double.NaN;
    bool _assessNow;

    public RivalrySystem(SystemBuildContext context, IReadOnlyList<IDirector> directors, RivalryOptions settings, RngPurpose purpose, Action<string> log)
    {
        _data = context.Data;
        _directors = directors;
        _settings = settings;
        _purpose = purpose;
        _log = log;
        _options = settings.Copy();
        _inspector = new Inspector(_data);
        var population = context.Find<PopulationSystem>();
        _catalogue = new Catalogue(_data, population);
        _actions = new Actions(_data, population, context.Find<DialogueSystem>()?.Memory, Notoriety, _catalogue);
    }

    public PlayerReport Report { get; private set; } = PlayerReport.Empty;
    public NotorietyLedger Notoriety { get; } = new();

    /// <summary>A copy, safe to read from the host's thread.</summary>
    public IReadOnlyList<string> History { get; private set; } = [];

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        _options = _settings.Copy();
        foreach (var command in commands)
        {
            if (command is AssessNowCommand) _assessNow = true;
            else if (command is GrudgeCommand g) AddGrudge(g);
        }
    }

    public void SlowWorld(World world)
    {
        ResolveSaved(world);
        CheckKills(world);
        if (double.IsNaN(_nextAssess)) _nextAssess = world.GameHours;
        if (!_assessNow && world.GameHours < _nextAssess) return;
        _assessNow = false;
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
        if (_data.FactionIndex(text) is >= 0 and var byId) return byId;
        for (int f = 0; f < _data.Factions.Count; f++)
            if (string.Equals(_data.Factions[f].Name, text.Trim(), StringComparison.OrdinalIgnoreCase)) return f;
        return -1;
    }

    void Assess(World world)
    {
        double hours = double.IsNaN(_lastAssess) ? 0 : world.GameHours - _lastAssess;
        _lastAssess = world.GameHours;
        _nextAssess = world.GameHours + Math.Max(1, _options.AssessEveryHours);
        Notoriety.Decay(world.Characters, hours, _options.GrudgeHalfLifeDays * 24);
        _cooldowns.Expire(world.GameHours);
        Report = _inspector.Inspect(world);
        if (Report.PlayerFaction < 0) return;

        var decided = Decide(world);
        foreach (var decision in decided) Carry(world, decision);
    }

    List<Decision> Decide(World world)
    {
        var assessment = new Assessment(world, _data, Report, Notoriety, _options, _purpose, _catalogue);
        var decided = new List<Decision>();
        foreach (var director in _directors)
        {
            if (_faulted.Contains(director.Name)) continue;
            try
            {
                director.Assess(assessment, new Decisions(director.Name, decided, _cooldowns, world.GameHours));
            }
            catch (Exception e)
            {
                _faulted.Add(director.Name);
                _log($"director {director.Name} threw and is off for this world: {e.Message}");
            }
        }
        return decided;
    }

    void Carry(World world, Decision decision)
    {
        string line;
        try
        {
            line = _actions.Apply(world, decision, _options, Report);
        }
        catch (Exception e)
        {
            line = $"{decision.GetType().Name} failed: {e.Message}";
        }
        Remember($"{decision.Director}: {line}");
    }

    void Remember(string line)
    {
        _log(line);
        _history.Add(line);
        if (_history.Count > HistoryLength) _history.RemoveAt(0);
        History = [.. _history];
    }

    public void Hash(ref StateHasher h)
    {
        Notoriety.Hash(ref h);
        _cooldowns.Hash(ref h);
        h.Add(_nextAssess);
        h.Add(_faulted.Count);
        HashVictims(ref h);
    }
}
