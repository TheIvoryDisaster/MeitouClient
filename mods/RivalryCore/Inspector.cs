using System.Numerics;
using Meitou.Data;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Stat = Meitou.Data.Gameplay.Bodies.StatsEnumerated;

namespace Rivalry;

/// <summary>
/// Builds a <see cref="PlayerReport"/> from the world in a serial step (docs/modding-rivalry.md "Inspection"). Reads only; every walk is in slot, town or
/// registry order, so the report is the same on any thread count.
/// </summary>
public sealed class Inspector(PopulationData data)
{
    /// <summary>Building <c>function</c> numbers the report counts (docs/game/factions-squads-towns.md "Campaigns", town level).</summary>
    public const int ResearchFunction = 3, TurretFunction = 12, GateFunction = 21;

    /// <summary>The strength a manned turret adds to its town's defence (an engine choice of this framework).</summary>
    public const float TurretDefence = 15;

    /// <summary>The rating used for a character without stats (an engine choice).</summary>
    public const float DefaultStrength = 10;

    readonly Dictionary<string, int> _values = new(StringComparer.Ordinal);

    public PlayerReport Inspect(World world)
    {
        int player = world.Player.Faction;
        if (player < 0) return PlayerReport.Empty with { Tick = world.Tick, GameHours = world.GameHours };

        var towns = world.Buildings.Towns.Where(t => t.Owner == player).ToList();
        var characters = InspectCharacters(world, player, towns);
        var townReports = towns.Select(t => InspectTown(world, player, t, towns, characters)).ToList();
        return new PlayerReport(world.Tick, world.GameHours, player, world.Player.Money, characters, townReports, InspectCampaigns(world));
    }

    /// <summary>
    /// A rating of a character's fighting power: the mean of melee attack, melee defence, dexterity and toughness, 0 while down. Coarse on purpose; a
    /// director wanting more reads the stats itself.
    /// </summary>
    public static float StrengthOf(CharacterCold c)
    {
        if (c.Medical is { Incapacitated: true }) return 0;
        if (c.Stats is not { } s) return DefaultStrength;
        return (s[Stat.MeleeAttack] + s[Stat.MeleeDefence] + s.Dexterity + s.Toughness) / 4;
    }

