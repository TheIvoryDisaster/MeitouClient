using Meitou.Simulation;

namespace Rivalry;

/// <summary>A faction's grudge against one character.</summary>
public readonly record struct GrudgeKey(int Faction, CharacterId Who) : IComparable<GrudgeKey>
{
    public int CompareTo(GrudgeKey other)
    {
        if (Faction != other.Faction) return Faction.CompareTo(other.Faction);
        if (Who.Slot != other.Who.Slot) return Who.Slot.CompareTo(other.Who.Slot);
        return Who.Generation.CompareTo(other.Who.Generation);
    }
}

/// <summary>
/// The grudges factions hold against characters (docs/modding-rivalry.md "Notoriety"): fed by combat hits and by directors, halved every half-life, dropped below
/// <see cref="Floor"/> or when the character is gone. Changed only in serial steps; walked in key order so the hash and saves never depend on insertion.
/// </summary>
public sealed class NotorietyLedger
{
    /// <summary>A grudge below this is forgotten.</summary>
    public const float Floor = 0.5f;

    readonly SortedDictionary<GrudgeKey, float> _grudges = [];

    public int Count => _grudges.Count;

    /// <summary>Every grudge, in key order.</summary>
    public IEnumerable<KeyValuePair<GrudgeKey, float>> Entries => _grudges;

    public float Grudge(int faction, CharacterId who) => _grudges.GetValueOrDefault(new GrudgeKey(faction, who));

    /// <summary>A character's notoriety: the sum of every faction's grudge against it.</summary>
    public float Notoriety(CharacterId who) => _grudges.Where(g => g.Key.Who == who).Sum(g => g.Value);

    /// <summary>Adds to (or, negative, eases) a grudge; one that falls below <see cref="Floor"/> is forgotten. Serial steps only.</summary>
    public void Add(int faction, CharacterId who, float delta)
    {
        if (faction < 0 || who.IsNone || !float.IsFinite(delta)) return;
        var key = new GrudgeKey(faction, who);
        float value = _grudges.GetValueOrDefault(key) + delta;
        if (value < Floor) _grudges.Remove(key);
        else _grudges[key] = value;
    }

    /// <summary>Every grudge decays by half per <paramref name="halfLifeHours"/>, over <paramref name="hours"/>; characters no longer in the table are dropped.</summary>
    public void Decay(CharacterTable table, double hours, double halfLifeHours)
    {
        float factor = halfLifeHours > 0 ? (float)Math.Pow(0.5, hours / halfLifeHours) : 1;
        foreach (var key in _grudges.Keys.ToList())
        {
            float value = _grudges[key] * factor;
            if (value < Floor || !table.IsAlive(key.Who)) _grudges.Remove(key);
            else _grudges[key] = value;
        }
    }

    public void Clear() => _grudges.Clear();

    public void Hash(ref StateHasher h)
    {
        h.Add(_grudges.Count);
        foreach (var (key, value) in _grudges)
        {
            h.Add(key.Faction);
            h.Add(key.Who);
            h.Add(value);
        }
    }
}
