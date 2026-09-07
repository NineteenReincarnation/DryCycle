from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)


def find_matching_brace(text, open_index):
    depth = 0
    i = open_index
    state = "normal"
    while i < len(text):
        ch = text[i]
        nxt = text[i + 1] if i + 1 < len(text) else ""
        if state == "normal":
            if ch == '/' and nxt == '/':
                state = "line"
                i += 2
                continue
            if ch == '/' and nxt == '*':
                state = "block"
                i += 2
                continue
            if ch == '"':
                state = "string"
                i += 1
                continue
            if ch == "'":
                state = "char"
                i += 1
                continue
            if ch == '{':
                depth += 1
            elif ch == '}':
                depth -= 1
                if depth == 0:
                    return i
            i += 1
            continue
        if state == "line":
            if ch == '\n':
                state = "normal"
            i += 1
            continue
        if state == "block":
            if ch == '*' and nxt == '/':
                state = "normal"
                i += 2
            else:
                i += 1
            continue
        if state == "string":
            if ch == '\\':
                i += 2
                continue
            if ch == '"':
                state = "normal"
            i += 1
            continue
        if state == "char":
            if ch == '\\':
                i += 2
                continue
            if ch == "'":
                state = "normal"
            i += 1
            continue
    raise RuntimeError("unmatched method brace")


def replace_method(text, signature, replacement, label):
    count = text.count(signature)
    if count != 1:
        raise RuntimeError(f"{label}: expected one signature, got {count}")
    start = text.index(signature)
    open_index = text.index('{', start + len(signature))
    end = find_matching_brace(text, open_index)
    return text[:start] + replacement.rstrip() + text[end + 1:]


# -----------------------------------------------------------------------------
# DesertBatflyAI: split state refresh from owner-gated ImmediateDanger/Combat/
# Fear/Roost execution. Keep Combat in the legacy class for R3; R4 owns the
# later responsibility/file split.
# -----------------------------------------------------------------------------
ai_path = "src/Creatures/DesertBatfly/DesertBatflyAI.cs"
ai = read(ai_path)

