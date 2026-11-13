using Meitou.Simulation;

namespace Rivalry;

/// <summary>What a director decided; applied in order after every director has run (docs/modding-rivalry.md "Actions").</summary>
public abstract record Decision(string Director);

/// <summary>Up to <paramref name="Squads"/> squads of the faction near the target mark it as an enemy and, with a package, run it as a contract aimed at the target.</summary>
public sealed record HuntDecision(string Director, int Faction, CharacterId Target, int Squads, string? Package, float Hours) : Decision(Director);

/// <summary>The faction is asked for an ASSAULT_TOWN campaign against the player town (its own pick when <paramref name="CampaignId"/> is null).</summary>
public sealed record RaidDecision(string Director, int Faction, int Town, string? CampaignId, float DelayHours) : Decision(Director);

/// <summary>
/// The faction is asked for a DEFEND_TOWN campaign at the player town, and with <paramref name="Nearby"/> its loaded squads close to the town go in at once:
/// they mark the town's hostiles as enemies and run the defend package.
/// </summary>
public sealed record ReinforceDecision(string Director, int Faction, int Town, string? CampaignId, float DelayHours, bool Nearby) : Decision(Director);

/// <summary>A squad runs an AI_PACKAGE as a contract (dialogue's <c>AI contract</c>), optionally aimed at a character.</summary>
public sealed record DirectDecision(string Director, int Squad, string Package, float Hours, CharacterId? Target) : Decision(Director);

/// <summary>The faction's relation with the player moves by <paramref name="Delta"/>.</summary>
public sealed record RelationDecision(string Director, int Faction, float Delta) : Decision(Director);

public sealed record GrudgeDecision(string Director, int Faction, CharacterId Who, float Delta) : Decision(Director);

public sealed record NoticeDecision(string Director, string Message) : Decision(Director);

/// <summary>Anything else, run in the same serial step: it may change the world as a serial phase may, deterministically.</summary>
public sealed record CustomDecision(string Director, string Label, Action<World> Apply) : Decision(Director);

/// <summary>
/// A behaviour starts for a target (<see cref="IRivalryRegistry.AddBehaviour"/>): what a rule decides, and what a director may.
/// </summary>
public sealed record StartDecision(string Director, string Behaviour, RuleTarget Target, BehaviourArgs Args) : Decision(Director);

/// <summary>Where one director records its decisions during an assessment; named by the director so cooldowns never collide.</summary>
public sealed class Decisions
{
    readonly List<Decision> _list;
    readonly Cooldowns _cooldowns;
    readonly double _now;

    internal Decisions(string director, List<Decision> list, Cooldowns cooldowns, double now)
    {
        Director = director;
        _list = list;
        _cooldowns = cooldowns;
        _now = now;
    }

    public string Director { get; }

    /// <summary>True, and the key cooling for <paramref name="hours"/> game hours, when the key (scoped to this director) is not cooling; false otherwise.</summary>
    public bool TryClaim(string key, double hours) => _cooldowns.TryClaim($"{Director}/{key}", _now, hours);

    /// <summary>Whether the key (scoped to this director) is cooling.</summary>
    public bool Cooling(string key) => _cooldowns.Cooling($"{Director}/{key}", _now);

    public void Hunt(int faction, CharacterId target, int squads = 1, string? package = null, float hours = 24) =>
        _list.Add(new HuntDecision(Director, faction, target, Math.Max(1, squads), package, hours));

    public void Raid(int faction, int town, string? campaignId = null, float delayHours = 0) =>
        _list.Add(new RaidDecision(Director, faction, town, campaignId, delayHours));

    public void Reinforce(int faction, int town, string? campaignId = null, float delayHours = 0, bool nearby = true) =>
        _list.Add(new ReinforceDecision(Director, faction, town, campaignId, delayHours, nearby));

    public void Direct(int squad, string package, float hours, CharacterId? target = null) =>
        _list.Add(new DirectDecision(Director, squad, package, hours, target));

    public void ShiftRelation(int faction, float delta) => _list.Add(new RelationDecision(Director, faction, delta));

    public void Grudge(int faction, CharacterId who, float delta) => _list.Add(new GrudgeDecision(Director, faction, who, delta));

    public void Notice(string message) => _list.Add(new NoticeDecision(Director, message));

    /// <summary>
    /// Starts a behaviour by name for a target.
    /// </summary>
    public void Start(string behaviour, RuleTarget target, BehaviourArgs? args = null) =>
        _list.Add(new StartDecision(Director, behaviour, target, args ?? new BehaviourArgs()));

    public void Custom(string label, Action<World> apply) => _list.Add(new CustomDecision(Director, label, apply));
}

/// <summary>Keys cooling until a game hour; walked in key order for the hash and saves.</summary>
public sealed class Cooldowns
{
    readonly SortedDictionary<string, double> _until = new(StringComparer.Ordinal);

    public IEnumerable<KeyValuePair<string, double>> Entries => _until;

    public bool Cooling(string key, double now) => _until.TryGetValue(key, out double end) && end > now;

    public bool TryClaim(string key, double now, double hours)
    {
        if (Cooling(key, now)) return false;
        _until[key] = now + Math.Max(0, hours);
        return true;
    }

    public void Set(string key, double end) => _until[key] = end;

    /// <summary>Drops the keys that stopped cooling.</summary>
    public void Expire(double now)
    {
        foreach (var key in _until.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) _until.Remove(key);
    }

    public void Clear() => _until.Clear();

    public void Hash(ref StateHasher h)
    {
        h.Add(_until.Count);
        foreach (var (key, end) in _until)
        {
            h.Add(Rng.StableHash(key));
            h.Add(end);
        }
    }
}
