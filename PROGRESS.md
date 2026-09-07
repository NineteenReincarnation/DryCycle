# DryCycle Project Progress

Last updated: 2026-09-08
Active workstream: Desert Batfly Task14 architecture refactor
Current branch: `task14-r6-b1-final`
Current verified implementation HEAD before this progress update: `f93ad003ef44a16c1a6f13d36dfa54270a771992`

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
- B11: Environment Exposure/Profile physical domain migration; filenames moved to `Environment/DB_EnvironmentExposure.cs` and `Environment/DB_EnvironmentProfile.cs` with source blobs unchanged. Internal long type names remain intentionally pending a later caller-safe rename.

## This run: R6-B11 Environment leaf physical migration

### Start state

- Began from `ad378510d47da75e31bc3fbf2e9ae69a42eb3a5d`.
- Previous run had already reviewed `DesertBatflyThreatRuntime.cs` as a multi-responsibility SPLIT candidate and therefore unsuitable for mechanical rename.
- Environment Exposure/Profile were identified by the migration manifest as lower-risk leaf responsibilities.
- A repository-local temporary workflow approach was attempted but blocked by platform safety checks. The container also has no external DNS access, so a local Git clone could not be used for pre-commit repository guards.

### Completed

1. Physical domain move for Environment Exposure:
   - `src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalExposure.cs`
   - -> `src/Creatures/DesertBatfly/Environment/DB_EnvironmentExposure.cs`
   - Source blob SHA remains exactly `2910ad2ebbcb0ee0651ee2e11a6a35021cfcfcae`.
   - Therefore this move changes no compiled logic, constants, namespace, type identity, or caller behavior.

2. Physical domain move for Environment Profile:
   - `src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalProfile.cs`
   - -> `src/Creatures/DesertBatfly/Environment/DB_EnvironmentProfile.cs`
   - Source blob SHA remains exactly `c36a390ca4842d9a0e819de873b7123ad98156c3`.
   - Weather classification, six-phase resolution, HeavyRain/Heat/Sandstorm math, thresholds, and reason strings are byte-for-byte unchanged from the prior implementation.

3. R5 retention guard path coverage was extended so its Environment cross-room-ownership prohibitions cover both the remaining historical `Environmental/` directory and the new `Environment/` directory.
   - No assertion was removed or weakened.
   - Script executable mode was explicitly preserved as `100755`.

Implementation commit:

`f93ad003ef44a16c1a6f13d36dfa54270a771992` — `R6 B11 move Environment exposure/profile leaves`

## Self-review and verification

- Commit compare from `ad378510d47da75e31bc3fbf2e9ae69a42eb3a5d` to `f93ad003ef44a16c1a6f13d36dfa54270a771992` contains exactly three changed paths:
  - R5 guard: 2 additions / 2 deletions, only extending directory scope.
  - Environment Exposure: Git rename with 0 additions / 0 deletions.
  - Environment Profile: Git rename with 0 additions / 0 deletions.
- New Exposure path was fetched after commit and has the same blob SHA as before the move.
- No production logic, save identity, external ID, event ownership, movement ownership, weather thresholds, or serialization format changed in B11.

Not executed in this run:

- `scripts/check-desertbatfly-r5-retention.sh` as an actual shell process.
- `scripts/check-desertbatfly-r6-b1.sh` as an actual shell process.
- Full `.NET Framework 4.8` build against Rain World assemblies.
- Managed DesertBatfly integration tests requiring Rain World/BepInEx DLLs.
- Rain World live scenarios and 20–30 Desert Batfly performance acceptance.

These are not reported as passing. B11 was deliberately limited to a blob-identical physical move plus a mechanically reviewable guard path update because the available execution environment could not run repository-local scripts before commit.

## Important files changed this run

- `src/Creatures/DesertBatfly/Environment/DB_EnvironmentExposure.cs`
- `src/Creatures/DesertBatfly/Environment/DB_EnvironmentProfile.cs`
- `scripts/check-desertbatfly-r5-retention.sh`
- `PROGRESS.md`

## Remaining Task14 requirements

R6 remains incomplete. Major remaining work:

- Environment leaf **type identity** rename (`DesertBatflyEnvironmentalExposure` -> `DB_EnvironmentExposure`, `DesertBatflyEnvironmentalProfile` -> `DB_EnvironmentProfile`) after a safe all-caller replacement path is available.
- Environment Runtime/RoomState/Core responsibility migration.
- Threat runtime/event responsibility-aware split/absorb review and migration.
- Signals domain migration.
- remaining Social/Roost identities.
- Injury domain migration.
- Fear/Vengeance domain migration.
- Core (`DB_Creature`, state/personality/sex/tuning responsibility review, `DB_Definition`).
- TravelNavigation responsibility-aware migration.
- remaining Presentation/debug identities.
- production-wide Task09-Task13 historical naming cleanup.
- old root-file/dead-code cleanup.
- final R6 naming/path/architecture guard.
- R7 performance/debug/full regression/final acceptance.
- `FINAL_REPORT.md` after complete acceptance only.

## Current blockers

- Full managed build/tests require developer-local Rain World/BepInEx assemblies.
- Final behavior/performance acceptance requires Rain World runtime.
- Current environment cannot clone GitHub via normal network/DNS, and the attempted temporary workflow write was blocked by platform safety checks. This limits safe branch-wide type replacement, but does not block blob-identical physical moves or small individually reviewable edits.

## Recommended next run

1. Re-check whether a repository-local validation path is available. If yes, finish B11 type identities by replacing all `DesertBatflyEnvironmentalExposure` / `DesertBatflyEnvironmentalProfile` callers and run R5/R6 guards before commit.
2. If bulk caller-safe replacement remains unavailable, continue with another blob-identical physical domain move or small text-only Task09-Task13 cleanup that can be proven behavior-neutral.
3. Continue to avoid mechanical rename/split of `DesertBatflyThreatRuntime.cs` and `DesertBatflyTravelNavigation.cs` until their responsibility boundaries are explicit.
4. Keep `Environment/` included in all architecture guards as migration proceeds.
