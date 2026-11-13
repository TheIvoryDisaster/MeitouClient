using Meitou.Data.Gameplay;

namespace Rivalry;

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
