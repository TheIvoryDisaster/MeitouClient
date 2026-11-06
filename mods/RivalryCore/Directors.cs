using Meitou.Data.Gameplay;

namespace Rivalry;

/// <summary>
/// Allies come to a player town whose defenders are outmatched (docs/modding-rivalry.md "Built-in directors"): the town is under attack and its balance is below
/// <see cref="RivalryOptions.ReinforceBelowBalance"/>, or half its defenders are down. The player's closest ally (highest relation) is asked, once per town per
/// cooldown. Allies with a DEFEND_TOWN campaign come first; any ally's loaded squads nearby still go in.
/// </summary>
public sealed class ReinforcementDirector : IDirector
{
    public string Name => "rivalry.reinforce";

    public void Assess(Assessment a, Decisions d)
    {
        if (!a.Options.Reinforcements) return;
        foreach (var town in a.Report.Towns)
        {
            if (!Losing(town, a.Options) || d.Cooling($"town{town.Number}")) continue;
            int ally = a.OtherFactions().Where(a.IsAlly)
                .OrderByDescending(f => a.CampaignFor(f, CampaignKey.DefendTown) is not null).ThenByDescending(a.Relation).ThenBy(f => f)
                .DefaultIfEmpty(-1).First();
            if (ally < 0 || !d.TryClaim($"town{town.Number}", a.Options.ReinforceCooldownHours)) continue;
            d.Reinforce(ally, town.Number);
            if (a.Options.Announce) d.Notice($"{a.Faction(ally)?.Name} answers the call to {town.Name}");
        }
    }

    static bool Losing(TownReport town, RivalryOptions options)
    {
        if (!town.UnderAttack) return false;
        bool halfDown = town.Defenders.Count > 0 && town.DefendersDown * 2 >= town.Defenders.Count;
        return town.Balance < options.ReinforceBelowBalance || halfDown;
    }
}

/// <summary>
/// A faction whose grudge against a standing player character reaches <see cref="RivalryOptions.HuntThreshold"/> sends hunters after them, largest grudge first,
/// at most <see cref="RivalryOptions.MaxHuntsPerAssessment"/> per assessment and once per faction and character per cooldown. Allies never hunt.
/// </summary>
public sealed class GrudgeDirector : IDirector
{
    public string Name => "rivalry.grudge";

    public void Assess(Assessment a, Decisions d)
    {
        if (!a.Options.Hunters) return;
        var grudges = a.Notoriety.Entries
            .Where(g => g.Value >= a.Options.HuntThreshold && !a.IsAlly(g.Key.Faction) && a.Report.Find(g.Key.Who) is { Down: false })
            .OrderByDescending(g => g.Value).ThenBy(g => g.Key)
            .ToList();
        int sent = 0;
        foreach (var (key, _) in grudges)
        {
            if (sent >= a.Options.MaxHuntsPerAssessment) break;
            // Keyed by name, not slot: slots change when a save loads, and the cooldown comes back with it.
            if (!d.TryClaim($"{key.Faction}:{a.Report.Find(key.Who)?.Name}", a.Options.HuntCooldownHours)) continue;
            d.Hunt(key.Faction, key.Who, a.Options.HuntSquads, hours: a.Options.HuntHours);
            if (a.Options.Announce) d.Notice($"{a.Faction(key.Faction)?.Name} wants {a.Report.Find(key.Who)?.Name} dead");
            sent++;
        }
    }
}

/// <summary>
/// A hostile faction raids a player town that is not under attack, holds at least <see cref="RivalryOptions.RaidWealthMin"/> and more than
/// <see cref="RivalryOptions.RaidValuePerDefence"/> per point of defence, with <see cref="RivalryOptions.RaidChancePerDay"/> spread over the assessments; the most
/// hostile faction with an assault campaign is asked, once per town per cooldown. The campaign then follows the game's own request rules.
/// </summary>
public sealed class OpportunistDirector : IDirector
{
    public string Name => "rivalry.opportunist";

    public void Assess(Assessment a, Decisions d)
    {
        if (!a.Options.OpportunistRaids) return;
        float chance = a.Options.RaidChancePerDay * a.Options.AssessEveryHours / 24f;
        foreach (var town in a.Report.Towns)
        {
            if (!Tempting(a, town) || d.Cooling($"town{town.Number}") || a.Roll($"raid{town.Number}") >= chance) continue;
            int raider = a.OtherFactions().Where(f => a.IsHostile(f) && a.CampaignFor(f, CampaignKey.AssaultTown) is not null)
                .OrderBy(a.Relation).ThenBy(f => f).DefaultIfEmpty(-1).First();
            if (raider < 0 || !d.TryClaim($"town{town.Number}", a.Options.RaidCooldownHours)) continue;
            d.Raid(raider, town.Number);
            if (a.Options.Announce) d.Notice($"{a.Faction(raider)?.Name} has its eye on {town.Name}");
        }
    }

    /// <summary>The town's containers and its people's belongings, against its defence.</summary>
    static bool Tempting(Assessment a, TownReport town)
    {
        if (town.UnderAttack) return false;
        long value = town.StoredValue + a.Report.Characters.Where(c => c.Town == town.Number).Sum(c => c.GearValue + c.CarriedValue);
        return value >= a.Options.RaidWealthMin && value > a.Options.RaidValuePerDefence * Math.Max(1, town.Defence);
    }
}
