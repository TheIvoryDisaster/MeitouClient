using System.Globalization;
using Meitou.Modding;
using Meitou.Simulation;

namespace Rivalry;

/// <summary>
/// Rivalry Core's entry (docs/modding-rivalry.md): registers the framework system, the dialogue codes and the debug rows, and serves dependents as
/// <see cref="IRivalryApi"/>.
/// </summary>
public sealed class RivalryMod : IMod, IRivalryApi
{
    /// <summary>Dialogue condition: the speaker's faction's grudge against the subject (compared with the value).</summary>
    public const int GrudgeCondition = 1600;

    /// <summary>Dialogue condition: the subject's notoriety, every faction's grudge summed (compared with the value).</summary>
    public const int NotorietyCondition = 1601;

    /// <summary>Dialogue effect: the speaker's faction's grudge against the conversation's target grows by the value (negative eases it).</summary>
    public const int GrudgeEffect = 1600;

    readonly List<IDirector> _directors = [new ReinforcementDirector(), new GrudgeDirector(), new OpportunistDirector()];
    volatile RivalrySystem? _latest;
    bool _built;

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

    public void AddDirector(IDirector director)
    {
        ArgumentNullException.ThrowIfNull(director);
        if (_built) throw new InvalidOperationException("Rivalry Core: directors are added before the first world, in a dependent's Initialize.");
        if (_directors.Any(d => d.Name == director.Name)) throw new ArgumentException($"Rivalry Core already has a director named '{director.Name}'.", nameof(director));
        _directors.Add(director);
    }

    public IRivalryState? In(World world) => StateOf(world);

    RivalrySystem Build(SystemBuildContext ctx, ModContext c)
    {
        _built = true;
        var system = new RivalrySystem(ctx, [.. _directors], Options, c.RngPurpose, c.Log);
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
