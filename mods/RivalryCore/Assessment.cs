using Meitou.Data.Gameplay;
using Meitou.Simulation;

namespace Rivalry;

/// <summary>
/// What a director sees at one assessment: the report, the grudges, the world (read only), the data, the tuning and a deterministic random source.
/// </summary>
public sealed class Assessment
{
    readonly RngPurpose _purpose;
    readonly Catalogue _catalogue;

    internal Assessment(World world, PopulationData data, PlayerReport report, NotorietyLedger notoriety, RivalryOptions options, RngPurpose purpose,
        Catalogue catalogue)
    {
        _catalogue = catalogue;
        World = world;
        Data = data;
        Report = report;
        Notoriety = notoriety;
        Options = options;
        _purpose = purpose;
    }

    /// <summary>The running world. Read only: changes go through <see cref="Decisions"/>.</summary>
    public World World { get; }

    public PopulationData Data { get; }
    public PlayerReport Report { get; }
    public NotorietyLedger Notoriety { get; }
    public RivalryOptions Options { get; }

    public int PlayerFaction => Report.PlayerFaction;
    public double GameHours => World.GameHours;

    /// <summary>A uniform number in [0, 1), the same for the same world seed, tick and key: never <c>Random</c>, which would break the determinism check.</summary>
    public float Roll(string key) => Rng.Float(Rng.Hash(World.Seed, (ulong)World.Tick, _purpose, Rng.StableHash(key)));

    public float Relation(int faction) => Known(faction) ? Data.Relations.Get(faction, PlayerFaction) : 0;

    public bool IsHostile(int faction) => Known(faction) && Data.Relations.IsHostile(faction, PlayerFaction);

    /// <summary>An ally of the player and not the player's own faction.</summary>
    public bool IsAlly(int faction) => Known(faction) && faction != PlayerFaction && Data.Relations.IsAlly(faction, PlayerFaction);

    /// <summary>Real factions other than the player's (FCS <c>not real</c> ones left out), in data order.</summary>
    public IEnumerable<int> OtherFactions()
    {
        for (int f = 0; f < Data.Factions.Count; f++)
            if (f != PlayerFaction && !Data.Factions[f].NotReal) yield return f;
    }

    /// <summary>The campaign of a kind a raid or reinforcement by the faction would request (<see cref="Catalogue.CampaignFor"/>); null when it has none.</summary>
    public string? CampaignFor(int faction, CampaignKey key) => _catalogue.CampaignFor(faction, key);

    public FactionData? Faction(int faction) => faction >= 0 && faction < Data.Factions.Count ? Data.Factions[faction] : null;

    bool Known(int faction) => PlayerFaction >= 0 && Data.Relations.Contains(faction) && Data.Relations.Contains(PlayerFaction);
}
