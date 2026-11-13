using System.Text;
using Meitou.Simulation;

namespace Rivalry;

public sealed partial class RivalrySystem
{
    /// <summary>The sidecar layout's version (docs/modding-rivalry.md "Save data").</summary>
    public const int SaveVersion = 2;

    /// <summary>A saved grudge waiting for its character: faction by string id, character by name and record (slots differ after a load).</summary>
    readonly record struct SavedGrudge(string Faction, string Name, string Record, float Value);

    readonly record struct Subject(string Name, string Record);

    readonly Dictionary<CharacterId, Subject> _subjects = [];
    List<SavedGrudge>? _pending;

    public void Write(Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(SaveVersion);
        w.Write(_nextAssess);
        w.Write(_lastAssess);
        WriteGrudges(w);
        var keys = _cooldowns.Entries.ToList();
        w.Write(keys.Count);
        foreach (var (key, end) in keys)
        {
            w.Write(key);
            w.Write(end);
        }

        _runs.Write(_world, w);
    }

    void WriteGrudges(BinaryWriter w)
    {
        var saved = Notoriety.Entries
            .Where(g => g.Key.Faction < _data.Factions.Count && _subjects.ContainsKey(g.Key.Who))
            .Select(g => new SavedGrudge(_data.Factions[g.Key.Faction].Id, _subjects[g.Key.Who].Name, _subjects[g.Key.Who].Record, g.Value))
            .Concat(_pending ?? [])
            .ToList();
        w.Write(saved.Count);
        foreach (var g in saved)
        {
            w.Write(g.Faction);
            w.Write(g.Name);
            w.Write(g.Record);
            w.Write(g.Value);
        }
    }

    public void Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        int version = r.ReadInt32();
        if (version is < 1 or > SaveVersion)
        {
            _log($"save data version {version} is not 1 to {SaveVersion}; starting fresh");
            return;
        }
        _nextAssess = r.ReadDouble();
        _lastAssess = r.ReadDouble();
        _pending = ReadGrudges(r);
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++) _cooldowns.Set(r.ReadString(), r.ReadDouble());

        // Version 2 adds the behaviours under way.
        if (version >= 2) _runs.Read(r);
    }

    static List<SavedGrudge> ReadGrudges(BinaryReader r)
    {
        int count = r.ReadInt32();
        var list = new List<SavedGrudge>(count);
        for (int i = 0; i < count; i++) list.Add(new SavedGrudge(r.ReadString(), r.ReadString(), r.ReadString(), r.ReadSingle()));
        return list;
    }

    /// <summary>
    /// Keeps each grudge's character named for the next save, and gives saved grudges back to the characters of the loaded world: by faction id, then the
    /// first living character in slot order with the saved name and record, at the first serial step after the load (the save's characters are in
    /// the table by then). One not found is dropped.
    /// </summary>
    void ResolveSaved(World world)
    {
        NameSubjects(world);
        if (_pending is null) return;
        var table = world.Characters;
        var taken = new HashSet<(int, CharacterId)>();
        foreach (var g in _pending)
            if (_data.FactionIndex(g.Faction) is >= 0 and var f && FindSubject(table, g, f, taken) is { IsNone: false } who) Notoriety.Add(f, who, g.Value);
        _pending = null;
    }

    static CharacterId FindSubject(CharacterTable table, SavedGrudge g, int faction, HashSet<(int, CharacterId)> taken)
    {
        var prev = table.Previous;
        for (int slot = 0; slot < prev.Length; slot++)
        {
            if (!prev[slot].Alive || table.Cold(slot) is not { } c || c.Name != g.Name || c.RecordId != g.Record) continue;
            var id = table.IdOf(slot);
            if (taken.Add((faction, id))) return id;
        }
        return CharacterId.None;
    }

    void NameSubjects(World world)
    {
        if (Notoriety.Entries.All(g => _subjects.ContainsKey(g.Key.Who))) return;
        _subjects.Clear();
        foreach (var (key, _) in Notoriety.Entries)
            if (world.Characters.TryResolveNext(key.Who, out int slot) && world.Characters.Cold(slot) is { } c) _subjects[key.Who] = new Subject(c.Name, c.RecordId);
    }
}
