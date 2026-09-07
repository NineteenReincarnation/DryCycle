from pathlib import Path

AI = Path('src/Creatures/DesertBatfly/DesertBatflyAI.cs')
HOOKS = Path('src/Creatures/DesertBatfly/DesertBatflyHooks.cs')
THREAT = Path('src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatRuntime.cs')
STATUS = Path('docs/Discussion/Task_14_R3_FrameArbiterStatus.txt')
TEST = Path('tests/DesertBatfly/Program.Task14R3.cs')
EXEC = Path('src/Creatures/DesertBatfly/Runtime/DB_CoreDecisionExecutor.cs')


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{label}: expected 1 occurrence, found {count}')
    return text.replace(old, new, 1)

ai = AI.read_text(encoding='utf-8')
start = ai.index('    internal void Update()\n    {')
end = ai.index('    private BodyChunk FindContact()', start)
old_method = ai[start:end]
new_method = r'''    /// <summary>
    /// R3 decision refresh. This phase may update perception, timers and intent state, but it
    /// deliberately does not steer, hang, burrow or write a locomotion goal. The selected
    /// PrimaryOwner performs those writes later in the same FlyAI frame.
    /// </summary>
    internal void RefreshDecisionState()
    {
        if (fly.room == null) return;
        ticks++;

        if (fly.Emergence.Active || RestrainedByNonFly() || !fly.Consious || fly.inShortcut)
        {
            ClearRecoveryNavigation();
            CancelAttack();
            fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "unavailable / restraint / shortcut");
            return;
        }

        if (++scan >= 8)
        {
            scan = 0;
            ScanCreatures();
            ScanWeapons();
        }

        bool recoveryBurrow = fly.AI.behavior == FlyAI.Behavior.Burrow &&
                              fly.Injury.RecoveryState == InjuryRecoveryState.Hive;
        if (fly.AI.fleeFromRain || (!recoveryBurrow && fly.AI.behavior == FlyAI.Behavior.Burrow) ||
            fly.AI.luredCounter > 0 || fly.safariControlled)
        {
            if (Mode == Activity.InjuryRecovery)
            {
                ClearRecoveryNavigation();
                fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "vanilla priority owns behavior");
            }
            CancelAttack();
            return;
        }

        if (danger != null && retreat <= 0)
        {
            escapeFrom = danger.mainBodyChunk.pos;
            retreat = Mathf.Max(retreat, DesertBatflyTuning.ApproachRetreatTicks);
            CancelAttack();
            SetMode(Activity.Escape);
        }

        if (danger != null || retreat > 0)
        {
            ClearRecoveryNavigation();
            fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "danger / escape");
            hasSlot = false;
            attachedChunk = null;
            Target = null;
            if (danger != null) escapeFrom = danger.mainBodyChunk.pos;
            SetMode(Activity.Escape);
            return;
        }

        if (recoveryBurrow)
        {
            if (!fly.Injury.IsSeverelyInjured)
            {
                ClearRecoveryNavigation();
                fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "recovered below severe threshold while entering hive");
                SetMode(Activity.Flight);
            }
            else
            {
                SetMode(Activity.InjuryRecovery);
                return;
            }
        }

        if (Mode == Activity.Escape) SetMode(Activity.Flight);
        if (fly.Injury.BlocksCombat)
        {
            CancelPhysicalAttack();
            if (Mode != Activity.Roost) CancelAttack();
            return;
        }

        bool retaliationReady = fly.Personality.Aggressive && retaliationCharges > 0 && retaliationRecovery <= 0;
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
            return;
        }

        if (!Valid(Target))
        {
            CancelAttack();
            if (memory > 0 && Valid(attacker) && CanHarass(attacker)) Target = attacker;
            if (Target == null) return;
            SetMode(Activity.Observe);
        }

        DB_VisibilityChannel targetChannel = Target is Player
            ? DB_VisibilityChannel.Player
            : DB_VisibilityChannel.Creature;
        if (!DB_VisibilityPolicy.CanObserve(fly, Target.mainBodyChunk.pos, 430f, targetChannel)) unseen++;
        else unseen = 0;

        if (++interest > DesertBatflyTuning.InterestTicks || unseen > 35 ||
            !Custom.DistLess(fly.mainBodyChunk.pos, Target.mainBodyChunk.pos, 430f))
        {
            Vector2 from = Target?.mainBodyChunk.pos ?? fly.mainBodyChunk.pos - Vector2.up;
            CancelAttack();
            fly.DesertState.Cooldown = Mathf.Max(fly.DesertState.Cooldown, DesertBatflyTuning.FailedCooldown);
            escapeFrom = from;
            retreat = 75;
            SetMode(Activity.Escape);
            return;
        }

        if (Mode is Activity.Flight or Activity.Cooldown or Activity.Roost)
            SetMode(Activity.Observe);
    }

    internal bool ExecuteImmediateDangerOwned()
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.ImmediateDanger) ||
            fly.room == null || fly.dead || !fly.Consious || (!HasImmediateDanger && danger == null))
            return false;

        if (Mode == Activity.Roost && IsInFlyChain(fly))
            BreakHangChain(danger, DesertBatflyTuning.RetreatTicks);
        ClearRecoveryNavigation();
        fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "R3 ImmediateDanger owner");
        hasSlot = false;
        attachedChunk = null;
        Target = null;
        SetMode(Activity.Escape);
        if (danger != null) escapeFrom = danger.mainBodyChunk.pos;
        Steer(
            fly.mainBodyChunk.pos + Custom.DirVec(escapeFrom, fly.mainBodyChunk.pos) * 160f + Vector2.up * 50f,
            8f);
        return true;
    }

    internal bool ExecuteCombatOwned()
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat) ||
            fly.room == null || fly.dead || !fly.Consious || fly.Injury.BlocksCombat || !Valid(Target))
            return false;

        if (Mode == Activity.Roost) StopRoost(true);
        if (Mode is Activity.Flight or Activity.Cooldown or Activity.Roost) SetMode(Activity.Observe);

        Vector2 center = Target.mainBodyChunk.pos;
        float distance = Vector2.Distance(fly.mainBodyChunk.pos, center);

        switch (Mode)
        {
            case Activity.Observe:
                Steer(center + Orbit(150f, 90f), 4.5f);
                if (ticks > fly.Personality.ObserveDuration)
                {
                    bool counter = Target == attacker && memory > 0;
                    bool grudge = Target is Player targetPlayer && IsRememberedPlayer(targetPlayer) &&
                                  !IsTraumatizedPlayer(targetPlayer);
                    if ((counter || grudge) && Target is Player retaliationTarget &&
                        !IsTraumatizedPlayer(retaliationTarget) && retaliationCharges > 0 && retaliationRecovery <= 0)
                    {
                        float memoryBoost = grudge ? fly.DesertState.GrabMemoryStrength * 0.18f : 0f;
                        if (Random.value < Mathf.Clamp01(
                                fly.Personality.RetaliationChance * fly.Injury.AggressionScale + memoryBoost) && AcquireSlot())
                        {
                            retaliationCharges--;
                            retaliationDirection = Custom.DirVec(fly.mainBodyChunk.pos, retaliationTarget.mainBodyChunk.pos);
                            SetMode(Activity.RetaliationCharge);
                            break;
                        }
                    }

                    float effectiveAttackThirst = Mathf.Lerp(
                        DesertBatflyTuning.AttackThirst,
                        DesertBatflyTuning.ObserveThirst,
                        fly.Personality.AggressionDrive * 0.35f);
                    bool thirsty = fly.DesertState.Thirst * fly.DesertState.GriefAttackScale *
                                   fly.Injury.AggressionScale > effectiveAttackThirst;
                    bool revengeDrink = grudge && fly.DesertState.GrabMemoryStrength > 0.12f;
                    bool wantsRealAttack = thirsty || counter || revengeDrink;
                    float fakeChance = Mathf.Clamp01(fly.Personality.FakeDiveChance);
                    if (grudge) fakeChance *= Mathf.Lerp(0.8f, 0.48f, fly.DesertState.GrabMemoryStrength);
                    if (counter) fakeChance *= 0.82f;
                    if (Target is Player learnedTarget)
                        fakeChance = DesertBatflyThreatTactics.AdjustFakeDiveChance(fly, learnedTarget, fakeChance);

                    if (!wantsRealAttack || Random.value < fakeChance) SetMode(Activity.FakeDive);
                    else if (AcquireSlot()) SetMode(Activity.Approach);
                    else ticks = fly.Personality.ObserveDuration / 2;
                }
                break;

            case Activity.Approach:
                Steer(center + Vector2.up * 100f, 6f + fly.Personality.AggressionDrive * 1.2f);
                if (ticks > DesertBatflyTuning.ApproachTicks || distance < 110f) SetMode(Activity.Circle);
                break;

            case Activity.Circle:
                Steer(center + Orbit(95f, 65f), 6.5f + fly.Personality.AggressionDrive);
                if (ticks > DesertBatflyTuning.CircleTicks) SetMode(Activity.Dive);
                break;

            case Activity.FakeDive:
                if (distance < 52f || ticks > DesertBatflyTuning.FakeDivePullUpTicks)
                    ticks = Mathf.Max(DesertBatflyTuning.FakeDivePullUpTicks + 1, ticks);
                Steer(
                    PullingUp
                        ? center + Vector2.up * 160f + Custom.DirVec(center, fly.mainBodyChunk.pos) * 80f
                        : center,
                    PullingUp ? 10f : 12f);
                if (ticks > DesertBatflyTuning.FakeDiveTicks) SetMode(Activity.Observe);
                break;

            case Activity.Dive:
                Steer(center + Target.mainBodyChunk.vel * 1.5f, 12f + fly.Personality.AggressionDrive * 1.5f);
                BodyChunk contact = FindContact();
                if (contact != null && unseen == 0)
                {
                    attachedChunk = contact;
                    attachOffset = Custom.DirVec(contact.pos, fly.mainBodyChunk.pos) *
                                   (contact.rad + fly.mainBodyChunk.rad * 0.5f);
                    drainedWater = 0f;
                    SetMode(Activity.Attach);
                }
                else if (ticks > DesertBatflyTuning.DiveTicks) Finish(false);
                break;

            case Activity.Attach:
                fly.movMode = Fly.MovementMode.Passive;
                if (ticks >= DesertBatflyTuning.AttachTicks) Finish(drainedWater > 0.001f);
                break;

            case Activity.RetaliationCharge:
                if (Target is not Player chargeTarget || IsTraumatizedPlayer(chargeTarget))
                {
                    FinishRetaliation(false);
                    break;
                }
                Vector2 predicted = chargeTarget.mainBodyChunk.pos + chargeTarget.mainBodyChunk.vel * 1.15f;
                Steer(predicted, fly.Personality.RetaliationSpeed);
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
                else if (ticks > DesertBatflyTuning.RetaliationChargeTicks) FinishRetaliation(false);
                break;

            case Activity.Interfere:
                fly.movMode = Fly.MovementMode.Passive;
                if (ticks >= fly.Personality.RetaliationContactDuration) FinishRetaliation(true);
                break;

            default:
                return false;
        }
        return true;
    }

    internal bool ExecuteRoostOwned()
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Roost) || fly.room == null)
            return false;
        UpdateRoost();
        return Mode == Activity.Roost || fly.AI.behavior == FlyAI.Behavior.Chain;
    }

    internal bool ExecuteOrdinaryOwned()
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Ordinary) || fly.room == null)
            return false;
        if (Mode == Activity.Roost || fly.AI.behavior == FlyAI.Behavior.Chain)
        {
            UpdateRoost();
            return true;
        }
        if (Mode == Activity.Cooldown && fly.DesertState.Cooldown <= 0) SetMode(Activity.Flight);
        // Ordinary owns no second flight motor in R3. Vanilla FlyAI has already produced the
        // neutral goal for this frame; this executor only permits species-local roost entry.
        if (!fly.Personality.Aggressive || !GriefAllowsHarass() || Target == null)
            UpdateRoost();
        return true;
    }

'''
ai = ai[:start] + new_method + ai[end:]
# Add same-tick owner guard to Steer: all remaining callers are explicit owner executors.
old = '''    private void Steer(Vector2 goal, float speed)\n    {\n        fly.Injury.NominalFlightSpeed = speed;'''
new = '''    private void Steer(Vector2 goal, float speed)\n    {\n        bool owned = DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.ImmediateDanger) ||\n                     DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.InjuryRecovery) ||\n                     DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat);\n        if (!owned) return;\n        fly.Injury.NominalFlightSpeed = speed;'''
ai = replace_once(ai, old, new, 'Steer owner guard')
AI.write_text(ai, encoding='utf-8')