    List<CharacterReport> InspectCharacters(World world, int player, List<PlayerTown> towns)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var list = new List<CharacterReport>();
        for (int slot = 0; slot < prev.Length; slot++)
        {
            if (!prev[slot].Alive || table.Cold(slot) is not { } c || c.Faction != player || c.Medical is { Dead: true }) continue;
            list.Add(Describe(table.IdOf(slot), c, prev[slot].Position, towns));
        }
        return list;
    }

    CharacterReport Describe(CharacterId id, CharacterCold c, Vector3 at, List<PlayerTown> towns)
    {
        var (gear, carried) = InventoryValue(c.Inventory);
        bool down = c.Medical is { Incapacitated: true };
        return new CharacterReport(id, c.Name, c.SquadId, at, TownAt(towns, at), down, c.InCombat, StrengthOf(c), gear, carried);
    }

    TownReport InspectTown(World world, int player, PlayerTown town, List<PlayerTown> towns, List<CharacterReport> characters)
    {
        var (buildings, turrets, gates, research) = CountBuildings(world, town.Number);
        var defenders = characters.Where(c => c.Town == town.Number).ToList();
        var (hostiles, threat) = FindHostiles(world, player, town, towns);
        float defence = defenders.Sum(c => c.Strength) + turrets * TurretDefence;
        return new TownReport(town.Number, town.Name, town.Position, town.Radius, buildings, turrets, gates, research, StoredValue(world, player, town, towns),
            [.. defenders.Select(c => c.Id)], defenders.Count(c => c.Down), defence, hostiles, threat);
    }

    (int Buildings, int Turrets, int Gates, int Research) CountBuildings(World world, int town)
    {
        int buildings = 0, turrets = 0, gates = 0, research = 0;
        foreach (var b in world.Buildings.All)
        {
            if (b.Town != town || !b.Complete || b.Destroyed || b.Ruined) continue;
            buildings++;
            var (function, level) = FunctionOf(b.Record);
            if (function == TurretFunction) turrets++;
            else if (function == GateFunction) gates++;
            else if (function == ResearchFunction) research = Math.Max(research, level);
        }
        return (buildings, turrets, gates, research);
    }

    (int Function, int TechLevel) FunctionOf(string record)
    {
        if (data.Db.Find(record)?.GetReferences("functionality") is not [var link, ..] || data.Db.Find(link.TargetStringId) is not { } f) return (0, 0);
        return (f.GetInt("function"), f.GetInt("tech level"));
    }

    (List<CharacterId> Hostiles, float Threat) FindHostiles(World world, int player, PlayerTown town, List<PlayerTown> towns)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var hostiles = new List<CharacterId>();
        float threat = 0;
        for (int slot = 0; slot < prev.Length; slot++)
        {
            if (!prev[slot].Alive || table.Cold(slot) is not { } c || TownAt(towns, prev[slot].Position) != town.Number) continue;
            if (!IsHostile(world, player, c) || c.Medical is { Incapacitated: true }) continue;
            hostiles.Add(table.IdOf(slot));
            threat += StrengthOf(c);
        }
        return (hostiles, threat);
    }

    /// <summary>Hostile to the player: its faction's relation is at or below the hostile threshold, or its squad assaults the player (a campaign's war targets).</summary>
    bool IsHostile(World world, int player, CharacterCold c)
    {
        if (c.Faction == player || c.Faction < 0) return false;
        if (data.Relations.Contains(c.Faction) && data.Relations.IsHostile(c.Faction, player)) return true;
        return world.Squads.Find(c.SquadId) is { War: { IsSet: true } war } && (war.Everyone || war.Faction == player);
    }

    long StoredValue(World world, int player, PlayerTown town, List<PlayerTown> towns)
    {
        long total = 0;
        foreach (var f in world.Fixtures.All)
            if (f.Contents is { } contents && f.Owner == player && TownAt(towns, f.Position) == town.Number) total += Value(contents.All());
        return total;
    }

    List<CampaignReport> InspectCampaigns(World world)
    {
        var list = new List<CampaignReport>();
        if (world.Systems.OfType<PopulationSystem>().FirstOrDefault() is not { } population) return list;
        foreach (var war in population.Wars)
            foreach (var c in war.Actives)
            {
                if (c.Target.PlayerTown <= 0) continue;
                bool hostile = population.CampaignOf(c.CampaignId)?.IsHostile ?? false;
                list.Add(new CampaignReport(c.Faction, c.CampaignId, c.Target.PlayerTown, c.Phase, hostile));
            }
        return list;
    }

    (long Gear, long Carried) InventoryValue(Inventory? inventory)
    {
        if (inventory is null) return (0, 0);
        long gear = Value(inventory.Items.Where(i => InventoryCommands.IsEquipmentSection(i.Section)));
        return (gear, Value(inventory.All()) - gear);
    }

    long Value(IEnumerable<ItemInstance> items)
    {
        long total = 0;
        foreach (var item in items) total += (long)ValueOf(item.Record) * Math.Max(1, item.Quantity);
        return total;
    }

    int ValueOf(string record)
    {
        if (!_values.TryGetValue(record, out int value)) _values[record] = value = data.Db.Find(record)?.GetInt("value") ?? 0;
        return value;
    }

    /// <summary>The number of the nearest player town whose radius holds the point (ground plane); 0 for none.</summary>
    public static int TownAt(IReadOnlyList<PlayerTown> towns, Vector3 at)
    {
        int best = 0;
        float bestDistance = float.MaxValue;
        foreach (var t in towns)
        {
            float d = Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(t.Position.X, t.Position.Z));
            if (d <= t.Radius && d < bestDistance) (best, bestDistance) = (t.Number, d);
        }
        return best;
    }
}
