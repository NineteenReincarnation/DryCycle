# DryCycle Project Progress

Last updated: 2026-09-08
Active workstream: Desert Batfly Task14 architecture refactor
Current branch: `task14-r6-b1-final`
Current verified implementation HEAD before this progress update: `d81bfc7987e430f12326fa80e9f5d88ed89a37c8`

## Start-of-run state

- Previous verified R6 implementation had completed B1 Integration/Observatory and B2 Colony migration.
- Previous progress HEAD: `42a7beec7064a06a2fff942e7f1151d112d675dd`.
- R5 stable baseline remains `e7bb53e32b367fec91805c180151a36a75b68816`.
- `main` remains behind the stacked Task14 refactor work; active work continues on the Task14 branch.
- Full local Rain World build/live validation is still unavailable in this execution environment.

## Completed in this run

### R6-B3 Travel support domain migration

Verified implementation commit:

`580ff8c4c8186716a06c633c134d5d4852064d56` — `R6 B3 migrate Travel support identities`

Moved/renamed:

- `src/Creatures/DesertBatfly/DesertBatflyRefuge.cs` -> `src/Creatures/DesertBatfly/Travel/DB_RefugePolicy.cs`
- `src/Creatures/DesertBatfly/DesertBatflyWorldRoutePlanner.cs` -> `src/Creatures/DesertBatfly/Travel/DB_WorldRoutePlanner.cs`

Supporting type identities migrated in the same cohesive Travel batch:

- `DesertBatflyRefugeTarget` -> `DB_RefugeTarget`
- `DesertBatflyWorldRoute` -> `DB_WorldRoute`
- `DesertBatflyWorldRoutePlanner` -> `DB_WorldRoutePlanner`
- `DesertBatflyTravelPurpose` -> `DB_TravelPurpose`
- `DesertBatflyRefuge` -> `DB_RefugePolicy`

No route/refuge algorithm redesign was performed. The existing weighted bounded route planner, refuge scoring, Environment policy boundary and Task09 travel semantics were retained.

### R6-B4 Platform Roost migration

Verified implementation commit:

`d81bfc7987e430f12326fa80e9f5d88ed89a37c8` — `R6 B4 migrate Platform Roost identity`

Moved/renamed:

- `src/Creatures/DesertBatfly/DesertBatflyPlatformRoostRuntime.cs` -> `src/Creatures/DesertBatfly/Roost/DB_PlatformRoostRuntime.cs`
- `DesertBatflyPlatformRoostRuntime` -> `DB_PlatformRoostRuntime`

The existing Rain World `FlyAI.ChainTile` extension behavior was preserved: vanilla chain validity is checked first, the extra rule remains Desert-Batfly-only, five-tile solid/water clearance remains required, and hanging remains limited to the underside of one-way Floor tiles.

## Self-review findings and fixes

1. The first B3 migration execution reached the renamed files successfully but failed the existing R5 retention guard.
2. Root cause: the guard had followed the `DesertBatflyRefuge` type rename but still referenced a now-invalid root path (`src/Creatures/DesertBatfly/DB_RefugePolicy.cs`) instead of the physical Travel path.
3. This was validation-infrastructure path drift, not a behavior regression. The guard was updated to `src/Creatures/DesertBatfly/Travel/DB_RefugePolicy.cs`; no assertion was removed or weakened.
4. B3 was re-run from the unmodified pre-migration source state and then passed all existing and new checks.
5. B3 diff review showed the large line-count changes in `DesertBatflyTravelNavigation.cs` are identifier substitutions only; the TravelNavigation responsibility-heavy file was deliberately not physically renamed or split in this batch.
6. B4 was selected as the next small low-risk unit because the migration manifest marks PlatformRoostRuntime KEEP/RENAME and its lifecycle/behavior contract can be statically guarded without redesign.

## Validation performed

### R6-B3

- GitHub Actions: `Task14 R6 B3 Travel support migration`, run `34147710304` — **SUCCESS**.
- `bash scripts/check-desertbatfly-r5-retention.sh` — passed after updating the guard path to the new Travel ownership.
- `bash scripts/check-desertbatfly-r6-b1.sh` — passed.
- Old production names `DesertBatflyRefuge`, `DesertBatflyRefugeTarget`, `DesertBatflyWorldRoute`, `DesertBatflyWorldRoutePlanner`, and `DesertBatflyTravelPurpose` were checked absent from `src/*.cs`.
- New Travel files/types were checked present.
- `RefugeMaxHops = 3`, `MigrationMaxHops = 5`, `MinimumRefugeQuality = 0.62f`, and the 600-tick safety margin were checked retained.
- Sandstorm Environment-policy call sites remained present in `DB_RefugePolicy`.
- `git diff --check` passed.
- Compare from the prior progress HEAD to B3 showed expected Travel identity/reference changes only; no route/refuge algorithm replacement was introduced.

