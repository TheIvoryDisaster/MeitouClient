using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Simulation;

namespace Rivalry;

/// <summary>The data the actions pick from: each faction's campaigns by kind and the default defend package, looked up once per world.</summary>
public sealed class Catalogue(PopulationData data, PopulationSystem? population)
{
    /// <summary>Signal 15, <c>Contract_DefendPlayerBase</c> (docs/game/ai.md "Package classes").</summary>
    public const int DefendPlayerBaseSignal = 15;

    readonly Dictionary<(int, CampaignKey), string?> _campaigns = [];
    List<string>? _campaignIds;
    string? _defendPackage;
    bool _defendLooked;

    /// <summary>
    /// The faction's first campaign of a kind: its own <c>campaigns</c> list in data order, then the FACTION_CAMPAIGN records whose <c>faction override</c> names it,
    /// in id order (the "Ally ... (trigger)" DEFEND_TOWN records are of that second kind). An assault must be hostile. Null: none, or no population.
    /// </summary>
    public string? CampaignFor(int faction, CampaignKey key)
    {
        if (population is null || faction < 0 || faction >= data.Factions.Count) return null;
        if (!_campaigns.TryGetValue((faction, key), out string? id)) _campaigns[(faction, key)] = id = Find(data.Factions[faction], key);
        return id;
    }

    /// <summary>The first AI_PACKAGE, by id, whose signal is <see cref="DefendPlayerBaseSignal"/>; null when the data has none.</summary>
    public string? DefendPackage
    {
        get
        {
            if (_defendLooked) return _defendPackage;
            _defendLooked = true;
            _defendPackage = IdsOf(FcsRecordType.AiPackage).FirstOrDefault(id => data.Ai.Package(id)?.Signal == DefendPlayerBaseSignal);
            return _defendPackage;
        }
    }

    string? Find(FactionData faction, CampaignKey key)
    {
        var own = faction.Campaigns.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        _campaignIds ??= [.. IdsOf(FcsRecordType.FactionCampaign)];
        foreach (var id in faction.Campaigns.Select(l => l.Id).Concat(_campaignIds))
        {
            if (population!.CampaignOf(id) is not { } c || c.Key != key || key == CampaignKey.AssaultTown && !c.IsHostile) continue;
            if (own.Contains(id) && c.FactionOverride is null || c.FactionOverride == faction.Id) return id;
        }
        return null;
    }

    IEnumerable<string> IdsOf(FcsRecordType type) =>
        data.Db.Records.Values.Where(r => r.Type == type).Select(r => r.StringId).Order(StringComparer.Ordinal);
}
