# DryCycle Project Progress

Last updated: 2026-09-08
Active workstream: Desert Batfly Task14 architecture refactor
Current branch: `task14-r6-b1-final`
Current verified implementation HEAD before this progress update: `3e6837b7997698926ffd24f774619388be769362`

## Current architecture state

Task14 R0-R5 are code-side complete on the stacked refactor history. R6 domain/type/file migration remains in progress. Full Rain World build/live acceptance is still pending because this execution environment does not contain the developer-local Rain World/BepInEx assemblies or game runtime.

Completed R6 batches:

- B1: Integration + Observatory identities.
- B2: Colony domain identities.
- B3: Travel support identities (`DB_RefugePolicy`, `DB_WorldRoutePlanner` and supporting types).
- B4: Platform Roost identity (`DB_PlatformRoostRuntime`).
- B5: Presentation SandBurst (`DB_SandBurst`).
- B6: SocialBond (`DB_SocialBond`).
- B7: Presentation Graphics (`DB_Graphics`).
- B8: Threat persistent memory (`DB_ThreatMemory*`).
- B9: Threat tactics (`DB_ThreatTactics`, `DB_ThreatTacticalProfile`).
- B10: historical Task wording cleanup in Threat trace and Environment profile.
- B11: Environment Exposure/Profile physical domain migration; filenames moved to `Environment/DB_EnvironmentExposure.cs` and `Environment/DB_EnvironmentProfile.cs` with source blobs unchanged. Internal long type names remain pending a later caller-safe rename.
- B12: Emergence physical domain migration to `Runtime/DB_Emergence.cs`; source body unchanged, internal type rename still pending.
- B13: Definition physical domain migration to `Core/DB_Definition.cs`; source body unchanged, internal type rename still pending.

## This run: R6-B12/B13 Runtime + Core leaf physical migration

### Start state

- Began from branch HEAD `ce28fdf12e7e4523b9bc5ce037521aee0a85b49e`.
- B11 had completed only physical relocation for Environment Exposure/Profile because safe all-caller type replacement could not be validated in the available environment.
- The same validation limitation remains: no repository-local shell/build path is available for broad type replacement.
- Therefore this run selected only manifest entries whose physical ownership is explicit and whose move can be proven behavior-neutral by exact Git rename comparison.

### Completed work unit 1 — B12 Runtime Emergence move

Moved:

- `src/Creatures/DesertBatfly/DesertBatflyEmergence.cs`
- -> `src/Creatures/DesertBatfly/Runtime/DB_Emergence.cs`

Why this was selected:

- Task14 migration manifest marks Emergence as `KEEP/RENAME` with target `Runtime/DB_Emergence.cs`.
- Emergence is a coherent special physical owner: emergence collision gating, `MoveFromOutsideMyUpdate`, escape path sampling and quicksand exclusion all belong to the same lifecycle.

Behavior preservation:

- source contents were copied exactly;
- internal type remains `DesertBatflyEmergence` for now, avoiding an unvalidated caller-wide rename;
- Git compare reports the final path change as a rename with **0 additions / 0 deletions**.

### Completed work unit 2 — B13 Core Definition move

Moved:

- `src/Creatures/DesertBatfly/DesertBatflyDefinition.cs`
- -> `src/Creatures/DesertBatfly/Core/DB_Definition.cs`

Why this was selected:

- Task14 migration manifest marks Definition as `KEEP/RENAME` with target `Core/DB_Definition.cs`.
- The file is a stable creature-definition boundary rather than a mixed behavior runtime.

Behavior/compatibility preservation:

- source contents were copied exactly;
- internal type remains `DesertBatflyDefinition` pending caller-safe rename;
- external `CreatureTemplate.Type.value` remains exactly `"DesertBatfly"`;
- template parameters, Fly ancestry and Peach Lizard relationship setup are unchanged;
- Git compare reports the final path change as a rename with **0 additions / 0 deletions**.