threat = THREAT.read_text(encoding='utf-8')
threat = replace_once(threat, '    internal static void Update(DesertBatfly bat)\n    {',
'''    internal static void RefreshState(DesertBatfly bat)\n    {''', 'Threat Update rename')
threat = replace_once(threat, '''        TrackEncounter(bat, state);\n        ApplyTacticalAdjustment(bat, state);\n        state.PreviousMode = bat.DesertAI.Mode;\n    }''',
'''        TrackEncounter(bat, state);\n        state.PreviousMode = bat.DesertAI.Mode;\n    }\n\n    internal static void ApplyOwnedTacticalModifier(DesertBatfly bat)\n    {\n        if (bat == null || !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Combat))\n            return;\n        ApplyTacticalAdjustment(bat, StateFor(bat));\n    }''', 'Threat split apply')
THREAT.write_text(threat, encoding='utf-8')

executor = '''namespace DryCycle.Creatures.DesertBatfly;\n\n/// <summary>R3 gateway for the remaining DesertBatflyAI-owned frame decisions.</summary>\ninternal static class DB_CoreDecisionExecutor\n{\n    internal static bool TryExecute(DesertBatfly bat, in DB_BehaviorResolution resolution)\n    {\n        if (bat?.DesertAI == null || !resolution.Resolved ||\n            !DB_BehaviorArbiter.IsPrimaryOwner(bat, resolution.PrimaryOwner))\n            return false;\n\n        return resolution.PrimaryOwner switch\n        {\n            DB_BehaviorOwner.ImmediateDanger => bat.DesertAI.ExecuteImmediateDangerOwned(),\n            DB_BehaviorOwner.Combat => ExecuteCombat(bat),\n            DB_BehaviorOwner.Roost => bat.DesertAI.ExecuteRoostOwned(),\n            DB_BehaviorOwner.Ordinary => bat.DesertAI.ExecuteOrdinaryOwned(),\n            _ => false\n        };\n    }\n\n    private static bool ExecuteCombat(DesertBatfly bat)\n    {\n        if (!bat.DesertAI.ExecuteCombatOwned()) return false;\n        DesertBatflyThreatRuntime.ApplyOwnedTacticalModifier(bat);\n        return true;\n    }\n}\n'''
EXEC.write_text(executor, encoding='utf-8')

