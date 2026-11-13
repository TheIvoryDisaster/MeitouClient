using Meitou.Simulation;

namespace Rivalry;

/// <summary>
/// Rivalry Core's public surface (docs/modding-rivalry.md), implemented by the entry type: a dependent gets it with
/// <c>c.Dependency&lt;IRivalryApi&gt;("rivalry.core")</c> in its own <c>Initialize</c>.
/// </summary>
public interface IRivalryApi
{
    /// <summary>The tuning: the player's settings and the code-only values a dependent may set during its <c>Initialize</c>.</summary>
    RivalryOptions Options { get; }

    /// <summary>
    /// The registrations of the mod <paramref name="modId"/> (the dependent's <c>c.Id</c>): probes, conditions, target kinds, behaviours, rules and
    /// directors, each named with that id as prefix. Allowed only before the first world is built (during dependents' <c>Initialize</c>).
    /// </summary>
    IRivalryRegistry For(string modId);

    /// <summary>The framework's state in a world (its report and grudges); null when the world does not run it. Serial steps of that world only.</summary>
    IRivalryState? In(World world);
}

/// <summary>
/// A rule that looks at an assessment and decides what the world does about the player (docs/modding-rivalry.md "Directors"). Runs in a serial step; it must
/// only read the world and record decisions, take randomness from <see cref="Assessment.Roll"/>, and keep no world state of its own (cooldowns live in
/// <see cref="Decisions.TryClaim"/>, which is saved and hashed).
/// </summary>
public interface IDirector
{
    string Name { get; }

    void Assess(Assessment assessment, Decisions decisions);
}

/// <summary>Rivalry Core's state in one world.</summary>
public interface IRivalryState
{
    /// <summary>The latest inspection; <see cref="PlayerReport.Empty"/> before the first.</summary>
    PlayerReport Report { get; }

    NotorietyLedger Notoriety { get; }

    /// <summary>The latest decisions, newest last, as log lines (at most 64).</summary>
    IReadOnlyList<string> History { get; }

    /// <summary>
    /// The behaviours under way, as lines.
    /// </summary>
    IReadOnlyList<string> Runs { get; }
}