Implementation commits ended at:

`3e6837b7997698926ffd24f774619388be769362`

## Self-review and verification

Self-review compare from start-of-run HEAD `ce28fdf12e7e4523b9bc5ce037521aee0a85b49e` to implementation HEAD `3e6837b7997698926ffd24f774619388be769362` contains exactly two effective file changes:

- `DesertBatflyDefinition.cs` -> `Core/DB_Definition.cs`: rename, 0 additions / 0 deletions.
- `DesertBatflyEmergence.cs` -> `Runtime/DB_Emergence.cs`: rename, 0 additions / 0 deletions.

No production logic, constants, save identity, external creature ID, event ownership, flight ownership, emergence timing, collision behavior, terrain sampling, or relationship semantics changed.

No regression was found in the mechanically reviewable diff, so no code repair was required after these moves.

Not executed in this run:

- `scripts/check-desertbatfly-r5-retention.sh` as an actual shell process.
- `scripts/check-desertbatfly-r6-b1.sh` as an actual shell process.
- full `.NET Framework 4.8` build against Rain World assemblies.
- managed DesertBatfly integration tests requiring Rain World/BepInEx DLLs.
- Rain World live scenarios and 20–30 Desert Batfly performance acceptance.

These are **not** reported as passing. This run deliberately used only blob-identical physical moves because those can be proven behavior-neutral without a repository-local build runner.

## Important files changed this run

- `src/Creatures/DesertBatfly/Runtime/DB_Emergence.cs`
- `src/Creatures/DesertBatfly/Core/DB_Definition.cs`
- `docs/Discussion/Task_14_R6_DomainMigrationStatus.txt`
- `PROGRESS.md`

## Remaining Task14 requirements

R6 remains incomplete. Major remaining work:

- pending leaf **C# type identity** renames:
  - `DesertBatflyEnvironmentalExposure` -> `DB_EnvironmentExposure`
  - `DesertBatflyEnvironmentalProfile` -> `DB_EnvironmentProfile`
  - `DesertBatflyEmergence` -> `DB_Emergence`
  - `DesertBatflyDefinition` -> `DB_Definition`
  after a safe all-caller replacement/validation path is available;
- Environment Runtime/RoomState/Core responsibility migration;
- Threat runtime/event responsibility-aware split/absorb review and migration;
- Signals domain migration;
- remaining Social/Roost identities;
- Injury domain migration;
- Fear/Vengeance domain migration;
- Core (`DB_Creature`, state/personality/sex/tuning responsibility review);
- TravelNavigation responsibility-aware migration;
- remaining Presentation/debug identities;
- production-wide Task09-Task13 historical naming cleanup;
- old root-file/dead-code cleanup;
- final R6 naming/path/architecture guard;
- R7 performance/debug/full regression/final acceptance;
- `FINAL_REPORT.md` only after complete acceptance.

## Current blockers

- Full managed build/tests require developer-local Rain World/BepInEx assemblies.
- Final behavior/performance acceptance requires Rain World runtime.
- Current execution environment still lacks a reliable repository-local bulk-edit + shell validation path for caller-wide C# type renames.

These blockers do not prevent additional blob-identical physical moves, narrow text-only cleanup, or responsibility review.

## Recommended next run

1. Re-check whether a repository-local validation path has become available. If yes, finish the four pending leaf C# type identities together with all callers and run R5/R6 guards before accepting them.
2. If bulk caller-safe replacement remains unavailable, inventory the remaining root-level `DesertBatfly*.cs` files and select only another manifest-explicit KEEP/RENAME leaf whose physical target is unambiguous.
3. Do **not** mechanically rename/split `DesertBatflyThreatRuntime.cs` or `DesertBatflyTravelNavigation.cs`; both still require responsibility-aware migration.
4. Continue production-wide Task09-Task13 text cleanup only where each edit is clearly non-semantic and individually reviewable.