hooks = HOOKS.read_text(encoding='utf-8')
# unavailable path now refreshes state, never legacy Update.
hooks = replace_once(hooks, '            suspended.DesertAI.Update();', '            suspended.DesertAI.RefreshDecisionState();', 'suspended refresh')
anchor = '''        DesertBatflyEnvironmentalIntegration.Register(desert);\n        DesertBatflyEnvironmentalBehavior.RefreshInfluence(desert);\n        DesertBatflySocialLife.RefreshState(desert);\n\n        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);'''
replacement = '''        DesertBatflyEnvironmentalIntegration.Register(desert);\n        desert.DesertAI.RefreshDecisionState();\n        DesertBatflyThreatRuntime.RefreshState(desert);\n        DesertBatflyEnvironmentalBehavior.RefreshInfluence(desert);\n        DesertBatflySocialLife.RefreshState(desert);\n\n        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);'''
hooks = replace_once(hooks, anchor, replacement, 'pre-arbiter state refresh')
legacy = '''        // R3 owner-gated executors now cover InjuryRecovery, Travel, Environment,\n        // Vengeance, ImmediateProjectileEvade and Social. Combat/ordinary remain.\n        desert.DesertAI.Update();\n        DesertBatflyThreatRuntime.Update(desert);\n        DesertBatflyThreatTrace.Sample(desert);\n        DesertBatflySignalRuntime.Update(desert);\n        DesertBatflySocialLife.SampleTrace(desert);\n        DesertBatflyDebugTrace.Sample(desert);'''
core = '''        if (ownership.PrimaryOwner is DB_BehaviorOwner.ImmediateDanger or DB_BehaviorOwner.Combat or\n            DB_BehaviorOwner.Roost or DB_BehaviorOwner.Ordinary)\n        {\n            if (DB_CoreDecisionExecutor.TryExecute(desert, ownership))\n            {\n                DesertBatflyThreatTrace.Sample(desert);\n                DesertBatflySignalRuntime.Update(desert);\n                DesertBatflySocialLife.SampleTrace(desert);\n                DesertBatflyDebugTrace.Sample(desert);\n                return;\n            }\n\n            ownership = DB_BehaviorArbiter.ResolveFrame(\n                desert, ownership.PrimaryOwner,\n                "core AI executor yielded after current state recheck");\n        }\n\n        // NativeSpecial / creature-special owners intentionally keep the vanilla result.\n        // All ordinary DryCycle movement writers above are now reached through PrimaryOwner.\n        DesertBatflyThreatTrace.Sample(desert);\n        DesertBatflySignalRuntime.Update(desert);\n        DesertBatflySocialLife.SampleTrace(desert);\n        DesertBatflyDebugTrace.Sample(desert);'''
hooks = replace_once(hooks, legacy, core, 'legacy core pipeline')
HOOKS.write_text(hooks, encoding='utf-8')

