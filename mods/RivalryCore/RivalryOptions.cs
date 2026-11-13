using Meitou.Modding;

namespace Rivalry;

/// <summary>
/// Rivalry Core's tuning (docs/modding-rivalry.md "Options"). Properties marked <see cref="SettingAttribute"/> are on the Mod settings page; the rest are for
/// dependents (<see cref="IRivalryApi.Options"/>), set in their <c>Initialize</c>. The simulation reads a copy taken in a serial step.
/// </summary>
public sealed class RivalryOptions
{
    [Setting("Inspect every (game hours)", Hint = "How often the world looks at your setup and its directors decide.", Min = 1, Max = 24)]
    public int AssessEveryHours { get; set; } = 2;

    [Setting("Allies reinforce raids", Hint = "An allied faction sends help when hostiles in one of your towns outmatch its defenders.")]
    public bool Reinforcements { get; set; } = true;

    [Setting("Grudges send hunters", Hint = "A faction that holds a large grudge against one of your characters sends squads after them.")]
    public bool Hunters { get; set; } = true;

    [Setting("Opportunist raids", Hint = "Hostile factions raid towns that look rich and poorly defended.")]
    public bool OpportunistRaids { get; set; } = true;

    [Setting("Grudge half-life (days)", Min = 0.5, Max = 30, Step = 0.5)]
    public double GrudgeHalfLifeDays { get; set; } = 4;

    [Setting("Grudge that sends hunters", Min = 10, Max = 500, Step = 10)]
    public float HuntThreshold { get; set; } = 60;

    [Setting("Reinforce below balance", Hint = "Defence over threat in a town under attack at which allies come (1 = even).", Min = 0.1, Max = 2, Step = 0.05)]
    public float ReinforceBelowBalance { get; set; } = 0.75f;

    [Setting("Opportunist raid chance per day", Min = 0, Max = 2, Step = 0.05)]
    public float RaidChancePerDay { get; set; } = 0.25f;

    [Setting("Announce decisions", Hint = "A message line for each decision (for testing and for players who like to know).")]
    public bool Announce { get; set; }

    /// <summary>Grudge per landed hit by a player character on a member of another faction.</summary>
    public float HitGrudge { get; set; } = 1;

    /// <summary>Extra grudge for a hit on a victim already down.</summary>
    public float DownedHitGrudge { get; set; } = 3;

    /// <summary>Grudge when a victim a player character hit dies within <see cref="KillWindowHours"/>.</summary>
    public float KillGrudge { get; set; } = 25;

    public double KillWindowHours { get; set; } = 1;

    /// <summary>AI_PACKAGE a hunting squad runs as a contract aimed at its target; null: hunters only mark the target as an enemy (docs/modding-rivalry.md "Actions").</summary>
    public string? HuntPackage { get; set; }

    /// <summary>AI_PACKAGE a nearby reinforcing squad runs; null: the first package with signal 15 (Contract_DefendPlayerBase) in the data, by id.</summary>
    public string? DefendPackage { get; set; }

    public int HuntSquads { get; set; } = 1;
    public float HuntRadius { get; set; } = 20000;
    public float HuntHours { get; set; } = 24;
    public double HuntCooldownHours { get; set; } = 48;
    public int MaxHuntsPerAssessment { get; set; } = 2;

    /// <summary>How far from a town under attack loaded squads of the reinforcing faction are sent in at once.</summary>
    public float ReinforceRadius { get; set; } = 8000;
    public float ReinforceHours { get; set; } = 12;
    public double ReinforceCooldownHours { get; set; } = 12;

    /// <summary>A town's stored and carried value at which it starts to look worth raiding, and the value per point of defence that makes it a target.</summary>
    public long RaidWealthMin { get; set; } = 20000;
    public float RaidValuePerDefence { get; set; } = 400;
    public double RaidCooldownHours { get; set; } = 96;

    /// <summary>Whether hunts and reinforcements may take a town's own squads (guards, residents), not only roaming ones.</summary>
    public bool UseTownSquads { get; set; }

    public RivalryOptions Copy() => (RivalryOptions)MemberwiseClone();
}
