# Desert Batfly — Emergent Social Roles

**Status: REJECTED / 否决·已移除**  
**Decision date:** 2026-09-06

This document is now a decision record, not a feature specification.

## Decision

The runtime social-role system is removed from the current Desert Batfly design.

Rejected roles:

- Sentinel / 哨戒者
- Bully / 挑衅者
- Opportunist / 机会主义者

The species should express individual differences through continuous personality, experience and local social context instead of temporary job-like labels.

## Why it was rejected

### 1. Role steering interfered with natural Fly locomotion

The previous Sentinel/Opportunist implementation could alter `FlyAI.localGoal` and directly add velocity while vanilla `FlyAI` was also controlling movement.

For Sentinel, the ordinary-flight bias attempted to hold the bat near a fixed perimeter around `Flock.Center`. In some geometries this reduced to repeated correction along nearly one axis. During playtesting, some Desert Batflies visibly flew up and down in a straight line for long periods.

That behavior is too mechanical for Rain World and violates the intended ownership boundary:

> social/ecology code may influence decisions, but it must not become a second persistent locomotion controller competing with `FlyAI`.

### 2. The role abstraction over-organized the species

The role system made the colony read more like a lightly organized profession-based society than intended.

Desert Batfly already has enough continuous sources of individuality:

- `Temperament`
- `Nerve`
- `Conformity`
- `RoostAffinity`
- `SandSpitAffinity`
- `VengeanceAffinity`
- Sex
- SocialBond
- Grief
- Trauma / PTSD
- GrabMemory
- Thirst
- Injury
- Fear / local alarm
- Extreme Vengeance
- Roost / Chain
- ordinary Observe / Harass / FakeDive / Dive / Attach behavior

A bat can therefore be more watchful, more aggressive, more independent, more social or more cautious without being assigned a named runtime role.

### 3. The implementation spread too far across unrelated systems

Task 02 eventually touched or depended on:

- `DesertBatflyAI`
- creature scanning
- ordinary flight
- harassment tuning
- injury gating
- room flock snapshots
- AI Observatory
- debug traces
- managed regression tests

That was too much control surface for an ecological flavor feature and increased regression risk.

## Current implementation state

The feature is functionally removed.

The migration currently guarantees:

- role scores are always `0`;
- role selection always returns `None`;
- Bully modifiers are neutral;
- Sentinel/Opportunist visible-scan logic performs no action;
- role flight bias performs no action;
- role code no longer writes `FlyAI.localGoal`;
- role code no longer adds role-specific velocity;
- `DesertBatflyFlockSnapshot.Capture` no longer reads the role of each bat;
- tests verify that 10,000 personalities produce zero role expressions.

A small number of old type/member names may temporarily remain as **zero-behavior compatibility seams** because AI Observatory and older helper code still reference them. These symbols are migration scaffolding only. They must not regain gameplay authority.

## Compatibility-seam rules

Any remaining `DesertBatflySocialRoles`, `DesertBatflyRoleScores`, `ExpressedSocialRole`, or `SocialRoleSuppression` compatibility code must obey all of the following:

1. It may return neutral/debug values only.
2. It may not create a role lifecycle.
3. It may not change AI targets.
4. It may not alter attack eligibility or `AttackSlots`.
5. It may not raise alarms.
6. It may not modify `localGoal`.
7. It may not modify velocity.
8. It may not maintain a role population budget.
9. Removing the compatibility seam later must be behavior-preserving cleanup.

## What remains valid from the surrounding social design

The rejection of Task 02 does **not** remove the other social/ecological systems.

Still valid:

- `Conformity` as a continuous response tendency;
- SocialBond as a concrete one-target relationship;
- Grief after meaningful partner loss;
- local fear/alarm propagation;
- Trauma / PTSD;
- GrabMemory;
- Extreme Vengeance and its follower lifecycle;
- Roost / Chain behavior;
- Injury and recovery;
- ordinary harassment and retaliation;
- SandSpit;
- Peach predation/rescue/corpse interactions.

`Follower-like` and `Loner-like` remain descriptions of ordinary personality tendencies, not formal roles.

## Replacement design direction

If later work needs effects similar to the old roles, implement them as continuous ordinary-AI biases with strict ownership boundaries.

Examples:

- A high-Nerve bat may notice danger slightly earlier through the normal perception path.
- A high-Temperament bat may have a somewhat lower ordinary harassment threshold.
- A low-Conformity bat may return to normal activity on a different timescale after group panic.

These should be direct consequences of existing personality/state values, not `SentinelRole`, `BullyRole`, `OpportunistRole`, score selection, role commitment, or a role budget.

Any future movement influence must be bounded and must not continuously overwrite the vanilla fly's navigation target just to maintain a social formation.

## Regression requirements

The rejected feature is considered safely removed only while all of these remain true:

- every generated role score is zero;
- `Select(...)` always returns `None`;
- no role-specific harassment/fake-dive modifier is active;
- no role-specific visible-threat alarm exists;
- no role-specific early-return state exists;
- no role-specific movement controller exists;
- flock capture does not inspect role state;
- the remaining Desert Batfly systems continue to function independently.

## Historical note

The original implementation and old design can still be recovered from Git history if historical analysis is needed. It must not be treated as an active specification or automatically restored by future code-generation work.