# Managed reflection regression additions.
test = TEST.read_text(encoding='utf-8')
test = replace_once(test,
'''        Type environmentBehavior = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalBehavior", true);\n        Type desertAI = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);''',
'''        Type environmentBehavior = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalBehavior", true);\n        Type coreExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CoreDecisionExecutor", true);\n        Type threatRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatRuntime", true);\n        Type desertAI = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);''', 'test type refs')
test = replace_once(test,
'''        Check(MethodCallOffset(desertAI.GetMethod("ExecuteInjuryRecoveryOwned", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 InjuryRecovery executor requires same-tick PrimaryOwner");''',
'''        Check(MethodCallOffset(desertAI.GetMethod("ExecuteInjuryRecoveryOwned", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 InjuryRecovery executor requires same-tick PrimaryOwner");\n        Check(desertAI.GetMethod("Update", Flags) == null &&\n              desertAI.GetMethod("RefreshDecisionState", Flags) != null &&\n              desertAI.GetMethod("ExecuteImmediateDangerOwned", Flags) != null &&\n              desertAI.GetMethod("ExecuteCombatOwned", Flags) != null &&\n              desertAI.GetMethod("ExecuteRoostOwned", Flags) != null &&\n              desertAI.GetMethod("ExecuteOrdinaryOwned", Flags) != null,\n            "Task14 R3 DesertBatflyAI no longer exposes one mixed decision/execution Update");\n        Check(coreExecutor.GetMethod("TryExecute", Flags) != null &&\n              MethodCallOffset(coreExecutor.GetMethod("TryExecute", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 core AI owner gateway requires same-tick PrimaryOwner");\n        Check(threatRuntime.GetMethod("RefreshState", Flags) != null &&\n              threatRuntime.GetMethod("ApplyOwnedTacticalModifier", Flags) != null &&\n              threatRuntime.GetMethod("Update", Flags) == null &&\n              MethodCallOffset(threatRuntime.GetMethod("ApplyOwnedTacticalModifier", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 Threat runtime refresh is split from Combat-owned tactical goal modifier");''', 'test core assertions')
test = test.replace('Social/combat/projectile/ordinary migration remains open.',
                    'Core ImmediateDanger/Combat/Roost/Ordinary are owner-gated; vanilla pre-arbitration writer audit remains open.')