new_update = r'''    internal void RefreshDecisionState()
    {
        if (fly.room == null) return;

        // This phase may refresh species state and perception, but it must not steer ordinary
        // locomotion. All localGoal/velocity writes live behind the selected R3 owner below.
        if (fly.Emergence.Active || RestrainedByNonFly() ||
            !fly.Consious || fly.inShortcut)
        {
            if (Mode == Activity.Roost)
            {
                hasRoost = false;
                SetMode(Activity.Flight);
            }
            if (Mode == Activity.InjuryRecovery)
            {
                recoveryRoostTarget = null;
                recoverySearchCooldown = 0;
                fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "unavailable / restraint / shortcut");
            }
            CancelAttack();
            return;
        }

        if (++scan >= 8)
        {
            scan = 0;
            ScanCreatures();
        }

        bool recoveryBurrow = fly.AI.behavior == FlyAI.Behavior.Burrow &&
                              fly.Injury.RecoveryState == InjuryRecoveryState.Hive;
        if (fly.AI.fleeFromRain || (!recoveryBurrow && fly.AI.behavior == FlyAI.Behavior.Burrow) ||
            fly.AI.luredCounter > 0 || fly.safariControlled)
        {
            if (Mode == Activity.Roost)
            {
                hasRoost = false;
                SetMode(Activity.Flight);
            }
            if (Mode == Activity.InjuryRecovery)
            {
                recoveryRoostTarget = null;
                recoverySearchCooldown = 0;
                fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "native special behavior owns frame");
            }
            CancelAttack();
            return;
        }

        if (danger != null && retreat <= 0)
        {
            escapeFrom = danger.mainBodyChunk.pos;
            retreat = Mathf.Max(retreat, DesertBatflyTuning.ApproachRetreatTicks);
        }

        if (danger != null || retreat > 0)
        {
            recoveryRoostTarget = null;
            recoverySearchCooldown = 0;
            fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "danger / escape");
            hasRoost = false;
            hasSlot = false;
            attachedChunk = null;
            Target = null;
            SetMode(Activity.Escape);
            if (danger != null) escapeFrom = danger.mainBodyChunk.pos;
            return;
        }

        if (recoveryBurrow)
        {
            if (!fly.Injury.IsSeverelyInjured)
            {
                ClearRecoveryNavigation();
                fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "recovered below severe threshold while entering hive");
                fly.AI.ChangeBehavior(FlyAI.Behavior.Idle);
                fly.burrowOrHangSpot = null;
                fly.movMode = Fly.MovementMode.BatFlight;
                SetMode(Activity.Flight);
            }
            else
            {
                SetMode(Activity.InjuryRecovery);
                return;
            }
        }

        if (Mode == Activity.Escape) SetMode(Activity.Flight);

        // Severe recovery itself is executed only by DB_InjuryRecoveryExecutor. A milder
        // injury may still suppress combat and allow a normal roost proposal.
        if (fly.Injury.BlocksCombat)
        {
            CancelPhysicalAttack();
            if (Mode != Activity.Roost) CancelAttack();
            TryPlanRoost();
            return;
        }

        bool retaliationReady = fly.Personality.Aggressive &&
            retaliationCharges > 0 && retaliationRecovery <= 0;
        if (fly.DesertState.Cooldown > 0 && Mode != Activity.Attach &&
            Mode != Activity.Interfere && !retaliationReady)
        {
            CancelAttack();
            SetMode(Activity.Cooldown);
            return;
        }

        if (!fly.Personality.Aggressive || !GriefAllowsHarass())
        {
            if (Mode != Activity.Roost) CancelAttack();
            TryPlanRoost();
            return;
        }

        if (!Valid(Target))
        {
            CancelAttack();
            if (memory > 0 && Valid(attacker) && CanHarass(attacker))
                Target = attacker;
            if (Target == null)
            {
                TryPlanRoost();
                return;
            }
            SetMode(Activity.Observe);
        }

        // Choosing a combat mode is state/proposal preparation only. The actual roost release,
        // steering, contact and attack timers are frozen until PrimaryOwner=Combat executes.
        if (Mode is Activity.Flight or Activity.Cooldown or Activity.Roost)
        {
            hasRoost = false;
            SetMode(Activity.Observe);
        }
    }

    // Compatibility surface for old callers/tests. R3 hooks use RefreshDecisionState directly;
    // this alias never executes locomotion.
    internal void Update() => RefreshDecisionState();

    internal bool ExecuteImmediateDangerOwned()
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.ImmediateDanger) ||
            fly.room == null || fly.dead || !fly.Consious || RestrainedByNonFly() || fly.inShortcut)
            return false;
        if (danger == null && retreat <= 0 && Mode != Activity.Escape)
            return false;

        ClearRecoveryNavigation();
        fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "R3 PrimaryOwner=ImmediateDanger");
        hasRoost = false;
        hasSlot = false;
        attachedChunk = null;
        Target = null;
        SetMode(Activity.Escape);
        if (danger != null) escapeFrom = danger.mainBodyChunk.pos;

        Vector2 goal = fly.mainBodyChunk.pos +
                       Custom.DirVec(escapeFrom, fly.mainBodyChunk.pos) * 160f +
                       Vector2.up * 50f;
        return SteerOwned(goal, 8f, DB_BehaviorOwner.ImmediateDanger);
    }

    internal bool ExecuteFearOwned(in DB_BehaviorResolution resolution)
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.FearResponse) ||
            resolution.PrimaryOwner != DB_BehaviorOwner.FearResponse || fly.room == null ||
            fly.dead || !fly.Consious || RestrainedByNonFly() || fly.inShortcut)
            return false;

        CancelPhysicalAttack();
        if (resolution.WinningProposal.PreserveGoal || !resolution.FinalGoal.HasValue)
            return true;

        Vector2 goal = resolution.FinalGoal.Value;
        Vector2 direction = Custom.DirVec(fly.mainBodyChunk.pos, goal);
        if (direction == Vector2.zero) return true;
        escapeFrom = fly.mainBodyChunk.pos - direction * 80f;
        retreat = Mathf.Max(retreat, 60);
        SetMode(Activity.Escape);
        return SteerOwned(
            goal,
            Mathf.Max(6f, resolution.WinningProposal.NominalSpeed),
            DB_BehaviorOwner.FearResponse);
    }

    internal bool ExecuteCombatOwned()
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat) ||
            fly.room == null || fly.dead || !fly.Consious || RestrainedByNonFly() ||
            fly.inShortcut || fly.Injury.BlocksCombat || !Valid(Target) ||
            Mode is not (Activity.Observe or Activity.Approach or Activity.Circle or
                Activity.FakeDive or Activity.Dive or Activity.Attach or
                Activity.RetaliationCharge or Activity.Interfere))
            return false;

        ticks++;
        DB_VisibilityChannel targetChannel = Target is Player
            ? DB_VisibilityChannel.Player
            : DB_VisibilityChannel.Creature;
        if (!DB_VisibilityPolicy.CanObserve(
                fly,
                Target.mainBodyChunk.pos,
                430f,
                targetChannel))
            unseen++;
        else
            unseen = 0;

        if (++interest > DesertBatflyTuning.InterestTicks || unseen > 35 ||
            !Custom.DistLess(fly.mainBodyChunk.pos, Target.mainBodyChunk.pos, 430f))
        {
            Finish(false);
            return true;
        }

        Vector2 center = Target.mainBodyChunk.pos;
        float distance = Vector2.Distance(fly.mainBodyChunk.pos, center);

        switch (Mode)
        {
            case Activity.Observe:
                SteerOwned(center + Orbit(150f, 90f), 4.5f, DB_BehaviorOwner.Combat);
                if (ticks > fly.Personality.ObserveDuration)
                {
                    bool counter = Target == attacker && memory > 0;
                    bool grudge = Target is Player targetPlayer &&
                                  IsRememberedPlayer(targetPlayer) &&
                                  !IsTraumatizedPlayer(targetPlayer);

                    if ((counter || grudge) && Target is Player retaliationTarget &&
                        !IsTraumatizedPlayer(retaliationTarget) &&
                        retaliationCharges > 0 && retaliationRecovery <= 0)
                    {
                        float memoryBoost = grudge
                            ? fly.DesertState.GrabMemoryStrength * 0.18f
                            : 0f;
                        if (Random.value < Mathf.Clamp01(
                                fly.Personality.RetaliationChance * fly.Injury.AggressionScale + memoryBoost) &&
                            AcquireSlot())
                        {
                            retaliationCharges--;
                            retaliationDirection = Custom.DirVec(
                                fly.mainBodyChunk.pos,
                                retaliationTarget.mainBodyChunk.pos);
                            SetMode(Activity.RetaliationCharge);
                            break;
                        }
                    }

                    float effectiveAttackThirst = Mathf.Lerp(
                        DesertBatflyTuning.AttackThirst,
                        DesertBatflyTuning.ObserveThirst,
                        fly.Personality.AggressionDrive * 0.35f);
                    bool thirsty = fly.DesertState.Thirst * fly.DesertState.GriefAttackScale * fly.Injury.AggressionScale > effectiveAttackThirst;
                    bool revengeDrink = grudge && fly.DesertState.GrabMemoryStrength > 0.12f;
                    bool wantsRealAttack = thirsty || counter || revengeDrink;

                    float fakeChance = Mathf.Clamp01(fly.Personality.FakeDiveChance);
                    if (grudge)
                        fakeChance *= Mathf.Lerp(0.8f, 0.48f, fly.DesertState.GrabMemoryStrength);
                    if (counter) fakeChance *= 0.82f;
                    if (Target is Player learnedTarget)
                        fakeChance = DesertBatflyThreatTactics.AdjustFakeDiveChance(
                            fly, learnedTarget, fakeChance);

                    if (!wantsRealAttack || Random.value < fakeChance)
                        SetMode(Activity.FakeDive);
                    else if (AcquireSlot())
                        SetMode(Activity.Approach);
                    else
                        ticks = fly.Personality.ObserveDuration / 2;
                }
                break;

            case Activity.Approach:
                SteerOwned(
                    center + Vector2.up * 100f,
                    6f + fly.Personality.AggressionDrive * 1.2f,
                    DB_BehaviorOwner.Combat);
                if (ticks > DesertBatflyTuning.ApproachTicks || distance < 110f)
                    SetMode(Activity.Circle);
                break;

            case Activity.Circle:
                SteerOwned(
                    center + Orbit(95f, 65f),
                    6.5f + fly.Personality.AggressionDrive,
                    DB_BehaviorOwner.Combat);
                if (ticks > DesertBatflyTuning.CircleTicks)
                    SetMode(Activity.Dive);
                break;

            case Activity.FakeDive:
                if (distance < 52f || ticks > DesertBatflyTuning.FakeDivePullUpTicks)
                    ticks = Mathf.Max(DesertBatflyTuning.FakeDivePullUpTicks + 1, ticks);
                SteerOwned(
                    PullingUp
                        ? center + Vector2.up * 160f +
                          Custom.DirVec(center, fly.mainBodyChunk.pos) * 80f
                        : center,
                    PullingUp ? 10f : 12f,
                    DB_BehaviorOwner.Combat);
                if (ticks > DesertBatflyTuning.FakeDiveTicks)
                    SetMode(Activity.Observe);
                break;

            case Activity.Dive:
                SteerOwned(
                    center + Target.mainBodyChunk.vel * 1.5f,
                    12f + fly.Personality.AggressionDrive * 1.5f,
                    DB_BehaviorOwner.Combat);
                BodyChunk contact = FindContact();
                if (contact != null && unseen == 0)
                {
                    attachedChunk = contact;
                    attachOffset = Custom.DirVec(contact.pos, fly.mainBodyChunk.pos) *
                        (contact.rad + fly.mainBodyChunk.rad * 0.5f);
                    drainedWater = 0f;
                    SetMode(Activity.Attach);
                }
                else if (ticks > DesertBatflyTuning.DiveTicks)
                {
                    Finish(false);
                }
                break;

            case Activity.Attach:
                fly.movMode = Fly.MovementMode.Passive;
                if (ticks >= DesertBatflyTuning.AttachTicks)
                    Finish(drainedWater > 0.001f);
                break;

            case Activity.RetaliationCharge:
                if (Target is not Player chargeTarget || IsTraumatizedPlayer(chargeTarget))
                {
                    FinishRetaliation(false);
                    break;
                }

                Vector2 predicted = chargeTarget.mainBodyChunk.pos +
                                    chargeTarget.mainBodyChunk.vel * 1.15f;
                SteerOwned(predicted, fly.Personality.RetaliationSpeed, DB_BehaviorOwner.Combat);
                BodyChunk retaliationContact = FindContact();
                if (retaliationContact != null && unseen == 0)
                {
                    attachedChunk = retaliationContact;
                    retaliationDirection = fly.mainBodyChunk.vel.sqrMagnitude > 0.5f
                        ? fly.mainBodyChunk.vel.normalized
                        : Custom.DirVec(fly.mainBodyChunk.pos, retaliationContact.pos);
                    attachOffset = Custom.DirVec(retaliationContact.pos, fly.mainBodyChunk.pos) *
                        (retaliationContact.rad + fly.mainBodyChunk.rad * 0.45f);
                    ApplyInitialRetaliationImpact(chargeTarget);
                    SetMode(Activity.Interfere);
                }
                else if (ticks > DesertBatflyTuning.RetaliationChargeTicks)
                {
                    FinishRetaliation(false);
                }
                break;

            case Activity.Interfere:
                fly.movMode = Fly.MovementMode.Passive;
                if (ticks >= fly.Personality.RetaliationContactDuration)
                    FinishRetaliation(true);
                break;
        }
        return true;
    }

    internal bool ExecuteRoostOwned()
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Roost) ||
            fly.room == null || fly.dead || !fly.Consious || RestrainedByNonFly() || fly.inShortcut)
            return false;

        if (Mode == Activity.Roost)
        {
            ticks++;
            if (!hasRoost || ticks > fly.Personality.RoostDuration || fly.AI.fleeFromRain)
            {
                StopRoost(true);
                return true;
            }

            if (fly.AI.behavior != FlyAI.Behavior.Chain)
                fly.AI.ChangeBehavior(FlyAI.Behavior.Chain);
            fly.burrowOrHangSpot = roost;
            fly.movMode = Fly.MovementMode.Hang;
            fly.mainBodyChunk.vel *= 0.5f;
            fly.AI.HangInChainUpdate();
            return true;
        }

        // A native/social Fly chain is still a Roost owner. Preserve vanilla chain physics
        // without running the rest of FlyAI.Update, which could choose an unrelated goal.
        if (fly.AI.behavior == FlyAI.Behavior.Chain)
        {
            fly.movMode = Fly.MovementMode.Hang;
            fly.AI.HangInChainUpdate();
            return true;
        }

        return false;
    }'''

