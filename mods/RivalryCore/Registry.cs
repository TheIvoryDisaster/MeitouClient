using Meitou.Simulation;

namespace Rivalry;

/// <summary>
/// What a rule aims at (docs/modding-rivalry.md "Rules"): <see cref="Key"/> names it in cooldowns and logs, so it must be the same after a load (a
/// town's number, a character's name, never a slot).
/// </summary>
public sealed record RuleTarget(string Key)
{
    /// <summary>
    /// The player town's number; 0: none.
    /// </summary>
    public int Town { get; init; }

    /// <summary>
    /// The character aimed at; none: no character.
    /// </summary>
    public CharacterId Character { get; init; } = CharacterId.None;

    /// <summary>
    /// The faction that acts (a rule's <see cref="Rule.Faction"/> fills it); -1: none.
    /// </summary>
    public int Faction { get; init; } = -1;

    /// <summary>
    /// A building or fixture id the target kind names; null: none.
    /// </summary>
    public string? Site { get; init; }
}

/// <summary>
/// A behaviour's arguments: numbers and texts by name, walked in name order for saves and the hash.
/// </summary>
public sealed class BehaviourArgs
{
    /// <summary>
    /// The numbers, by name.
    /// </summary>
    public SortedDictionary<string, double> Numbers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The texts (record ids, names), by name.
    /// </summary>
    public SortedDictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Sets a number and returns the arguments.
    /// </summary>
    public BehaviourArgs Set(string name, double value)
    {
        Numbers[name] = value;
        return this;
    }

    /// <summary>
    /// Sets a text and returns the arguments.
    /// </summary>
    public BehaviourArgs Set(string name, string value)
    {
        Texts[name] = value;
        return this;
    }

    /// <summary>
    /// The number, or <paramref name="fallback"/> when it is not set.
    /// </summary>
    public double Number(string name, double fallback = 0) => Numbers.GetValueOrDefault(name, fallback);

    /// <summary>
    /// The text; null when it is not set.
    /// </summary>
    public string? Text(string name) => Texts.GetValueOrDefault(name);
}

/// <summary>
/// When conditions hold for a target, start a behaviour, with a chance and a cooldown (docs/modding-rivalry.md "Rules"). Evaluated at every
/// assessment for every target of its kind, in the order rules were added; the cooldown is per rule and target key, saved and hashed.
/// </summary>
public sealed class Rule
{
    /// <summary>
    /// The rule's name, prefixed with its mod's id; it keys the cooldowns in saves.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The target kind (<see cref="IRivalryRegistry.AddTarget"/>); the player's towns by default.
    /// </summary>
    public string Target { get; init; } = BuiltIn.TownTarget;

    /// <summary>
    /// The named conditions that must all hold (<see cref="IRivalryRegistry.AddCondition"/>).
    /// </summary>
    public IReadOnlyList<string> Conditions { get; init; } = [];

    /// <summary>
    /// A test of the rule's own, after the named ones; null: none.
    /// </summary>
    public Func<Assessment, RuleTarget, bool>? When { get; init; }

    /// <summary>
    /// The behaviour to start (<see cref="IRivalryRegistry.AddBehaviour"/>).
    /// </summary>
    public required string Behaviour { get; init; }

    /// <summary>
    /// The chance per game day, spread over the assessments; null: every time the conditions hold.
    /// </summary>
    public Func<RivalryOptions, float>? ChancePerDay { get; init; }

    /// <summary>
    /// Game hours before the rule may fire again on the same target.
    /// </summary>
    public Func<RivalryOptions, double> CooldownHours { get; init; } = _ => 24;

    /// <summary>
    /// The faction that acts; -1 skips the target. Null: the behaviour needs none.
    /// </summary>
    public Func<Assessment, RuleTarget, int>? Faction { get; init; }

    /// <summary>
    /// The behaviour's arguments; null: none.
    /// </summary>
    public Func<Assessment, RuleTarget, BehaviourArgs>? Args { get; init; }

    /// <summary>
    /// A message for the player when the rule fires and <see cref="RivalryOptions.Announce"/> is on; null: none.
    /// </summary>
    public Func<Assessment, RuleTarget, string?>? Notice { get; init; }

    /// <summary>
    /// The rule's switch; null: always on.
    /// </summary>
    public Func<RivalryOptions, bool>? Enabled { get; init; }
}

/// <summary>
/// One mod's registrations with Rivalry Core (<see cref="IRivalryApi.For"/>; docs/modding-rivalry.md "Extension API"). Every name must start with
/// the mod's id and a dot, so two mods never collide. Allowed only before the first world is built (a dependent's <c>Initialize</c>).
/// </summary>
public interface IRivalryRegistry
{
    /// <summary>
    /// A number read into every town's report (<see cref="TownReport.Value"/>) at each inspection. Reads only.
    /// </summary>
    void AddTownProbe(string name, Func<World, TownReport, double> read);

    /// <summary>
    /// A number read into every character's report (<see cref="CharacterReport.Value"/>) at each inspection. Reads only.
    /// </summary>
    void AddCharacterProbe(string name, Func<World, CharacterReport, double> read);

