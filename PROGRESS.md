# DryCycle Project Progress

Last updated: 2026-09-08 (Asia/Tokyo)
Active workstream: Desert Batfly Task14 architecture refactor
Current branch: `task14-r6-b1-final`
Current verified implementation HEAD before this progress update: `9ec4b103c307cc6fa8271562870159d8e3fe40f9`

## Start-of-run state

- Task14 R0-R5 code-side refactor work had been completed on stacked branches, with Rain World live validation intentionally deferred to final acceptance.
- R5 stable baseline: `e7bb53e32b367fec91805c180151a36a75b68816`.
- R6 had been started, but B1 Integration/Observatory migration had not yet produced a clean verified commit.
- `PROGRESS.md` did not exist at the start of this run.
- `main` remains behind the stacked Task14 work; current Task14 work is intentionally isolated on refactor branches.

## Completed in this run

### R6-B1 Integration + Observatory identity migration

Verified and accepted:

`2fe1eacec6438749a6958704c23b3a4309bf6f92` — `R6 B1 migrate Integration and Observatory identities`

Integration source identities moved to:

- `src/Creatures/DesertBatfly/Integration/RainWorld/DB_RainWorldHooks.cs`
- `src/Creatures/DesertBatfly/Integration/RainWorld/DB_RuntimePatch.cs`
- `src/Creatures/DesertBatfly/Integration/Sandbox/DB_Sandbox.cs`
- `src/Creatures/DesertBatfly/Integration/Warp/DB_WarpCompatibility.cs`

Observatory source identity chain moved to:

- `DB_ObservatorySource`
- `DB_TravelDebugSource`
- `DB_SocialDebugSource`
- `DB_ThreatDebugSource`
- `DB_SignalDebugSource`
- `DB_EnvironmentDebugSource`

### R6-B2 Colony domain migration

Verified and accepted:

`9ec4b103c307cc6fa8271562870159d8e3fe40f9` — `R6 B2 migrate Colony domain identities`

Moved/renamed without changing migration or persistence algorithms:

- `DesertBatflyColonyState.cs` -> `src/Creatures/DesertBatfly/Colony/DB_ColonyState.cs`
- `DesertBatflyColonyRuntime.cs` -> `src/Creatures/DesertBatfly/Colony/DB_ColonyRuntime.cs`
- `DesertBatflyColonyMigration.cs` -> `src/Creatures/DesertBatfly/Colony/DB_MigrationPolicy.cs`

Production identities now use:

- `DB_ColonyState`
- `DB_ColonyRuntime`
- `DB_MigrationPolicy`

Hard compatibility retained:

- Colony save prefix remains `DCBATCOLONY09<svB>`.
- Colony payload version remains `V1`.
- Existing Task09 migration semantics and Travel/Environment ownership boundaries were not redesigned.

## Self-review findings and fixes

1. The initial B1 attempts failed because the R5 permanent retention guard still hard-coded the pre-R6 Integration paths/type names. This was validation infrastructure drift, not a gameplay regression.
2. The R5 guard was updated to follow the R6 Integration paths and `DB_RuntimePatch` identity while preserving its behavioral/architecture checks.
3. Updating the guard through GitHub Contents API removed its Unix executable bit. CI failed with exit code 126 (`Permission denied`). The executable bit was restored without weakening the guard.
4. The clean B1 finalizer then completed successfully.
5. After B1, a second low-risk unit was selected from the migration manifest. Colony was chosen because its three source files are KEEP/RENAME responsibilities and did not require algorithm redesign.
6. B2 validation explicitly protected the external Colony save prefix/version so the DB_ rename cannot silently invalidate persistence compatibility.

## Validation performed

### R6-B1

- GitHub Actions: `Task14 R6 B1 clean finalizer`, run `34143572208` — **SUCCESS**.
- R5 bridge/debt retention guard passed under the new R6 Integration paths.
- R6 B1 source retention checks passed.
- R6 lifecycle wiring checks passed.
- Observatory enrichment-chain checks passed.
- Diff hygiene check passed before the B1 migration commit.
- R5 -> R6-B1 compare showed only expected Integration/Observatory renames, executable guard/test updates, status, and reference changes.

### R6-B2

