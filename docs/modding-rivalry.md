# Rivalry Core (framework mod)

**Status: proposed, mod version 0.1.0, code-mod API 2.** A code mod in `mods/RivalryCore` (id `rivalry.core`) that lets the world's factions
inspect the player's setup and act on it: hunt a character, raid a weak rich town, reinforce a town that is losing a fight. Not rival-only: the
same pipeline sends allies to help. Built on [dependencies](modding-dependencies.md); other mods add their own rules as directors.

## Pipeline

At each assessment (every `AssessEveryHours` game hours, in the system's `SlowWorld` at `SystemAnchor.End`), all serial:

1. **Inspect** (`Inspector`) the player faction into an immutable `PlayerReport`.
2. **Decide**: each `IDirector` reads an `Assessment` and records `Decision`s; built-ins first, then dependents' in the order added.
3. **Act** (`Actions`): decisions are carried out in order through the game's own mechanisms. Each yields a log line (`History`, the game log).

A throwing director is logged and skipped for the rest of that world; a failing decision is logged and the rest go on.

## Inspection

| Report | Contents |
| --- | --- |
| `CharacterReport` | each living player-faction character: id, name, squad, position, town (nearest player town whose radius holds it), down, in combat, strength, gear value (equipment sections), carried value (the rest, bags included) |
| `TownReport` | each player town: finished buildings, turrets (function 12), gates (21), highest research `tech level` (3), value in player-owned containers, defenders (player characters in it), defenders down, defence, hostiles standing in it, threat; `UnderAttack`, `Balance` = defence / threat |
| `CampaignReport` | running campaigns aimed at a player town: faction, FACTION_CAMPAIGN id, town, phase, hostile |
| `PlayerReport` | the above, money, `Wealth`, `Strongest` |

Engine choices (framework's own, not the original's): strength = mean of melee attack, melee defence, dexterity, toughness (10 without stats, 0
while down); defence = defenders' strength + 15 per turret; hostile = faction relation ≤ -30 with the player, or a squad whose campaign war
targets include the player. Item value = the record's `value` × quantity. Function numbers: [campaigns](game/factions-squads-towns.md#campaigns).

## Notoriety

`NotorietyLedger`: grudge per (faction, character). Fed by `ICombatHitObserver`: a player-faction attacker landing a hit on another faction's
member adds `HitGrudge` (1), plus `DownedHitGrudge` (3) if the victim was already down; the victim dying within `KillWindowHours` (1) adds
`KillGrudge` (25). Halves every `GrudgeHalfLifeDays` (4); below 0.5 or once the character leaves the table it is dropped. Directors and dialogue
add to it too. A character's notoriety is the sum over factions.

## Built-in directors

Each is a settings switch; cooldowns are per director and key.

| Director | When | Decision |
| --- | --- | --- |
| `rivalry.reinforce` | a town is under attack and `Balance` < `ReinforceBelowBalance` (0.75) or half its defenders are down | `Reinforce` by the closest ally (relation ≥ 50; allies with a DEFEND_TOWN campaign first); once per town per 12 h |
| `rivalry.grudge` | a non-allied faction's grudge on a standing character ≥ `HuntThreshold` (60) | `Hunt` with `HuntSquads` squads; largest first, at most 2 per assessment, once per faction and character name per 48 h |
| `rivalry.opportunist` | a town not under attack holds ≥ `RaidWealthMin` (20000) and > 400 per point of defence; roll < `RaidChancePerDay` × hours / 24 | `Raid` by the most hostile faction that has an assault campaign; once per town per 96 h |

## Actions

| Decision | Carried out by |
| --- | --- |
| `Raid(faction, town, campaign?)` | `PopulationSystem.RequestCampaign` with an ASSAULT_TOWN campaign: the given one, else the faction's first hostile one (its `campaigns` list, then records whose `faction override` names it). The game's request rules then apply: world states and leader only, no chance, range or cooldown ([campaigns](game/factions-squads-towns.md#campaigns)) |
| `Reinforce(faction, town, campaign?, nearby)` | the same with a DEFEND_TOWN campaign (the "Ally ... (trigger)" records carry a `faction override`), and with `nearby` each of the faction's candidate squads within `ReinforceRadius` (8000) marks up to 16 of the town's hostiles TEMP_ENEMY and runs the defend package (`DefendPackage`, else the first AI_PACKAGE by id with signal 15, `Contract_DefendPlayerBase`) for `ReinforceHours` |
| `Hunt(faction, target, squads, package?)` | the nearest candidate squads within `HuntRadius` (20000) mark the target TEMP_ENEMY (squad memory, so the aggro fights it on sight) and, given a package (`HuntPackage`), run it as a contract aimed at the target |
| `Direct(squad, package, hours, target?)` | `StartContract` (dialogue's `AI contract`), deferred to the next tick |
| `ShiftRelation`, `Grudge`, `Notice`, `Custom` | `PlayerRelations.Add`; the ledger; `World.Post`; the director's own serial code |

Candidate squad: of the faction, not the player's, not dead, leader alive, in no campaign, not a town's own squad unless `UseTownSquads`; nearest
leader first, ties by id.

**Unknown**: which vanilla AI_PACKAGE makes a squad travel to a contract's mission target and attack it; so `HuntPackage` has no default and a
hunt without one only marks the target. A dependent (e.g. a rival faction mod) ships that package as data. Whether `Contract_DefendPlayerBase`
walks a squad into the town from 8000 away is **Unknown** until tried in play.

## Writing a director

```csharp
public void Initialize(ModContext c)
{
    var rivalry = c.Dependency<IRivalryApi>("rivalry.core")!;
    rivalry.Options.HuntPackage = "1234-nemesis.mod";             // our AI_PACKAGE
    rivalry.AddDirector(new AssassinDirector());                   // before the first world
}

sealed class AssassinDirector : IDirector
{
    public string Name => "nemesis.assassins";
    public void Assess(Assessment a, Decisions d)
    {
        if (a.Report.Strongest is { } top && a.Roll("assassin") < 0.1f && d.TryClaim("strongest", 72))
            d.Hunt(a.Data.FactionIndex("1234-nemesis.mod"), top.Id, squads: 2);
    }
}
```

- Read only; randomness from `Assessment.Roll(key)`; no world state of your own (use `TryClaim`/`Cooling`, which are hashed and saved).
- `IRivalryApi.In(world)` gives the report, ledger and history to a dependent's own systems (serial steps only).

## Dialogue codes

| Code | Kind | Meaning |
| --- | --- | --- |
| 1600 | condition | grudge of the speaker's faction against the subject, compared with the value |
| 1601 | condition | the subject's notoriety, compared with the value |
| 1600 | effect | the speaker's faction's grudge against the target changes by the value |

## Options

The switches, assessment period, half-life, hunt threshold, reinforce balance, raid chance and `Announce` (a message per decision) are on the Mod
settings page. Everything else in `RivalryOptions` is for dependents. The system copies the options in `Inputs` each tick.

## Save data

Sidecar system `rivalry`, version 1: next/last assessment hours, grudges by faction string id and character name + record id (slots change on
load; restored to the first living match in slot order at the first step after the load, unmatched ones dropped), cooldowns. Kill watches are not
saved. A renamed character loses its grudges across a save made before the rename.

## Debug rows (F10)

`Rivalry: report` (characters, wealth, grudges, towns, last decision), `Rivalry: assess now`, `Rivalry: grudge on selected` (field:
`faction name or id[, amount]`, default 100).

## Building

`dotnet build mods/RivalryCore` writes `RivalryCore.dll` and `meitou.json`; the game assemblies are referenced with `Private="false"`. Copy both
into `Kenshi/mods/RivalryCore/` beside an empty `RivalryCore.mod` made in FCS, enable it in `mods.cfg` above its dependents, and allow it in the
launcher.

## Evidence

**Verified** only by build (2026-11-06, Linux x64, .NET SDK 10.0.114) and a scratch run kept out of the repo: an outmatched town made an ally
send a nearby squad, a grudge sent a hunter, grudges survived a save round trip, and the world hash matched on 1 and 4 threads. **Unknown**:
everything in a real game (campaign starts from `RequestCampaign`, contract packages, launcher), and the `meitou-tools mod-check` result.
