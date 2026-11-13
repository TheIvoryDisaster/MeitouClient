using System.Globalization;
using Meitou.Modding;
using Meitou.Simulation;

namespace Rivalry;

/// <summary>
/// Rivalry Core's entry (docs/modding-rivalry.md): registers the framework system, the dialogue codes and the debug rows, and serves dependents as
/// <see cref="IRivalryApi"/>.
/// </summary>
public sealed class RivalryMod : IMod, IRivalryApi, IModDependents
{
    /// <summary>Dialogue condition: the speaker's faction's grudge against the subject (compared with the value).</summary>
    public const int GrudgeCondition = 1600;

    /// <summary>Dialogue condition: the subject's notoriety, every faction's grudge summed (compared with the value).</summary>
    public const int NotorietyCondition = 1601;

    /// <summary>Dialogue effect: the speaker's faction's grudge against the conversation's target grows by the value (negative eases it).</summary>
    public const int GrudgeEffect = 1600;

    /// <summary>
    /// The mod's manifest id.
    /// </summary>
    public const string Id = "rivalry.core";

    readonly Registry _registry = new();
    volatile RivalrySystem? _latest;

    public RivalryMod() => BuiltIn.Register(_registry.For(Id, "rivalry."));

    public RivalryOptions Options { get; } = new();

    public void Initialize(ModContext c)
    {
        c.AddSettings(Options);
        c.AddSystem("rivalry", ctx => Build(ctx, c), SystemAnchor.End);
        c.AddDialogueCondition(GrudgeCondition, check => check.Compare(GrudgeOf(check.World, check.Speaker, check.Subject)));
        c.AddDialogueCondition(NotorietyCondition, check => check.Compare(StateOf(check.World)?.Notoriety.Notoriety(check.Subject) ?? 0));
        c.AddDialogueEffect(GrudgeEffect, act => AddGrudge(act.World, act.Speaker, act.Target, act.Effect.Value));
        AddDebugRows(c);
    }

    public IRivalryRegistry For(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        if (modId == Id) throw new ArgumentException("Rivalry Core: a dependent registers under its own id.", nameof(modId));
        return _registry.For(modId, modId + ".");
    }

    public void DependentFailed(string modId) => _registry.Drop(modId);

    public IRivalryState? In(World world) => StateOf(world);

    RivalrySystem Build(SystemBuildContext ctx, ModContext c)
    {
        _registry.Frozen = true;
        var system = new RivalrySystem(ctx, _registry, Options, c.RngPurpose, c.Log);
        _latest = system;
        return system;
    }

    static RivalrySystem? StateOf(World world) => world.Systems.OfType<RivalrySystem>().FirstOrDefault();

    static float GrudgeOf(World world, CharacterId speaker, CharacterId subject)
    {
        if (StateOf(world) is not { } state || !world.Characters.TryResolveNext(speaker, out int s) || world.Characters.Cold(s) is not { } c) return 0;
        return state.Notoriety.Grudge(c.Faction, subject);
    }

    static void AddGrudge(World world, CharacterId speaker, CharacterId target, int amount)
    {
        if (StateOf(world) is not { } state || !world.Characters.TryResolveNext(speaker, out int s) || world.Characters.Cold(s) is not { } c) return;
        state.Notoriety.Add(c.Faction, target, amount);
    }

    void AddDebugRows(ModContext c)
    {
        c.AddDebugCommand("Rivalry: report", _ => Describe(_latest));
        c.AddDebugCommand("Rivalry: assess now", d =>
        {
            d.Enqueue(new AssessNowCommand { Tick = d.Tick + 1 });
            return "assessment queued";
        });
        c.AddDebugCommand("Rivalry: grudge on selected (faction[, amount])", d => QueueGrudge(d), hasField: true);
        c.AddDebugCommand("Rivalry: runs and rules", _ => DescribeRuns(_latest));
        c.AddDebugCommand("Rivalry: fire rule (rule[, target key])", d => QueueRule(d), hasField: true);
        c.AddDebugCommand("Rivalry: visit town (template, faction[, hours[, town]])", d => QueueVisit(d), hasField: true);
    }

    /// <summary>
    /// The behaviours under way and the rules' names.
    /// </summary>
    static string DescribeRuns(RivalrySystem? system)
    {
        if (system is null) return "no world yet";

        string runs = system.Runs.Count > 0 ? string.Join("; ", system.Runs) : "no runs";
        return $"{runs} | rules: {string.Join(", ", system.RuleNames)}";
    }

    /// <summary>
    /// Queues a rule to fire on a target, whatever its conditions say.
    /// </summary>
    static string QueueRule(DebugCommandContext d)
    {
        var parts = d.Field.Split(',', StringSplitOptions.TrimEntries);
        if (parts[0].Length == 0) return "type a rule name";

        d.Enqueue(new FireRuleCommand(parts[0], parts.Length > 1 ? parts[1] : null) { Tick = d.Tick + 1 });
        return $"{parts[0]} queued";
    }

    /// <summary>
    /// Queues a visit of a trader squad template to a player town (the first when none is given).
    /// </summary>
    static string QueueVisit(DebugCommandContext d)
    {
        var parts = d.Field.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[0].Length == 0) return "type a squad template id and a faction name";

        double hours = parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) ? h : 48;
        int town = parts.Length > 3 && int.TryParse(parts[3], out int t) ? t : 1;
        var args = new BehaviourArgs().Set("template", parts[0]).Set("hours", hours);
        d.Enqueue(new StartBehaviourCommand(BuiltIn.Visit, town, parts[1], args) { Tick = d.Tick + 1 });
        return $"visit of {parts[0]} to town {town} queued";
    }

    static string QueueGrudge(DebugCommandContext d)
    {
        var parts = d.Field.Split(',', StringSplitOptions.TrimEntries);
        if (parts[0].Length == 0 || d.Selected.Count == 0) return "select characters and type a faction name";
        float amount = parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float a) ? a : 100;
        d.Enqueue(new GrudgeCommand(d.Selected, parts[0], amount) { Tick = d.Tick + 1 });
        return $"{parts[0]}: +{amount} grudge on {d.Selected.Count} character(s)";
    }

    /// <summary>One status line from the latest report (read on the host's thread: the report and history are immutable snapshots).</summary>
    static string Describe(RivalrySystem? system)
    {
        if (system is null) return "no world yet";
        var r = system.Report;
        var towns = string.Join("; ", r.Towns.Select(t => $"{t.Name}: {t.Buildings} bldg, {t.StoredValue} stored, defence {t.Defence:0} vs threat {t.Threat:0}"));
        string last = system.History.Count > 0 ? system.History[^1] : "no decisions yet";
        return $"{r.Characters.Count} chars, wealth {r.Wealth}, {system.Notoriety.Count} grudges | {towns} | {last}";
    }
}