ai = replace_method(ai, "    internal void Update()\n", new_update, "DesertBatflyAI.Update split")

new_disturbed = r'''    private void DisturbedByApproach(Creature source)
    {
        // Perception only: record an escape fact. R3 ImmediateDanger owns the actual chain
        // release/steering later in the same FlyAI frame.
        escapeFrom = source?.mainBodyChunk.pos ??
                     fly.mainBodyChunk.pos - Vector2.up * 20f;
        retreat = Mathf.Max(retreat, DesertBatflyTuning.ApproachRetreatTicks);
    }'''
ai = replace_method(ai, "    private void DisturbedByApproach(Creature source)\n", new_disturbed, "DisturbedByApproach")

new_steer = r'''    private bool SteerOwned(Vector2 goal, float speed, DB_BehaviorOwner owner)
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, owner) || fly?.room == null || fly.AI == null)
            return false;

        fly.Injury.NominalFlightSpeed = speed;
        fly.LoseAllGrasps();
        fly.burrowOrHangSpot = null;
        if (fly.AI.behavior == FlyAI.Behavior.Chain)
            fly.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        else
            fly.AI.behavior = FlyAI.Behavior.Idle;
        fly.AI.followingDijkstraMap = -1;
        fly.movMode = Fly.MovementMode.BatFlight;
        hasRoost = false;

        Vector2 direction = Custom.DirVec(fly.mainBodyChunk.pos, goal);
        if (fly.room.GetTile(fly.mainBodyChunk.pos + direction * 25f).Solid ||
            (fly.room.terrain != null &&
             fly.room.terrain.Contains(fly.mainBodyChunk.pos + direction * 25f)))
        {
            goal = fly.mainBodyChunk.pos + Vector2.up * 70f;
            speed = 4f;
        }

        fly.AI.localGoal = goal;
        fly.mainBodyChunk.vel = Vector2.Lerp(
            fly.mainBodyChunk.vel,
            Custom.DirVec(fly.mainBodyChunk.pos, goal) * speed,
            0.22f);
        return true;
    }'''