TEST.write_text(test, encoding='utf-8')

status = STATUS.read_text(encoding='utf-8')
status += '''\n\n======================================================================\nR3-CLOSED-08 — Core DesertBatflyAI owner gateway（代码侧已收敛）\n======================================================================\n\nDesertBatflyAI 不再暴露混合式 Update。当前拆为：\n\n- RefreshDecisionState：感知 / timer / target / mode intent 刷新；\n- ExecuteImmediateDangerOwned；\n- ExecuteCombatOwned；\n- ExecuteRoostOwned；\n- ExecuteOrdinaryOwned。\n\n新增 DB_CoreDecisionExecutor，只有 same-tick PrimaryOwner 才能进入上述执行器。\nSteer 本身再次验证 ImmediateDanger / InjuryRecovery / Combat owner，防止未来旁路调用。\n\nTask11 ThreatRuntime 同时拆为：\n\n- RefreshState：仲裁前刷新 acute cue / learned threat facts；\n- ApplyOwnedTacticalModifier：仅 PrimaryOwner=Combat 时允许修改战术 localGoal。\n\n这关闭了旧 DesertBatflyAI.Update 与 ThreatRuntime 后置二次抢写的代码入口。\n\n仍 OPEN：vanilla FlyAI.Update 当前仍先于 R3 Arbiter 运行，因此 Ordinary/Native goal 的\n“vanilla pre-arbitration writer”还需单独审计和收口；在该项完成前 R3 仍不可宣称最终 single writer。\n'''
STATUS.write_text(status, encoding='utf-8')

# one-shot cleanup
Path('scripts/task14_r3_core_ai_owner_apply.py').unlink()
Path('.github/workflows/task14-r3-core-ai-owner-one-shot.yml').unlink()
