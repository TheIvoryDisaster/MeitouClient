namespace Rivalry;

/// <summary>
/// Evaluates the rules at an assessment (docs/modding-rivalry.md "Rules"): for each rule, in the order added, and each target of its kind, in the
/// kind's order: not cooling, every condition holds, the chance roll passes, a faction is found when the rule names one; then the cooldown is
/// claimed and the behaviour's start is recorded. A rule that throws is logged and off for the rest of that world.
/// </summary>
internal sealed class RuleEngine(Registry registry, Action<string> log)
{
    /// <summary>
    /// The conditions by name.
    /// </summary>
    readonly Dictionary<string, Func<Assessment, RuleTarget, bool>> _conditions =
        registry.Conditions.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);

    /// <summary>
    /// The target kinds by name.
    /// </summary>
    readonly Dictionary<string, Func<Assessment, IEnumerable<RuleTarget>>> _targets =
        registry.Targets.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);

    /// <summary>
    /// The rules, in the order added.
    /// </summary>
    readonly List<Rule> _rules = [.. registry.Rules.Select(e => e.Value)];

    /// <summary>
    /// The rules that threw in this world.
    /// </summary>
    readonly HashSet<string> _faulted = new(StringComparer.Ordinal);

    /// <summary>
    /// The rules' names, in the order added.
    /// </summary>
    public IEnumerable<string> Names => _rules.Select(r => r.Name);

    /// <summary>
    /// How many rules threw in this world.
    /// </summary>
    public int Faulted => _faulted.Count;

    /// <summary>
    /// Evaluates every rule that is on and has not thrown.
    /// </summary>
    public void Evaluate(Assessment a, List<Decision> decided, Cooldowns cooldowns)
    {
        foreach (var rule in _rules)
        {
            if (_faulted.Contains(rule.Name) || rule.Enabled?.Invoke(a.Options) == false) continue;

            try
            {
                Evaluate(a, rule, new Decisions(rule.Name, decided, cooldowns, a.GameHours));
            }
            catch (Exception e)
            {
                _faulted.Add(rule.Name);
                log($"rule {rule.Name} threw and is off for this world: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Fires a rule on the target with <paramref name="key"/> (its first target when null) whatever its conditions, chance and cooldown say (the
    /// debug row); the log line.
    /// </summary>
    public string Fire(Assessment a, string name, string? key, List<Decision> decided, Cooldowns cooldowns)
    {
        if (_rules.FirstOrDefault(r => r.Name == name) is not { } rule) return $"no rule {name}";

        try
        {
            var targets = TargetsOf(a, rule);
            if (targets.FirstOrDefault(t => key is null || t.Key == key) is not { } target) return $"rule {name}: no target {key}";
            if (WithFaction(a, rule, target) is not { } aimed) return $"rule {name}: no faction for {target.Key}";

            Record(a, rule, aimed, new Decisions(rule.Name, decided, cooldowns, a.GameHours));
            return $"rule {name} fired on {target.Key}";
        }
        catch (Exception e)
        {
            return $"rule {name} threw: {e.Message}";
        }
    }

    /// <summary>
    /// One rule over its targets.
    /// </summary>
    void Evaluate(Assessment a, Rule rule, Decisions d)
    {
        float? chance = rule.ChancePerDay?.Invoke(a.Options) * a.Options.AssessEveryHours / 24f;
        double cooldown = rule.CooldownHours(a.Options);
        foreach (var target in TargetsOf(a, rule))
        {
            if (d.Cooling(target.Key) || !Holds(a, rule, target)) continue;
            if (chance is { } c && a.Roll($"{rule.Name}/{target.Key}") >= c) continue;
            if (WithFaction(a, rule, target) is not { } aimed || !d.TryClaim(target.Key, cooldown)) continue;

            Record(a, rule, aimed, d);
        }
    }

    /// <summary>
    /// The rule's targets now.
    /// </summary>
    IEnumerable<RuleTarget> TargetsOf(Assessment a, Rule rule) =>
        _targets.TryGetValue(rule.Target, out var list) ? list(a) : throw new InvalidOperationException($"no target kind {rule.Target}");

    /// <summary>
    /// Whether every named condition and the rule's own test hold.
    /// </summary>
    bool Holds(Assessment a, Rule rule, RuleTarget target)
    {
        foreach (string name in rule.Conditions)
        {
            if (!_conditions.TryGetValue(name, out var test)) throw new InvalidOperationException($"no condition {name}");
            if (!test(a, target)) return false;
        }

        return rule.When?.Invoke(a, target) != false;
    }

    /// <summary>
    /// The target with the rule's acting faction; null when the rule names one and none is found.
    /// </summary>
    static RuleTarget? WithFaction(Assessment a, Rule rule, RuleTarget target)
    {
        if (rule.Faction is null) return target;

        int faction = rule.Faction(a, target);
        return faction < 0 ? null : target with { Faction = faction };
    }

    /// <summary>
    /// Records the behaviour's start and the rule's notice.
    /// </summary>
    static void Record(Assessment a, Rule rule, RuleTarget target, Decisions d)
    {
        d.Start(rule.Behaviour, target, rule.Args?.Invoke(a, target));
        if (a.Options.Announce && rule.Notice?.Invoke(a, target) is { } message) d.Notice(message);
    }
}