- GitHub Actions: `Task14 R6 B2 Colony migration`, run `34143783596` — **SUCCESS**.
- `bash scripts/check-desertbatfly-r5-retention.sh` passed after the Colony physical move.
- `bash scripts/check-desertbatfly-r6-b1.sh` passed after the Colony physical move.
- Old production Colony type identities were checked absent.
- `DCBATCOLONY09<svB>` was checked present.
- `PayloadVersion = "V1"` was checked present.
- New `DB_ColonyState`, `DB_ColonyRuntime`, and `DB_MigrationPolicy` identities were checked present.
- `git diff --check` passed before the verified B2 commit.

Not executed in this environment:

- Full `.NET Framework 4.8` build against the developer-local Rain World assemblies.
- Managed DesertBatfly integration suite requiring Rain World/BepInEx DLLs.
- Rain World live scenarios/performance acceptance.

No pass result is claimed for those deferred validations.

## Important files changed

### B1

- `src/Creatures/DesertBatfly/Integration/RainWorld/DB_RainWorldHooks.cs`
- `src/Creatures/DesertBatfly/Integration/RainWorld/DB_RuntimePatch.cs`
- `src/Creatures/DesertBatfly/Integration/Sandbox/DB_Sandbox.cs`
- `src/Creatures/DesertBatfly/Integration/Warp/DB_WarpCompatibility.cs`
- `src/Debug/AIDebugger/Sources/DB_ObservatorySource.cs`
- `src/Debug/AIDebugger/Sources/DB_TravelDebugSource.cs`
- `src/Debug/AIDebugger/Sources/DB_SocialDebugSource.cs`
- `src/Debug/AIDebugger/Sources/DB_ThreatDebugSource.cs`
- `src/Debug/AIDebugger/Sources/DB_SignalDebugSource.cs`
- `src/Debug/AIDebugger/Sources/DB_EnvironmentDebugSource.cs`
- `src/Debug/AIDebugger/Core/AIDebugRegistry.cs`
- `src/Plugin.cs`
- `scripts/check-desertbatfly-r6-b1.sh`
- `tests/DesertBatfly/Program.Task14R6.cs`

### B2

- `src/Creatures/DesertBatfly/Colony/DB_ColonyState.cs`
- `src/Creatures/DesertBatfly/Colony/DB_ColonyRuntime.cs`
- `src/Creatures/DesertBatfly/Colony/DB_MigrationPolicy.cs`
- references across `src/`, `tests/`, and executable retention guards.

### Shared progress/status

- `scripts/check-desertbatfly-r5-retention.sh`
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
  - Production-wide Task09-Task13 identifier/text sweep: not complete.
  - Travel domain migration: not complete.
  - Threat domain migration: not complete.
  - Signals domain migration: not complete.
  - Environment domain migration: not complete.
  - Social/Roost migration: not complete.
  - Injury/Fear/Vengeance/Core/Presentation remaining identities: not complete.
  - Final old-root-file/dead-code cleanup and final R6 naming/path guard: not complete.
- R7 performance/debug/full regression/final acceptance: not started as a formal closeout stage.

## Current blockers

- Full build/managed tests require developer-local Rain World/BepInEx assemblies referenced by the project.
- Final live acceptance requires Rain World itself.

Neither blocker prevents continued R6 source-level migration and static regression work.

## Recommended next hour

Continue from the current verified R6 branch state. Prefer another low-risk cohesive R6 batch before touching the SPLIT-heavy `DesertBatflyTravelNavigation` monolith. Recommended first choice:

1. migrate the low-risk Travel support files `DesertBatflyRefuge.cs` -> `Travel/DB_RefugePolicy.cs` and `DesertBatflyWorldRoutePlanner.cs` -> `Travel/DB_WorldRoutePlanner.cs`, preserving algorithms and Task09 ownership;
2. update all executable guards/tests and run R5 + R6 retention again;
3. only after those support files are stable, separately review `DesertBatflyTravelNavigation.cs` for responsibility-aware split/rename rather than a mechanical rename.

If Travel support references make that batch unexpectedly coupled, use the same review-first process and switch to another KEEP/RENAME domain such as Roost/Presentation rather than forcing the migration.