ai = replace_method(ai, "    private void Steer(Vector2 goal, float speed)\n", new_steer, "Steer owner guard")

new_roost_plan = r'''    private void TryPlanRoost()
    {
        if (Mode == Activity.Roost || hasRoost || fly.AI.behavior == FlyAI.Behavior.Chain)
            return;
        if (scan != 0 || Random.value >
            fly.Personality.RoostChance * DesertBatflySocialBond.RoostScale(fly) * fly.Injury.RoostScale)
            return;
        if (!TryFindRoost(out Vector2 spot)) return;

        // State/proposal preparation only. DB_RoostExecutor performs the Chain/Hang writes.
        roost = spot;
        hasRoost = true;
        SetMode(Activity.Roost);
    }'''
ai = replace_method(ai, "    private void UpdateRoost(bool recovery = false)\n", new_roost_plan, "Roost planning split")

ai = replace_method(ai, "    private void ScanWeapons()\n", "", "remove duplicate AI weapon scan")

ai = replace_once(
    ai,
    "                Steer(target, 4.2f);",
    "                SteerOwned(target, 4.2f, DB_BehaviorOwner.InjuryRecovery);",
    "injury roost steer owner")
ai = replace_once(
    ai,
    "        Steer(safeGoal, 3.8f);",
    "        SteerOwned(safeGoal, 3.8f, DB_BehaviorOwner.InjuryRecovery);",
    "injury safe steer owner")

ai = replace_once(
    ai,
    "    private bool TryDriveRecoveryHive(out Vector2 target)\n    {\n        target = default;",
    "    private bool TryDriveRecoveryHive(out Vector2 target)\n    {\n        target = default;\n        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.InjuryRecovery))\n            return false;",
    "injury hive owner guard")
ai = replace_once(
    ai,
    "    private void BeginRecoveryRoost(Vector2 spot)\n    {\n        recoveryRoostTarget = spot;",
    "    private void BeginRecoveryRoost(Vector2 spot)\n    {\n        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.InjuryRecovery)) return;\n        recoveryRoostTarget = spot;",
    "injury roost commit owner guard")

ai = replace_once(
    ai,
    "        interest = 0;\n        SetMode(Activity.Flight);",
    "        interest = 0;\n        unseen = 0;\n        SetMode(Activity.Flight);",
    "CancelAttack visibility reset")

ai = replace_once(
    ai,
    "    internal void AfterPhysics(bool eu)\n    {\n        if (Mode == Activity.Interfere)",
    "    internal void AfterPhysics(bool eu)\n    {\n        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat)) return;\n\n        if (Mode == Activity.Interfere)",
    "AfterPhysics combat owner guard")
ai = replace_once(
    ai,
    "    private void FinishRetaliation(bool success)\n    {\n        Vector2 from =",
    "    private void FinishRetaliation(bool success)\n    {\n        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat)) return;\n        Vector2 from =",
    "FinishRetaliation owner guard")
ai = replace_once(
    ai,
    "    private void Finish(bool success)\n    {\n        Vector2 from =",
    "    private void Finish(bool success)\n    {\n        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat)) return;\n        Vector2 from =",
    "Finish owner guard")

if re.search(r"\bSteer\(", ai):
    raise RuntimeError("legacy unowned Steer call remains in DesertBatflyAI")
if "UpdateRoost(" in ai:
    raise RuntimeError("legacy UpdateRoost call remains in DesertBatflyAI")
if "ScanWeapons" in ai:
    raise RuntimeError("duplicate DesertBatflyAI ScanWeapons remains")
write(ai_path, ai)


# -----------------------------------------------------------------------------
# Threat runtime: refresh before arbitration; tactical localGoal writes only as a
# modifier inside the already-selected Combat owner. Real projectile cues no longer
# promote themselves into ImmediateDanger before ProjectileEvade can arbitrate.
# -----------------------------------------------------------------------------
threat_path = "src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatRuntime.cs"
threat = read(threat_path)
new_threat_update = r'''    internal static void RefreshState(DesertBatfly bat)
    {
        if (bat == null || bat.room == null || bat.dead || bat.slatedForDeletetion) return;
        RuntimeState state = StateFor(bat);
        DesertBatflyThreatMemoryStore.DecayToCycle(bat.DesertState, CurrentCycle(bat));
        TickAcute(state);
        TrackFormalAggression(bat, state);
        UpdateCue(bat, state);
        ApplyHeldThreatPriority(bat, state);
        ExtendLearnedDisengage(bat, state);
        TrackPursuit(bat, state);
        TrackEncounter(bat, state);
    }

    // Compatibility state-only surface. R3 hooks call RefreshState before arbitration.
    internal static void Update(DesertBatfly bat) => RefreshState(bat);

    internal static void ApplyOwnedTacticalModifier(DesertBatfly bat)
    {
        if (bat == null || !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Combat) ||
            !states.TryGetValue(bat, out RuntimeState state))
            return;
        ApplyTacticalAdjustment(bat, state);
    }

    internal static void CommitFrame(DesertBatfly bat)
    {
        if (bat != null && states.TryGetValue(bat, out RuntimeState state))
            state.PreviousMode = bat.DesertAI.Mode;
    }'''
threat = replace_method(threat, "    internal static void Update(DesertBatfly bat)\n", new_threat_update, "Threat refresh/apply split")

threat = replace_once(
    threat,
    '''        if (cue.ProjectileThreat)\n        {\n            DesertBatflySocialLife.CancelForPriority(bat, "Task11 incoming projectile");\n            if (!DesertBatflyIntimidation.IsExtremeVengeanceActive(bat))\n                bat.DesertAI.Threatened(player, false);\n        }''',
    '''        if (cue.ProjectileThreat)\n        {\n            // Real trajectory is a current-frame Arbiter fact. Do not pre-promote it into\n            // DesertAI Escape here or ImmediateDanger would starve ProjectileEvade.\n            DesertBatflySocialLife.CancelForPriority(bat, "Task11 incoming projectile");\n            state.ModifierReason = "real incoming projectile queued for R3 arbitration";\n        }''',
    "projectile cue no pre-arbiter escape")

