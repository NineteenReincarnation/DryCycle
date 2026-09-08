# DryCycle Project Progress

Last updated: 2026-09-08
Active workstream: Desert Batfly Task14 architecture refactor
Current branch: `task14-r6-b1-final`
Current verified implementation baseline before B45 checkpoint: `71f53f3b29fbd83b658bd0474975215e300bd263`

## Current architecture state

Task14 R0-R5 are code-side complete. R6 remains **open** for further planned work, but the currently scheduled domain/type/file migration and responsibility-consolidation batches through B45 have reached a stable checkpoint.

The current R6 baseline now includes:

- Integration identities: `DB_RainWorldHooks`, `DB_RuntimePatch`, `DB_Sandbox`, `DB_WarpCompatibility`.
- Core/runtime identities: `DB_Creature`, `DB_AI`, `DB_Runtime`, `DB_Definition`, `DB_Emergence`.
- Event/context/ownership foundations: `DB_EventHub`, `DB_RoomContext`, `DB_FrameContext`, `DB_BehaviorArbiter`, `DB_FlightMotor`.
- World domains: Colony, Travel and Environment formal owners.
- Behavior domains: Combat, Injury, Social, Threat, Signals, Fear and Vengeance formal owners.
- Presentation/debug domain identities and current Observatory enrichment chain.
- Production Desert Batfly Task-number terminology exit.
- Managed Desert Batfly test suite migration from Task-number identities to domain identities.
- Removal of root-level historical DesertBatfly production `.cs` files and internal `DesertBatflyXxx` declarations.

## Recent R6 batches

### B43 — managed regression modernization

- Historical Task-number test filenames/entry points were replaced by domain suite names.
- Positive reflection probes now target current production owners rather than deleted bridges or stale owner names.
- Threat tactics tests were rewritten to protect current `DB_ThreatRuntime`, `DB_ThreatTactics`, Arbiter, FlightMotor and Vengeance boundaries instead of historical bridge absence.
- Managed-test Task-era terminology and stale positive reflection targets were audited.

### B44 — Fear / Vengeance responsibility extraction

`DB_VengeanceRuntime` now owns its real state-machine implementation instead of forwarding into a `partial DB_FearRuntime`.

Behavior-preserving lifecycle design:

- `DB_FearRuntime` still owns the single per-bat `ConditionalWeakTable<DB_Creature, State>` host.
- that host embeds one `DB_VengeanceRuntime.State`;
- Vengeance owns no second weak table;
- Fear collapse, persistent trauma, Reset and Forget therefore still cancel/retire Vengeance synchronously through the same object lifetime;
- Vengeance owns Mode, Participation, group arming, target/leader/supporter state, rescue/contact behavior, cancellation and owner-gated FlightMotor execution.

B44 validation froze Fear/Vengeance tuning constants, critical gameplay-call counts, switch/case state-machine structure and runtime string payloads. Candidate and clean-promotion workflows passed.

### B45 — architecture consolidation checkpoint

B45 does **not** close R6. It consolidates the architecture already completed so later R6 work cannot accidentally regress it.

Current B45 work:

- modernize `Program.ArchitectureDomainMigration.cs` to test the current formal owner set instead of only early B1 identities;
- install `.github/workflows/desertbatfly-r6-architecture-guard.yml` as a long-lived read-only aggregate guard;
- retire obsolete write-oriented migration facilities once the aggregate guard is verified;
- refresh R6 documentation to current state without declaring the phase closed.

The aggregate guard protects:

- R5 bridge-retention boundaries;
- R6 source-retention boundaries;
- production and managed-test Task-era terminology exit;
- `DB_` production filenames/internal identities and absence of root-level source;
- current positive managed reflection targets;
- formal owner presence;
- Fear/Vengeance single-host ownership;
- external creature/save/world compatibility literals;
- Roslyn syntax parsing for Desert Batfly production and managed-test C#.

## Compatibility identities intentionally unchanged

R6 internal naming work does not change published/game-data identities:

- CreatureTemplate identity: `DesertBatfly`
- Sandbox unlock identity: `DesertBatfly`
- primary state key: `DCDesertBatflyV1`
- Threat memory key: `DCDesertBatflyThreatV1`
- colony persistence prefix: `DCBATCOLONY09<svB>`
- authored room tag: `DESERTSWARMROOM`

The numeric `09` in `DCBATCOLONY09<svB>` is a persisted protocol identifier, not Task architecture terminology, and remains intentionally unchanged.

## Validation boundary

The current CI/environment can execute source-retention guards and Roslyn syntax validation, but it does not provide the developer-local Rain World/BepInEx runtime assembly set or a running Rain World process.

Therefore the following are **not** claimed as completed by this checkpoint:

- developer-local full net48 build against the exact game assemblies;
- execution of the managed integration binary against those assemblies;
- Rain World save/load compatibility testing;
- live scenario regression;
- 20-30 Desert Batfly stress/performance profiling;
- live Observatory review.

These remain future validation/work items and do not imply R6 is closed.

## R6 status

**R6 remains open.**

The current architecture migration baseline is stable and guarded, but additional R6 work may be added according to subsequent project arrangements. Do not advance the project status to R7 or write `FINAL_REPORT.md` unless explicitly requested after the remaining R6 work and later acceptance stages are defined.
