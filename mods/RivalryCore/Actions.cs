using System.Numerics;
using Meitou.Simulation;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Dialogue;

namespace Rivalry;

/// <summary>
/// Carries out decisions in the framework system's serial step (docs/modding-rivalry.md "Actions") through the game's own mechanisms: campaign requests
/// (<see cref="PopulationSystem.RequestCampaign"/>), AI contracts (<see cref="StartContract"/>, deferred to the next tick), squad memory tags and relations.
/// Each returns a log line saying what happened.
/// </summary>
internal sealed class Actions(PopulationData data, PopulationSystem? population, SquadMemory? memory, NotorietyLedger ledger, Catalogue catalogue)
{
    /// <summary>Hostiles one reinforcing squad marks at most.</summary>
    public const int MaxMarked = 16;

    public string Apply(World world, Decision decision, RivalryOptions options, PlayerReport report) => decision switch
    {
        HuntDecision d => Hunt(world, d, options),
        RaidDecision d => Raid(world, d),
        ReinforceDecision d => Reinforce(world, d, options, report),
        DirectDecision d => Direct(world, d.Squad, d.Package, d.Hours, d.Target),
        RelationDecision d => Relation(world, d),
        GrudgeDecision d => Grudge(d),
        NoticeDecision d => Notice(world, d.Message),
        CustomDecision d => Custom(world, d),
        _ => $"unknown decision {decision.GetType().Name}",
    };

    string Hunt(World world, HuntDecision d, RivalryOptions options)
    {
        if (!world.Characters.TryResolveNext(d.Target, out int slot)) return $"hunt by {FactionName(d.Faction)}: target gone";
        var at = world.Characters.Next[slot].Position;
        var squads = Candidates(world, d.Faction, at, options.HuntRadius, options.UseTownSquads).Take(d.Squads).ToList();
        string? package = d.Package ?? options.HuntPackage;
        foreach (var squad in squads)
        {
            memory?.Flag(world, squad.Id, d.Target, MemoryTag.TempEnemy);
            if (package is not null) Direct(world, squad.Id, package, d.Hours, d.Target);
        }
        return $"hunt: {FactionName(d.Faction)} sends {squads.Count} squad(s) after {world.Characters.Cold(slot)?.Name}";
    }

    string Raid(World world, RaidDecision d)
    {
        string? id = d.CampaignId ?? catalogue.CampaignFor(d.Faction, CampaignKey.AssaultTown);
        if (id is null || population is null) return $"raid: {FactionName(d.Faction)} has no assault campaign";
        bool queued = population.RequestCampaign(world, d.Faction, id, CampaignTown.OfPlayer(d.Town), d.DelayHours);
        return $"raid: {FactionName(d.Faction)} {(queued ? "queued" : "refused")} {id} against town {d.Town}";
    }

    string Reinforce(World world, ReinforceDecision d, RivalryOptions options, PlayerReport report)
    {
        string? id = d.CampaignId ?? catalogue.CampaignFor(d.Faction, CampaignKey.DefendTown);
        bool queued = id is not null && population?.RequestCampaign(world, d.Faction, id, CampaignTown.OfPlayer(d.Town), d.DelayHours) == true;
        int sent = d.Nearby && report.TownOf(d.Town) is { } town ? SendNearby(world, d.Faction, town, options) : 0;
        return $"reinforce: {FactionName(d.Faction)} to town {d.Town}: campaign {(queued ? id : "none")}, {sent} squad(s) nearby";
    }

    int SendNearby(World world, int faction, TownReport town, RivalryOptions options)
    {
        var squads = Candidates(world, faction, town.Position, options.ReinforceRadius, options.UseTownSquads).ToList();
        string? package = options.DefendPackage ?? catalogue.DefendPackage;
        foreach (var squad in squads)
        {
            foreach (var hostile in town.Hostiles.Take(MaxMarked)) memory?.Flag(world, squad.Id, hostile, MemoryTag.TempEnemy);
            if (package is not null) Direct(world, squad.Id, package, options.ReinforceHours, null);
        }
        return squads.Count;
    }

    string Direct(World world, int squad, string package, float hours, CharacterId? target)
    {
        if (data.Ai.Package(package) is null) return $"direct: no AI_PACKAGE {package}";
        world.Defer(new StartContract(squad, package, hours, target) { Tick = world.Tick + 1 });
        return $"direct: squad {squad} runs {package} for {hours} h";
    }

    string Relation(World world, RelationDecision d)
    {
        if (world.Player.Faction < 0 || !data.Relations.Contains(d.Faction)) return "relation: no such faction";
        PlayerRelations.Add(world, data.Relations, d.Faction, world.Player.Faction, d.Delta);
        return $"relation: {FactionName(d.Faction)} {d.Delta:+0.#;-0.#}";
    }

    string Grudge(GrudgeDecision d)
    {
        ledger.Add(d.Faction, d.Who, d.Delta);
        return $"grudge: {FactionName(d.Faction)} {d.Delta:+0.#;-0.#} on {d.Who}";
    }

    static string Notice(World world, string message)
    {
        world.Post(message);
        return $"notice: {message}";
    }

    static string Custom(World world, CustomDecision d)
    {
        d.Apply(world);
        return $"custom: {d.Label}";
    }

    /// <summary>
    /// Squads of the faction that may be sent, nearest first within the radius (ground plane; id order breaks ties): not the player's, not dead, with a
    /// living leader, in no campaign, and not a town's own squad unless allowed.
    /// </summary>
    static IEnumerable<Squad> Candidates(World world, int faction, Vector3 at, float radius, bool townSquads)
    {
        var inCampaign = world.Platoons.All.Where(p => p.Campaign is not null && p.SquadId >= 0).Select(p => p.SquadId).ToHashSet();
        return world.Squads.All
            .Where(s => s.Faction == faction && faction != world.Player.Faction && !s.Dead && (townSquads || !s.TownOwn) && !inCampaign.Contains(s.Id))
            .Select(s => (Squad: s, Distance: LeaderDistance(world, s, at)))
            .Where(x => x.Distance <= radius)
            .OrderBy(x => x.Distance).ThenBy(x => x.Squad.Id)
            .Select(x => x.Squad);
    }

    static float LeaderDistance(World world, Squad squad, Vector3 at)
    {
        if (!world.Characters.TryResolveNext(squad.Leader, out int slot)) return float.MaxValue;
        var p = world.Characters.Next[slot].Position;
        return Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(at.X, at.Z));
    }

    string FactionName(int faction) => faction >= 0 && faction < data.Factions.Count ? data.Factions[faction].Name : $"faction {faction}";
}