threat = replace_once(
    threat,
    '''        if (heldRisk >= 0.72f && distance < 155f &&\n            !DesertBatflyIntimidation.IsExtremeVengeanceActive(bat) &&\n            bat.DesertAI.Target == null)\n        {\n            bat.DesertAI.Threatened(player, false);\n            state.ModifierReason = "recognized currently held threat";\n        }''',
    '''        if (heldRisk >= 0.72f && distance < 155f &&\n            !DesertBatflyIntimidation.IsExtremeVengeanceActive(bat) &&\n            bat.DesertAI.Target == null)\n        {\n            // Learned Threat memory may suppress/reshape aggression, but it never creates a\n            // locomotion owner by itself. Direct real danger uses the ordinary danger/fear path.\n            state.ModifierReason = "recognized held threat; learned caution remains a modifier";\n        }''',
    "learned held threat stays modifier")

# Remove the old acute-hazard direct localGoal override from tactical apply. Acute current
# danger is represented as FearResponse/ImmediateDanger by the Arbiter instead.
old_hazard = r'''        if (state.HazardTimer > 0 && state.HazardCenter.HasValue)
        {
            Vector2 center = state.HazardCenter.Value;
            float distance = Vector2.Distance(center, bat.mainBodyChunk.pos);
            if (distance < 230f)
            {
                Vector2 away = Custom.DirVec(center, bat.mainBodyChunk.pos);
                Vector2 evade = bat.mainBodyChunk.pos +
                    away * Mathf.Lerp(130f, 70f, Mathf.InverseLerp(0f, 230f, distance));
                bat.AI.localGoal = evade;
                bat.Injury.NominalFlightSpeed = Mathf.Max(bat.Injury.NominalFlightSpeed, 7f);
                state.EvadeTarget = evade;
                state.ModifierReason = "acute hazard avoidance";
                state.AttackGeometryAdjustment = "move away from recent hazard center";
                return;
            }
        }

'''
threat = replace_once(threat, old_hazard, "", "remove threat hazard locomotion writer")
threat = replace_once(
    threat,
    "    private static void ApplyTacticalAdjustment(DesertBatfly bat, RuntimeState state)\n    {\n        state.ModifierReason = string.Empty;",
    "    private static void ApplyTacticalAdjustment(DesertBatfly bat, RuntimeState state)\n    {\n        if (!DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Combat)) return;\n\n        state.ModifierReason = string.Empty;",
    "tactical apply combat owner guard")
write(threat_path, threat)


# -----------------------------------------------------------------------------
# Projectile executor must leave a previous Chain/Hang state when ProjectileEvade wins.
# -----------------------------------------------------------------------------
tactics_path = "src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatTactics.cs"
tactics = read(tactics_path)
tactics = replace_once(
    tactics,
    "        bat.AI.localGoal = evade;\n        bat.Injury.NominalFlightSpeed = Mathf.Max(bat.Injury.NominalFlightSpeed, 9f);",
    "        bat.LoseAllGrasps();\n        bat.burrowOrHangSpot = null;\n        if (bat.AI.behavior != FlyAI.Behavior.Idle)\n            bat.AI.ChangeBehavior(FlyAI.Behavior.Idle);\n        bat.AI.followingDijkstraMap = -1;\n        bat.movMode = Fly.MovementMode.BatFlight;\n        bat.AI.localGoal = evade;\n        bat.Injury.NominalFlightSpeed = Mathf.Max(bat.Injury.NominalFlightSpeed, 9f);",
    "projectile evade exits roost/native chain")
write(tactics_path, tactics)


# -----------------------------------------------------------------------------
# Arbiter: acute Task11 current threat can become FearResponse. Persistent learned memory
# remains a modifier and never enters DB_BehaviorOwner.
# -----------------------------------------------------------------------------
arb_path = "src/Creatures/DesertBatfly/Runtime/DB_BehaviorArbiter.cs"
arb = read(arb_path)
old_fear = r'''        if (frame.FearSuppressed || frame.Trauma >= DesertBatflyTuning.TraumaAggressionBlock)
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.FearResponse,
                DB_BehaviorKind.FearRetreat,
                frame.FearSuppressed ? "active fear/intimidation response" : "persistent PTSD response",
                frame.CurrentGoal,
                nominalSpeed: 8f,
                suppressCombat: true,
                suppressSocial: true,
                preserveGoal: true,
                commitment: Mathf.Max(frame.Trauma, 0.55f)));'''
new_fear = r'''        if (frame.FearSuppressed || frame.Threat.AcuteThreat ||
            frame.Trauma >= DesertBatflyTuning.TraumaAggressionBlock)
        {
            Vector2? fearGoal = frame.CurrentGoal;
            bool preserveFearGoal = true;
            if (frame.Threat.AcuteThreat && frame.Threat.HazardCenter.HasValue)
            {
                Vector2 away = frame.Position - frame.Threat.HazardCenter.Value;
                if (away.sqrMagnitude < 0.01f) away = Vector2.up;
                fearGoal = frame.Position + away.normalized * 135f + Vector2.up * 28f;
                preserveFearGoal = false;
            }

            string fearReason = frame.Threat.AcuteThreat
                ? "acute current threat response"
                : frame.FearSuppressed
                    ? "active fear/intimidation response"
                    : "persistent PTSD response";
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.FearResponse,
                DB_BehaviorKind.FearRetreat,
                fearReason,
                fearGoal,
                nominalSpeed: 8f,
                suppressCombat: true,
                suppressSocial: true,
                preserveGoal: preserveFearGoal,
                commitment: Mathf.Max(frame.Trauma, frame.Threat.AcuteThreat ? 0.82f : 0.55f)));
        }'''
arb = replace_once(arb, old_fear, new_fear, "acute fear proposal")
write(arb_path, arb)


