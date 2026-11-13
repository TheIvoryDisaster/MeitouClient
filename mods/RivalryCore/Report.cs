using System.Numerics;
using Meitou.Simulation;

namespace Rivalry;

/// <summary>
/// One of the player's characters as an inspection saw it. <see cref="Town"/>: the player town whose radius holds it (0: none). <see cref="Strength"/>: the
/// framework's combat rating (<see cref="Inspector.StrengthOf"/>), 0 while down. Values are in cats: equipped gear, and everything else it carries.
/// </summary>
public sealed record CharacterReport(
    CharacterId Id, string Name, int Squad, Vector3 Position, int Town, bool Down, bool InCombat, float Strength, long GearValue, long CarriedValue)
{
    /// <summary>
    /// The probes' numbers, by probe name (<see cref="IRivalryRegistry.AddCharacterProbe"/>).
    /// </summary>
    public IReadOnlyDictionary<string, double> Values { get; init; } = PlayerReport.NoValues;

    /// <summary>
    /// A probe's number; 0 when no probe of that name ran.
    /// </summary>
    public double Value(string probe) => Values.GetValueOrDefault(probe);
}

/// <summary>
/// One player town as an inspection saw it: its finished buildings by function, the value in its player-owned containers, the player's characters in it
/// (<see cref="Defenders"/>) and the hostile characters standing in it (<see cref="Hostiles"/>), with summed strengths. Turrets add to <see cref="Defence"/>.
/// </summary>
public sealed record TownReport(
    int Number, string Name, Vector3 Position, float Radius, int Buildings, int Turrets, int Gates, int ResearchLevel, long StoredValue,
    IReadOnlyList<CharacterId> Defenders, int DefendersDown, float Defence, IReadOnlyList<CharacterId> Hostiles, float Threat)
{
    /// <summary>Hostile characters are standing in the town.</summary>
    public bool UnderAttack => Hostiles.Count > 0 && Threat > 0;

    /// <summary>Defence over threat: below 1 the defenders are outmatched; infinite when nothing threatens the town.</summary>
    public float Balance => Threat <= 0 ? float.PositiveInfinity : Defence / Threat;

    /// <summary>
    /// Open to the public (<see cref="Meitou.Simulation.Construction.PlayerTown.Public"/>): a visiting trader stays only in a public town.
    /// </summary>
    public bool Public { get; init; }

    /// <summary>
    /// The player's bed fixtures in the town.
    /// </summary>
    public int Beds { get; init; }

    /// <summary>
    /// The probes' numbers, by probe name (<see cref="IRivalryRegistry.AddTownProbe"/>).
    /// </summary>
    public IReadOnlyDictionary<string, double> Values { get; init; } = PlayerReport.NoValues;

    /// <summary>
    /// A probe's number; 0 when no probe of that name ran.
    /// </summary>
    public double Value(string probe) => Values.GetValueOrDefault(probe);
}

/// <summary>A running campaign aimed at a player town (<see cref="PopulationSystem.Wars"/>): its faction, FACTION_CAMPAIGN id, the town number, phase and hostility.</summary>
public sealed record CampaignReport(int Faction, string CampaignId, int Town, int Phase, bool Hostile);

/// <summary>
/// What one inspection of the player's setup found (<see cref="Inspector"/>): characters, towns and the campaigns aimed at them. Immutable; directors read it,
/// and the debug row shows the latest from another thread.
/// </summary>
public sealed record PlayerReport(
    long Tick, double GameHours, int PlayerFaction, long Money,
    IReadOnlyList<CharacterReport> Characters, IReadOnlyList<TownReport> Towns, IReadOnlyList<CampaignReport> Campaigns)
{
    public static readonly PlayerReport Empty = new(0, 0, -1, 0, [], [], []);

    /// <summary>
    /// The values of a report no probe wrote to.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, double> NoValues = new Dictionary<string, double>();

    /// <summary>Money, everything the characters carry and wear, and every town's stored value.</summary>
    public long Wealth => Money + Characters.Sum(c => c.GearValue + c.CarriedValue) + Towns.Sum(t => t.StoredValue);

    /// <summary>The standing character with the highest strength; null when none stands.</summary>
    public CharacterReport? Strongest => Characters.Where(c => !c.Down).MaxBy(c => c.Strength);

    public CharacterReport? Find(CharacterId id) => Characters.FirstOrDefault(c => c.Id == id);

    public TownReport? TownOf(int number) => Towns.FirstOrDefault(t => t.Number == number);
}
