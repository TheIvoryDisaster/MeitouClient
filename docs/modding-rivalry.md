# Rivalry Core (framework mod)

**Status: proposed, mod version 0.2.0, code-mod API 2.** A code mod in `mods/RivalryCore` (id `rivalry.core`) that lets the world's factions
inspect the player's setup and act on it: hunt a character, raid a weak rich town, reinforce a town that is losing a fight. Not rival-only: the
same pipeline sends allies to help and friendly visitors. Built on [dependencies](modding-dependencies.md); other mods add probes, conditions,
target kinds, behaviours, rules and directors (see "Extension API"). Needs the engine hooks of [modding](modding.md) "World hooks".

## Pipeline

At each assessment (every `AssessEveryHours` game hours, default 2, in the system's `SlowWorld` at `SystemAnchor.End`), all serial:

1. **Inspect** (`Inspector`) the player faction into an immutable `PlayerReport`; probes add their numbers.
2. **Decide**: the rules are evaluated in the order added (`RuleEngine`), then each `IDirector` reads the `Assessment`; both record `Decision`s.
3. **Act**: decisions are carried out in order. A `StartDecision` starts a behaviour; the rest go through `Actions`. Each yields a log line
   (`History`, the game log).

Between assessments, every slow step: behaviours under way are stepped (`BehaviourRuns`), then the debug rows' asks run.

A throwing rule or director is logged and off for the rest of that world; a failing decision or run is logged and dropped, the rest go on.

## Inspection

| Report | Contents |
| --- | --- |
| `CharacterReport` | each living player-faction character: id, name, squad, position, town (nearest player town whose radius holds it), down, in combat, strength, gear value (equipment sections), carried value (the rest, bags included); `Value(probe)` |
| `TownReport` | each player town: finished buildings, turrets (function 12), gates (21), highest research `tech level` (3), value in player-owned containers, defenders (player characters in it), defenders down, defence, hostiles standing in it, threat; `UnderAttack`, `Balance` = defence / threat; `Public`, `Beds` (the player's bed fixtures); `Value(probe)` |
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

## Built-ins

Registered under the prefix `rivalry.`; constants in `BuiltIn`. Each rule and the director has a settings switch; cooldowns are per rule or
director and key.

| Kind | Name | Meaning |
| --- | --- | --- |
| target | `rivalry.town` | each player town; key `town{number}` |
| target | `rivalry.character` | each standing player character; key: its name |
| condition | `rivalry.under-attack`, `rivalry.calm` | hostiles are standing in the town, or not |
| condition | `rivalry.losing` | under attack and `Balance` < `ReinforceBelowBalance` (0.75) or half its defenders down |
| condition | `rivalry.tempting` | not under attack, holds ≥ `RaidWealthMin` (20000) and > 400 per point of defence |
| condition | `rivalry.town-public` | the town is open to the public |
| behaviour | `rivalry.raid`, `rivalry.reinforce`, `rivalry.hunt` | the `Raid`, `Reinforce`, `Hunt` actions below; args `campaign`, `delay`, `nearby`, `package`, `squads`, `hours` |
| behaviour | `rivalry.visit` | see "Visit" |
| rule | `rivalry.reinforce` | town `rivalry.losing`: `rivalry.reinforce` by the closest ally (relation ≥ 50; allies with a DEFEND_TOWN campaign first); once per town per 12 h |
| rule | `rivalry.opportunist` | town `rivalry.tempting`, chance `RaidChancePerDay`: `rivalry.raid` by the most hostile faction that has an assault campaign; once per town per 96 h |
| director | `rivalry.grudge` | a non-allied faction's grudge on a standing character ≥ `HuntThreshold` (60): `Hunt` with `HuntSquads` squads; largest first, at most 2 per assessment, once per faction and character name per 48 h |

## Rules

A `Rule`: `Name`, `Target` kind (default `rivalry.town`), named `Conditions` and an optional `When` test, `Behaviour`, `ChancePerDay` (null:
always; spread over the assessments), `CooldownHours`, `Faction` picker (-1 skips the target), `Args`, `Notice`, `Enabled`. Per target, in the
kind's order: not cooling, conditions hold, roll (`Assessment.Roll("{rule}/{key}")`) passes, a faction is found; then the cooldown
`{rule}/{key}` is claimed and the start recorded. Rules are code only; no data file.

## Behaviours

`IBehaviour.Start(context, request)` returns a log line; one that lasts calls `context.Begin(this, request, until)` for a `BehaviourRun`
(id, behaviour, source, key, faction, town, platoon, phase, started, until, args) and is stepped by `Step(context, run)` every slow step until
it returns false. The run is the behaviour's only state. `BehaviourContext`: world, data, report, options, population, `Roll`, `Apply(decision)`.

### Visit

`rivalry.visit` (args: text `template`, a SQUAD_TEMPLATE whose package is a wandering trader's; numbers `hours` 48, `minRadius` 600,
`maxRadius` 1200, `money`): phase 0 `SpawnPlatoon` in the ring round the town, homed at the nearest town site, tried each step for 24 h when no
spot is walkable; phase 1 waits up to 24 h for the platoon to load, then sends `VisitTown` at the next tick; phase 2 ends when the stay + 24 h
has passed or the platoon is gone. **Unknown**: everything in play (see "Evidence"); a town that is not public sends the trader away at once.

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

## Extension API

```csharp
public void Initialize(ModContext c)
{
    var rivalry = c.Dependency<IRivalryApi>("rivalry.core")!;
    var mine = rivalry.For(c.Id);                                   // names must start with "<my id>."
    mine.AddTownProbe("inn.rating", (world, town) => Rating(world, town));
    mine.AddCondition("inn.open", (a, t) => a.Report.TownOf(t.Town)?.Value("inn.rating") > 0);
    mine.AddRule(new Rule
    {
        Name = "inn.travellers",
        Conditions = [BuiltIn.TownPublic, BuiltIn.Calm, "inn.open"],
        Behaviour = BuiltIn.Visit,
        ChancePerDay = _ => 0.5f,
        CooldownHours = _ => 24,
        Faction = (a, _) => a.Data.FactionIndex("1234-inn.mod"),
        Args = (_, _) => new BehaviourArgs().Set("template", "5678-inn.mod").Set("hours", 48),
    });
}
```

| Call | Adds |
| --- | --- |
| `AddTownProbe`, `AddCharacterProbe` | a number per town or character in every report (`Value(name)`; 0 when it throws) |
| `AddCondition` | a named test over the assessment and a target |
| `AddTarget` | a target kind: lists `RuleTarget`s (stable `Key`, town, character, site) in a fixed order |
| `AddBehaviour` | an `IBehaviour` |
| `AddRule` | a `Rule`, evaluated after those added before |
| `AddDirector` | an `IDirector` (`Assess(assessment, decisions)`), asked after the rules; `decisions.Start(behaviour, target, args)` starts a behaviour |

- Registrations only before the first world (in `Initialize`); a duplicate or unprefixed name throws. Rivalry Core implements
  `IModDependents`: a dependent that fails to load has its pieces dropped.
- Read only in probes, conditions, targets and directors; randomness from `Assessment.Roll(key)` / `BehaviourContext.Roll(key)`; no state
  outside cooldowns (`TryClaim`/`Cooling`) and runs, which are hashed and saved.
- `IRivalryApi.In(world)` gives the report, ledger, history and runs to a dependent's own systems (serial steps only).

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

Sidecar system `rivalry`, version 2 (version 1 still loads): next/last assessment hours, grudges by faction string id and character name +
record id (slots change on load; restored to the first living match in slot order at the first step after the load, unmatched ones dropped),
cooldowns; since version 2 the next run id and each run (faction by string id, args, and its platoon as template + position: platoon ids change
on load, so a loaded run takes the nearest platoon of that template and faction no other run holds). Kill watches are not saved. A renamed
character loses its grudges across a save made before the rename.

## Debug rows (F10)

`Rivalry: report` (characters, wealth, grudges, towns, last decision), `Rivalry: assess now`, `Rivalry: grudge on selected` (field:
`faction name or id[, amount]`, default 100), `Rivalry: runs and rules`, `Rivalry: fire rule` (field: `rule[, target key]`; skips conditions,
chance and cooldown), `Rivalry: visit town` (field: `template id, faction[, hours[, town]]`).

## Building

`dotnet build mods/RivalryCore` writes `RivalryCore.dll` and `meitou.json`; the game assemblies are referenced with `Private="false"`. Copy both
into `Kenshi/mods/RivalryCore/` beside an empty `RivalryCore.mod` made in FCS and enable it in `mods.cfg` above its dependents.
Tests: `dotnet test mods/RivalryCore.Tests` (synthetic worlds, no game needed).

## Evidence

**Verified** by `mods/RivalryCore.Tests` (synthetic worlds without a population system): the reinforce rule and its nearby squad, a grudge's
hunt, grudges and cooldowns across a save, a dependent's probe/condition/rule firing once per cooldown, a run across a save, a version 1 save
loading, name prefixes and dropping a mod's pieces, the world hash on 1 and 4 threads. **Unknown**: everything in a real game (campaign starts
from `RequestCampaign`, contract packages, the Visit behaviour end to end, a run's platoon found again after a real load), and the
`meitou-tools mod-check` result.