# -----------------------------------------------------------------------------
# Owner executors.
# -----------------------------------------------------------------------------
executor_files = {
"src/Creatures/DesertBatfly/Runtime/DB_ImmediateDangerExecutor.cs": r'''namespace DryCycle.Creatures.DesertBatfly;

/// <summary>R3 boundary for direct local escape movement.</summary>
internal static class DB_ImmediateDangerExecutor
{
    internal static bool TryExecute(DesertBatfly bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.ImmediateDanger ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.ImmediateDanger))
            return false;
        DesertBatflySocialLife.CancelForPriority(bat, "R3 PrimaryOwner=ImmediateDanger");
        return bat.DesertAI.ExecuteImmediateDangerOwned();
    }
}
''',
"src/Creatures/DesertBatfly/Runtime/DB_FearExecutor.cs": r'''namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R3 fear/PTSD execution boundary. Current acute danger may supply a retreat goal; passive
/// fear/PTSD can simply suppress lower owners while preserving the current native goal.
/// </summary>
internal static class DB_FearExecutor
{
    internal static bool TryExecute(DesertBatfly bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.FearResponse ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.FearResponse))
            return false;
        DesertBatflySocialLife.CancelForPriority(bat, "R3 PrimaryOwner=FearResponse");
        return bat.DesertAI.ExecuteFearOwned(resolution);
    }
}
''',
"src/Creatures/DesertBatfly/Runtime/DB_CombatExecutor.cs": r'''namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R3 owner boundary for the existing DesertBatflyAI combat state machine. R4 may later move
/// these responsibilities into Combat/ and FlightMotor; R3 only guarantees one owner.
/// </summary>
internal static class DB_CombatExecutor
{
    internal static bool TryExecute(DesertBatfly bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.Combat ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Combat))
            return false;
        DesertBatflySocialLife.CancelForPriority(bat, "R3 PrimaryOwner=Combat");
        if (!bat.DesertAI.ExecuteCombatOwned()) return false;
        DesertBatflyThreatRuntime.ApplyOwnedTacticalModifier(bat);
        return true;
    }
}
''',
"src/Creatures/DesertBatfly/Runtime/DB_RoostExecutor.cs": r'''namespace DryCycle.Creatures.DesertBatfly;

/// <summary>R3 owner boundary for species/native Chain roost behavior.</summary>
internal static class DB_RoostExecutor
{
    internal static bool TryExecute(DesertBatfly bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.Roost ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Roost))
            return false;
        return bat.DesertAI.ExecuteRoostOwned();
    }
}
'''
}
for path, content in executor_files.items():
    full = ROOT / path
    if full.exists():
        raise RuntimeError(f"executor already exists: {path}")
    full.write_text(content, encoding="utf-8", newline="\n")


# -----------------------------------------------------------------------------
# Hook: no vanilla FlyAI.Update before arbitration for DesertBatfly. Native/ordinary
# execution is itself owner-gated. State/Threat/Signal updates complete on every owner path.
# -----------------------------------------------------------------------------
hooks_path = "src/Creatures/DesertBatfly/DesertBatflyHooks.cs"
hooks = read(hooks_path)
new_hook_update = r'''    private static void UpdateAI(On.FlyAI.orig_Update orig, FlyAI self)
    {
        if (self.fly is not DesertBatfly desert)
        {
            orig(self);
            return;
        }

        // R3 order is deliberate: refresh state/facts first, resolve one owner, then execute.
        // Vanilla FlyAI.Update is no longer allowed to write an ordinary goal before Arbiter.
        DesertBatflyEnvironmentalIntegration.Register(desert);
        DesertBatflyEnvironmentalBehavior.RefreshInfluence(desert);
        desert.DesertAI.RefreshDecisionState();
        DesertBatflyThreatRuntime.RefreshState(desert);
        DesertBatflySocialLife.RefreshState(desert);

        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);

        if (ownership.PrimaryOwner is DB_BehaviorOwner.CreaturePhysics or
            DB_BehaviorOwner.Restraint or DB_BehaviorOwner.Shortcut or DB_BehaviorOwner.Emergence)
        {
            DesertBatflySocialLife.CancelForPriority(desert, $"R3 PrimaryOwner={ownership.PrimaryOwner}");
            CompleteR3Frame(desert, ownership);
            return;
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.NativeSpecial &&
            ExecuteNativeOwned(orig, self, desert, ownership))
        {
            CompleteR3Frame(desert, ownership);
            return;
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.ImmediateDanger)
        {
            if (DB_ImmediateDangerExecutor.TryExecute(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.ImmediateDanger,
                "ImmediateDanger executor yielded after current danger recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.InjuryRecovery)
        {
            if (DB_InjuryRecoveryExecutor.TryExecute(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.InjuryRecovery,
                "InjuryRecovery executor yielded after current physical recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.Travel)
        {
            if (DesertBatflyTravelNavigation.TryDriveRealized(desert))
            {
                DesertBatflySocialLife.CancelForPriority(desert, "R3 PrimaryOwner=Travel");
                desert.DesertAI.CancelAttack();
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.Travel,
                "Travel executor yielded after preflight / route validation");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.EnvironmentHardSurvival)
        {
            if (DB_EnvironmentExecutor.TryExecute(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.EnvironmentHardSurvival,
                "HardSurvival executor yielded after current state recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.FearResponse)
        {
            if (DB_FearExecutor.TryExecute(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.FearResponse,
                "Fear executor yielded after current state recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.Vengeance)
        {
            if (DB_VengeanceExecutor.TryExecute(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.Vengeance,
                "Vengeance executor yielded after current state recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.EnvironmentLocalSurvival)
        {
            if (DB_EnvironmentExecutor.TryExecute(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.EnvironmentLocalSurvival,
                "Environment executor yielded after current state recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.ImmediateProjectileEvade)
        {
            if (DB_ProjectileEvadeExecutor.TryExecute(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.ImmediateProjectileEvade,
                "Projectile evade executor yielded after current projectile recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.Combat)
        {
            if (DB_CombatExecutor.TryExecute(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.Combat,
                "Combat executor yielded after target/state recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.Roost)
        {
            if (DB_RoostExecutor.TryExecute(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.Roost,
                "Roost executor yielded after chain/roost recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.Social)
        {
            if (DB_SocialExecutor.TryExecute(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.Social,
                "Social executor yielded after reservation/state recheck");
        }

        if (ExecuteNativeOwned(orig, self, desert, ownership))
        {
            CompleteR3Frame(desert, ownership);
            return;
        }

        // Defensive fallback only; record the actual fallback owner rather than executing an
        // unowned second controller.
        ownership = DB_BehaviorArbiter.ResolveFrame(desert);
        if (ownership.PrimaryOwner is DB_BehaviorOwner.Ordinary or DB_BehaviorOwner.VanillaFallback)
            ExecuteNativeOwned(orig, self, desert, ownership);
        CompleteR3Frame(desert, ownership);
    }

    private static bool ExecuteNativeOwned(
        On.FlyAI.orig_Update orig,
        FlyAI self,
        DesertBatfly desert,
        in DB_BehaviorResolution ownership)
    {
        if (orig == null || self == null || desert == null) return false;
        DB_BehaviorOwner owner = ownership.PrimaryOwner;
        if (owner is not (DB_BehaviorOwner.NativeSpecial or DB_BehaviorOwner.Ordinary or
            DB_BehaviorOwner.VanillaFallback))
            return false;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(desert, owner)) return false;
        orig(self);
        return true;
    }

    private static void CompleteR3Frame(
        DesertBatfly desert,
        in DB_BehaviorResolution ownership)
    {
        if (desert == null) return;
        DesertBatflyThreatRuntime.CommitFrame(desert);
        DesertBatflyThreatTrace.Sample(desert);
        DesertBatflySignalRuntime.Update(desert);
        DesertBatflySocialLife.SampleTrace(desert);
        DesertBatflyDebugTrace.Sample(desert);
        if (desert.abstractCreature != null && AIDebugTrace.IsWatched(desert.abstractCreature))
            AIDebugTrace.RecordChange(
                desert.abstractCreature,
                AIDebugEventCategory.Decision,
                "PrimaryOwner",
                ownership.PrimaryOwner,
                ownership.Reason);
    }'''
