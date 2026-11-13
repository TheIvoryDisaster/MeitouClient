using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Rivalry;

public sealed partial class RivalrySystem
{
    /// <summary>A victim a player character hit: who, for which faction, and when, until it dies (a kill) or the window passes.</summary>
    readonly record struct Victim(CharacterId Attacker, int Faction, double Hours);

    readonly SortedDictionary<(int Slot, int Generation), Victim> _victims = [];

    /// <summary>
    /// A landed hit by one of the player's characters on a character of another faction: that faction's grudge against the attacker grows (more when the victim was
    /// already down), and the victim is watched for a kill. Runs serially inside combat; changes only this system's state.
    /// </summary>
    public void HitResolved(World world, in CombatHit hit)
    {
        var table = world.Characters;
        if (world.Player.Faction < 0 || !table.TryResolveNext(hit.Attacker, out int a) || !table.TryResolveNext(hit.Victim, out int v)) return;
        if (table.Cold(a) is not { } attacker || table.Cold(v) is not { } victim) return;
        if (attacker.Faction != world.Player.Faction || victim.Faction == world.Player.Faction || victim.Faction < 0) return;

        Notoriety.Add(victim.Faction, hit.Attacker, _options.HitGrudge + (hit.VictimWasIncapacitated ? _options.DownedHitGrudge : 0));
        if (hit.VictimWasAlive) _victims[(hit.Victim.Slot, hit.Victim.Generation)] = new Victim(hit.Attacker, victim.Faction, world.GameHours);
    }

    /// <summary>Each watched victim that died adds the kill grudge once; one past the window is no longer watched.</summary>
    void CheckKills(World world)
    {
        if (_victims.Count == 0) return;
        foreach (var (key, v) in _victims.ToList())
        {
            var id = new CharacterId(key.Slot, key.Generation);
            bool dead = !world.Characters.TryResolveNext(id, out int slot) || world.Characters.Cold(slot)?.Medical is { Dead: true };
            if (dead) Notoriety.Add(v.Faction, v.Attacker, _options.KillGrudge);
            if (dead || world.GameHours - v.Hours > _options.KillWindowHours) _victims.Remove(key);
        }
    }

    void HashVictims(ref StateHasher h)
    {
        h.Add(_victims.Count);
        foreach (var (key, v) in _victims)
        {
            h.Add(key.Slot);
            h.Add(key.Generation);
            h.Add(v.Attacker);
            h.Add(v.Faction);
            h.Add(v.Hours);
        }
    }
}
