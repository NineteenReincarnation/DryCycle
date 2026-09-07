# DryCycle Project Progress

Last updated: 2026-09-08
Active workstream: Desert Batfly Task14 architecture refactor
Current branch: `task14-r6-b1-final`
Current implementation HEAD before this progress update: `8c2ef92321924d48d61e065ac1f839896789a6d0`

## Current verified architecture state

Task14 R0-R5 are code-side complete on the stacked refactor branch history. R6 domain/type/file migration remains in progress. Full Rain World build/live validation is still deferred because this execution environment does not have the developer-local Rain World/BepInEx assemblies or Rain World runtime.

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
- B10: production/debug historical Task wording cleanup in Threat trace and Environment profile.

## This run: R6-B10 historical naming cleanup

### Start state

- Began from `26e3faa01189cfbc8ef21e8158d09bb590985cb4`.
- Previous review recommended inspecting `DesertBatflyThreatRuntime.cs` and `DesertBatflyThreatEvent.cs` before any mechanical rename because the migration manifest marks them SPLIT / PARTIAL ABSORB.
- Review confirmed Threat Runtime currently owns several distinct concerns at once: per-bat cue/acute runtime state, room temporal evidence, EventHub subscribers, weapon/explosion hooks, debug state, and tactical inputs. It is therefore not safe to treat it as a leaf rename.
- Environment Exposure/Profile were reviewed as lower-risk leaves, but the current connector write path cannot safely perform a branch-wide reference rename across their large callers with the same verification quality as prior GitHub Actions migration batches.

### Completed

1. `src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatTrace.cs`
   - Removed four production/debug strings that exposed the historical `Task11` development label.
   - Replaced them with domain terminology: `Threat Signature`.
   - No control flow, state, constants, calls, or trace keys changed.
   - Commit: `0cb6774105c75d0f7237e8d5165efb39d424d1e5`.

2. `src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalProfile.cs`
   - Removed historical `Task09` wording from DeathRain ownership reasons; wording now refers directly to the Travel domain/cross-room ownership.
   - Removed historical `Task13` wording from the HeavyRain shelter-ecology comment; wording now refers directly to Environment.
   - No weather thresholds, six-phase logic, profile math, or weather classification changed.
   - Commit: `8c2ef92321924d48d61e065ac1f839896789a6d0`.

### Self-review

- Compare from `26e3faa01189cfbc8ef21e8158d09bb590985cb4` to `8c2ef92321924d48d61e065ac1f839896789a6d0` contains exactly two modified production files.
- ThreatTrace diff is exactly four string replacements: `Task11` -> `Threat Signature` domain wording.
- EnvironmentProfile diff contains two DeathRain reason-string replacements and one HeavyRain historical-label comment replacement. Three incidental double-space comment formatting changes were also observed; they do not affect compiled behavior.
- No gameplay algorithm, threshold, save identity, external ID, event ownership, movement authority, or serialization logic changed.

### Validation performed

- GitHub commit/diff review performed for both B10 commits.
- R6 B10 diff scope verified as two files only.
- No behavior pass is claimed from compilation or live play.

Not executable in this run:

- `scripts/check-desertbatfly-r5-retention.sh`.
- `scripts/check-desertbatfly-r6-b1.sh`.
- Full `.NET Framework 4.8` build against Rain World assemblies.
- Managed DesertBatfly integration tests requiring Rain World/BepInEx DLLs.
- Rain World live scenarios and 20–30 Desert Batfly performance acceptance.

These remain validation blockers only; they do not prevent continued source-level R6 work.

## Remaining Task14 requirements

R6 remains incomplete. Major remaining work:

- Threat runtime/event/trace physical/type migration; Runtime/Event require responsibility-aware split/absorb review rather than mechanical rename.
- Signals domain migration.
- Environment domain migration, including Exposure/Profile physical/type migration and Environment Runtime/RoomState ownership cleanup.
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
- `FINAL_REPORT.md` only after complete final acceptance.

## Current blockers

- Developer-local Rain World/BepInEx assemblies are required for the full managed build/test suite.
- Rain World runtime is required for final behavior and performance acceptance.
- The current GitHub connector can update individual files safely but is not suitable for an unverified branch-wide rename across large callers; bulk domain renames should use a migration path that can run repository-local guards before commit.

## Recommended next run

1. Continue the production Task09-Task13 naming sweep in small files where changes can be proven text-only; prioritize remaining Debug/Observatory strings and small domain leaves.
2. Re-inspect Threat Event to determine which Threat-specific evidence interpretation still belongs in Threat and which real-world facts are already fully owned by `DB_EventHub`; do not rename it until that ownership boundary is explicit.
3. If a safe repository-local bulk-edit/validation path is available, perform Environment leaf physical migration:
   - `Environmental/DesertBatflyEnvironmentalExposure.cs` -> `Environment/DB_EnvironmentExposure.cs`;
   - `Environmental/DesertBatflyEnvironmentalProfile.cs` -> `Environment/DB_EnvironmentProfile.cs`;
   then update all callers and run R5/R6 guards plus exact source-equivalence checks.
4. Do not mechanically rename/split `DesertBatflyTravelNavigation.cs` or `DesertBatflyThreatRuntime.cs`.