hooks = replace_method(
    hooks,
    "    private static void UpdateAI(On.FlyAI.orig_Update orig, FlyAI self)\n",
    new_hook_update,
    "Hooks UpdateAI owner-first")
if "desert.DesertAI.Update();" in hooks or "suspended.DesertAI.Update();" in hooks:
    raise RuntimeError("Hooks still calls legacy DesertBatflyAI.Update pipeline")
write(hooks_path, hooks)


# -----------------------------------------------------------------------------
# R3 managed architecture regression.
# -----------------------------------------------------------------------------
test_path = "tests/DesertBatfly/Program.Task14R3.cs"
test = read(test_path)
test = replace_once(
    test,
    '''        Type projectileExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ProjectileEvadeExecutor", true);\n        Type threatTactics = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatTactics", true);''',
    '''        Type projectileExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ProjectileEvadeExecutor", true);\n        Type immediateDangerExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ImmediateDangerExecutor", true);\n        Type fearExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FearExecutor", true);\n        Type combatExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatExecutor", true);\n        Type roostExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RoostExecutor", true);\n        Type threatRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatRuntime", true);\n        Type threatTactics = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatTactics", true);''',
    "R3 executor test types")

test = replace_once(
    test,
    '''        Check(threatTactics.GetMethod("ApplyProjectileEvadeOwned", Flags) != null &&\n              MethodCallOffset(threatTactics.GetMethod("ApplyProjectileEvadeOwned", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 real projectile dodge has an owner-gated apply surface while Threat memory remains a modifier");''',
    '''        Check(threatTactics.GetMethod("ApplyProjectileEvadeOwned", Flags) != null &&\n              MethodCallOffset(threatTactics.GetMethod("ApplyProjectileEvadeOwned", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 real projectile dodge has an owner-gated apply surface while Threat memory remains a modifier");\n        Check(threatRuntime.GetMethod("RefreshState", Flags) != null &&\n              threatRuntime.GetMethod("ApplyOwnedTacticalModifier", Flags) != null &&\n              threatRuntime.GetMethod("CommitFrame", Flags) != null &&\n              MethodCallOffset(threatRuntime.GetMethod("ApplyTacticalAdjustment", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 Threat refresh is pre-arbiter and tactical localGoal adjustment is Combat-owner-only");\n        Check(desertAI.GetMethod("RefreshDecisionState", Flags) != null &&\n              desertAI.GetMethod("ExecuteImmediateDangerOwned", Flags) != null &&\n              desertAI.GetMethod("ExecuteFearOwned", Flags) != null &&\n              desertAI.GetMethod("ExecuteCombatOwned", Flags) != null &&\n              desertAI.GetMethod("ExecuteRoostOwned", Flags) != null &&\n              desertAI.GetMethod("ScanWeapons", Flags) == null,\n            "Task14 R3 DesertBatflyAI separates decision refresh from owner executors and removes duplicate weapon scan");\n        foreach (string methodName in new[]\n                 { "ExecuteImmediateDangerOwned", "ExecuteFearOwned", "ExecuteCombatOwned", "ExecuteRoostOwned", "SteerOwned" })\n            Check(MethodCallOffset(desertAI.GetMethod(methodName, Flags), arbiter, "IsPrimaryOwner") >= 0,\n                "Task14 R3 " + methodName + " requires same-tick PrimaryOwner");\n        Check(MethodCallOffset(desertAI.GetMethod("AfterPhysics", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 Attach/Interfere AfterPhysics cannot run after another locomotion owner won");''',
    "R3 AI/threat test checks")

test = replace_once(
    test,
    '''              MethodCallOffset(updateAI, environmentBehavior, "RefreshInfluence") >= 0 &&\n              MethodCallOffset(updateAI, injuryExecutor, "TryExecute") >= 0 &&''',
    '''              MethodCallOffset(updateAI, environmentBehavior, "RefreshInfluence") >= 0 &&\n              MethodCallOffset(updateAI, desertAI, "RefreshDecisionState") >= 0 &&\n              MethodCallOffset(updateAI, threatRuntime, "RefreshState") >= 0 &&\n              MethodCallOffset(updateAI, immediateDangerExecutor, "TryExecute") >= 0 &&\n              MethodCallOffset(updateAI, fearExecutor, "TryExecute") >= 0 &&\n              MethodCallOffset(updateAI, injuryExecutor, "TryExecute") >= 0 &&''',
    "Hook pre-arbiter AI/threat checks")