    /// <summary>
    /// A named test over the assessment and a target, usable in rules.
    /// </summary>
    void AddCondition(string name, Func<Assessment, RuleTarget, bool> test);

    /// <summary>
    /// A kind of thing rules aim at: lists its targets at each assessment, in a fixed order.
    /// </summary>
    void AddTarget(string name, Func<Assessment, IEnumerable<RuleTarget>> list);

    /// <summary>
    /// Something a rule or a director can start.
    /// </summary>
    void AddBehaviour(IBehaviour behaviour);

    /// <summary>
    /// A rule, evaluated after the ones added before it.
    /// </summary>
    void AddRule(Rule rule);

    /// <summary>
    /// A director, asked at every assessment after the rules, in the order added.
    /// </summary>
    void AddDirector(IDirector director);
}

/// <summary>
/// Everything registered with Rivalry Core, each piece tagged with the mod that added it, so a failed dependent's pieces can be dropped
/// (<see cref="Drop"/>). Frozen when the first world is built.
/// </summary>
internal sealed class Registry
{
    /// <summary>
    /// One registered piece and the mod that owns it.
    /// </summary>
    internal readonly record struct Entry<T>(string Owner, string Name, T Value);

    /// <summary>
    /// The town probes, in the order added.
    /// </summary>
    public List<Entry<Func<World, TownReport, double>>> TownProbes { get; } = [];

    /// <summary>
    /// The character probes, in the order added.
    /// </summary>
    public List<Entry<Func<World, CharacterReport, double>>> CharacterProbes { get; } = [];

    /// <summary>
    /// The conditions, in the order added.
    /// </summary>
    public List<Entry<Func<Assessment, RuleTarget, bool>>> Conditions { get; } = [];

    /// <summary>
    /// The target kinds, in the order added.
    /// </summary>
    public List<Entry<Func<Assessment, IEnumerable<RuleTarget>>>> Targets { get; } = [];

    /// <summary>
    /// The behaviours, in the order added.
    /// </summary>
    public List<Entry<IBehaviour>> Behaviours { get; } = [];

    /// <summary>
    /// The rules, in the order added.
    /// </summary>
    public List<Entry<Rule>> Rules { get; } = [];

    /// <summary>
    /// The directors, in the order added.
    /// </summary>
    public List<Entry<IDirector>> Directors { get; } = [];

    /// <summary>
    /// Set when the first world is built: nothing is added after.
    /// </summary>
    public bool Frozen { get; set; }

    /// <summary>
    /// The registrations of <paramref name="owner"/>, whose names must start with <paramref name="prefix"/>.
    /// </summary>
    public IRivalryRegistry For(string owner, string prefix) => new Scope(this, owner, prefix);

    /// <summary>
    /// Drops every piece <paramref name="owner"/> added.
    /// </summary>
    public void Drop(string owner)
    {
        TownProbes.RemoveAll(e => e.Owner == owner);
        CharacterProbes.RemoveAll(e => e.Owner == owner);
        Conditions.RemoveAll(e => e.Owner == owner);
        Targets.RemoveAll(e => e.Owner == owner);
        Behaviours.RemoveAll(e => e.Owner == owner);
        Rules.RemoveAll(e => e.Owner == owner);
        Directors.RemoveAll(e => e.Owner == owner);
    }

    /// <summary>
    /// Adds a piece after checking that the registry is open, the name carries the owner's prefix and no piece of its kind has it.
    /// </summary>
    void Add<T>(List<Entry<T>> list, string owner, string prefix, string name, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Frozen) throw new InvalidOperationException("Rivalry Core: registrations are made before the first world, in a mod's Initialize.");
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException($"Rivalry Core: '{name}' must start with '{prefix}'.");
        if (list.Any(e => e.Name == name)) throw new ArgumentException($"Rivalry Core already has '{name}'.");

        list.Add(new Entry<T>(owner, name, value));
    }

    /// <summary>
    /// One mod's view of the registry.
    /// </summary>
    sealed class Scope(Registry registry, string owner, string prefix) : IRivalryRegistry
    {
        public void AddTownProbe(string name, Func<World, TownReport, double> read) => registry.Add(registry.TownProbes, owner, prefix, name, read);

        public void AddCharacterProbe(string name, Func<World, CharacterReport, double> read) =>
            registry.Add(registry.CharacterProbes, owner, prefix, name, read);

        public void AddCondition(string name, Func<Assessment, RuleTarget, bool> test) => registry.Add(registry.Conditions, owner, prefix, name, test);

        public void AddTarget(string name, Func<Assessment, IEnumerable<RuleTarget>> list) => registry.Add(registry.Targets, owner, prefix, name, list);

        public void AddBehaviour(IBehaviour behaviour) => registry.Add(registry.Behaviours, owner, prefix, behaviour.Name, behaviour);

        public void AddRule(Rule rule) => registry.Add(registry.Rules, owner, prefix, rule.Name, rule);

        public void AddDirector(IDirector director) => registry.Add(registry.Directors, owner, prefix, director.Name, director);
    }
}