### R6-B4

- GitHub Actions: `Task14 R6 B4 Roost migration`, run `34147755095` — **SUCCESS**.
- Existing R5 retention audit — passed.
- Existing R6 B1 retention audit — passed.
- Old `DesertBatflyPlatformRoostRuntime` production identity was checked absent.
- `DB_PlatformRoostRuntime` file/type was checked present.
- `On.FlyAI.ChainTile` Enable/Disable subscription symmetry was checked.
- Vanilla-first `orig(self, testTile)` behavior was checked.
- Desert-Batfly-only gating, five-tile clearance loop, and `Floor` platform condition were checked.
- `DB_RainWorldHooks` lifecycle references were checked updated.
- `git diff --check` passed.

Not executed in this environment:

- Full `.NET Framework 4.8` build against developer-local Rain World assemblies.
- Managed DesertBatfly integration suite requiring Rain World/BepInEx DLLs.
- Rain World live scenarios and 20–30 Desert Batfly performance acceptance.

No pass result is claimed for those deferred validations.

## Important files changed

### B3

- `src/Creatures/DesertBatfly/Travel/DB_RefugePolicy.cs`
- `src/Creatures/DesertBatfly/Travel/DB_WorldRoutePlanner.cs`
- `src/Creatures/DesertBatfly/DesertBatflyTravelNavigation.cs` (identifier references only)
- `src/Creatures/DesertBatfly/Colony/DB_ColonyRuntime.cs` (identifier references only)
- `src/Creatures/DesertBatfly/Runtime/DB_FrameContext.cs` (identifier references only)
- `src/Creatures/DesertBatfly/Environmental/DB_EnvironmentalPolicy.cs` (identifier references only)
- `src/Creatures/DesertBatfly/Integration/RainWorld/DB_RainWorldHooks.cs` (identifier references only)
- `src/Debug/AIDebugger/Sources/DB_TravelDebugSource.cs` (identifier references only)
- `scripts/check-desertbatfly-r5-retention.sh`
- affected Task09/Task14 retention tests.

### B4

- `src/Creatures/DesertBatfly/Roost/DB_PlatformRoostRuntime.cs`
- references in Integration/tests/scripts as applicable.

### Progress/status

- `docs/Discussion/Task_14_R6_DomainMigrationStatus.txt`
- `PROGRESS.md`

## Task14 progress review

- R0 baseline/regression freeze: code-side complete; local managed validation pending.
- R1 event foundation: code-side complete; live validation pending.
- R2 RoomContext/perception: code-side complete; live/performance validation pending.
- R3 FrameContext/Arbiter: code-side complete; live validation pending.
- R4 FlightMotor/AI responsibility split: code-side complete; live validation pending.
- R5 internal Bridge/Detour/Reflection debt cleanup: code-side complete; live validation pending.
- R6 domain/type/file migration: **in progress**.
  - B1 Integration + Observatory identity migration: complete code-side and source-guard verified.
  - B2 Colony domain migration: complete code-side and source-guard verified.
  - B3 Travel support migration: complete code-side and source-guard verified.
  - B4 Platform Roost migration: complete code-side and source-guard verified.
  - TravelNavigation responsibility-aware split/rename: not complete.
  - Production-wide Task09-Task13 identifier/text sweep: not complete.
  - Threat domain migration: not complete.
  - Signals domain migration: not complete.
  - Environment domain migration: not complete.
  - remaining Social/Roost migration: not complete.
  - Injury/Fear/Vengeance/Core/Presentation remaining identities: not complete.
  - final old-root-file/dead-code cleanup and final R6 naming/path guard: not complete.
- R7 performance/debug/full regression/final acceptance: not started as a formal closeout stage.

## Current blockers

- Full build/managed tests require developer-local Rain World/BepInEx assemblies referenced by the project.
- Final live acceptance requires Rain World itself.

Neither blocker prevents continued R6 source-level migration and static regression work.

## Recommended next hour

Continue from the verified B4 state. Do not mechanically rename `DesertBatflyTravelNavigation.cs`; it is explicitly SPLIT-heavy in the migration manifest and needs responsibility review before physical migration.

Recommended next low-risk batch is Presentation support:

1. inspect `DesertBatflySandBurst.cs` and, if its responsibility remains purely visual, migrate it to `Presentation/DB_SandBurst.cs` with reference-only changes;
2. separately review `DesertBatflyGraphics.cs` before renaming because it owns a larger Rain World graphics surface;
3. alternatively, if Presentation coupling is larger than expected, migrate another manifest KEEP/RENAME leaf such as `DesertBatflySocialBond.cs` only after checking save/state identity implications;
4. after each batch run the R5 retention audit, the R6 retention audit, a domain-specific absence/contract guard, and `git diff --check`.