test = replace_once(
    test,
    '''              MethodCallOffset(updateAI, projectileExecutor, "TryExecute") >= 0 &&\n              MethodCallOffset(updateAI, socialExecutor, "TryExecute") >= 0,\n            "Task14 R3 migrated owner executors enter through central owner resolution");''',
    '''              MethodCallOffset(updateAI, projectileExecutor, "TryExecute") >= 0 &&\n              MethodCallOffset(updateAI, combatExecutor, "TryExecute") >= 0 &&\n              MethodCallOffset(updateAI, roostExecutor, "TryExecute") >= 0 &&\n              MethodCallOffset(updateAI, socialExecutor, "TryExecute") >= 0,\n            "Task14 R3 all ordinary locomotion domains enter through central owner resolution");''',
    "Hook combat/roost checks")
test = replace_once(
    test,
    '''        Check(MethodCallOffset(updateAI, threatTactics, "TryApplyOrdinaryProjectileEvade") < 0,\n            "Task14 R3 legacy pipeline no longer applies projectile dodge outside PrimaryOwner");''',
    '''        Check(MethodCallOffset(updateAI, threatTactics, "TryApplyOrdinaryProjectileEvade") < 0 &&\n              MethodCallOffset(updateAI, desertAI, "Update") < 0,\n            "Task14 R3 hook has no legacy projectile or monolithic DesertBatflyAI executor pipeline");\n        MethodInfo executeNativeOwned = hooks.GetMethod("ExecuteNativeOwned", Flags);\n        Check(executeNativeOwned != null && MethodCallOffset(executeNativeOwned, arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 vanilla FlyAI.Update is itself restricted to an accepted NativeSpecial/Ordinary/Fallback owner");''',
    "legacy pipeline/native owner checks")
test = replace_once(
    test,
    '''        Console.WriteLine(\n            "Task14 R3: InjuryRecovery, Travel, Vengeance, Environment, ProjectileEvade and Social are owner-gated. Combat/ordinary migration remains open.");''',
    '''        Console.WriteLine(\n            "Task14 R3: ordinary locomotion domains are owner-gated; Rain World live validation and rejected-proposal Observatory presentation remain.");''',
    "R3 test summary")
write(test_path, test)


# -----------------------------------------------------------------------------
# Status document: code-side single-owner closure, but do not claim live completion.
# -----------------------------------------------------------------------------
doc_path = "docs/Discussion/Task_14_R3_FrameArbiterStatus.txt"
doc = read(doc_path)
doc = replace_once(
    doc,
    "【R3 已启动 / FrameContext + Arbiter foundation 已实现 / ordinary single-owner migration 尚未完成】",
    "【R3 代码侧 ordinary single-owner 已收敛 / managed guard 已更新 / Rain World 实机验收尚未完成】",
    "R3 current status")
start = doc.index("R3-OPEN-01 — DesertBatflyAI decision / execution split")
end = doc.index("R3-CLOSED-02 — Injury proposal executor", start)
closed_01 = '''R3-CLOSED-01 — DesertBatflyAI decision / execution split（代码侧已收敛）\n-----------------------------------------------------------------------\n\n当前 FlyAI hook 顺序已经改为：\n\nRefresh facts/state\n    -> DB_BehaviorArbiter.ResolveFrame\n    -> exactly one owner executor\n\n而不是先执行 vanilla FlyAI.Update 再覆盖 localGoal。\n\nDesertBatflyAI 当前 R3 边界：\n\n- `RefreshDecisionState` 只刷新 perception / danger / target / roost intent；\n- `ExecuteImmediateDangerOwned` 只接受 `PrimaryOwner=ImmediateDanger`；\n- `ExecuteFearOwned` 只接受 `PrimaryOwner=FearResponse`；\n- `ExecuteCombatOwned` 只接受 `PrimaryOwner=Combat`；\n- `ExecuteRoostOwned` 只接受 `PrimaryOwner=Roost`；\n- `SteerOwned` 自身再次校验 same-tick PrimaryOwner；\n- Combat timers 在被高优先级 owner 抢占时冻结；\n- duplicate `DesertBatflyAI.ScanWeapons` 已删除，real projectile 使用统一 WeaponPerception/FrameContext；\n- `AfterPhysics` Attach/Interfere 只允许 Combat owner 同 tick 执行。\n\nvanilla FlyAI.Update 现在仅通过 `ExecuteNativeOwned` 进入，并只允许：\n\n- NativeSpecial；\n- Ordinary；\n- VanillaFallback。\n\n因此 custom owner 不再发生“vanilla 先写一次 localGoal，再被 custom 覆盖”的双 writer。\n\n这仍不是 R4：Combat 责任尚未迁出 DesertBatflyAI，也没有引入 DB_FlightMotor。\n\n仍需 Rain World 实机验证 Combat/Roost/Ordinary 切换、Attach/Interfere 和 vanilla swarm parity。\n\n'''
doc = doc[:start] + closed_01 + doc[end:]
doc = replace_once(
    doc,
    "【R3 foundation 已建立；InjuryRecovery / Travel / Vengeance / Environment / ImmediateProjectileEvade / Social 已进入 owner-gated executor。】\n【R3 尚未完成；下一阶段集中处理 Combat/Ordinary 的 DesertBatflyAI split、Threat tactical apply 与 ordinary localGoal 单 writer。】",
    "【R3 代码侧单 owner 已收敛：ImmediateDanger / InjuryRecovery / Travel / HardSurvival / Fear / Vengeance / Environment / ProjectileEvade / Combat / Roost / Social / Ordinary 均通过 Arbiter 进入。】\n【R3 尚不标记最终完成：仍需 Observatory rejected proposal 正式展示，以及 Rain World live scenarios 验收。】",
    "R3 conclusion")
write(doc_path, doc)


# Self-clean one-shot artifacts after successful mutation.
for transient in [
    ROOT / "scripts/task14_r3_ai_owner_apply.py",
    ROOT / ".github/workflows/task14-r3-ai-owner-one-shot.yml",
    ROOT / "scripts/task14_r3_ai_owner_trigger.txt",
]:
    if transient.exists():
        transient.unlink()
