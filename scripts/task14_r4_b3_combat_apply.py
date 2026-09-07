from pathlib import Path
import re

ROOT = Path('.')


def read(path):
    return (ROOT / path).read_text(encoding='utf-8')


def write(path, text):
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding='utf-8')


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{label}: expected 1 match, found {count}')
    return text.replace(old, new, 1)


def method_span(text, anchor):
    start = text.find(anchor)
    if start < 0:
        raise SystemExit(f'method anchor not found: {anchor}')
    line_start = text.rfind('\n', 0, start) + 1
    brace = text.find('{', start)
    if brace < 0:
        raise SystemExit(f'opening brace not found: {anchor}')
    i = brace
    depth = 0
    state = 'code'
    while i < len(text):
        c = text[i]
        n = text[i + 1] if i + 1 < len(text) else ''
        if state == 'code':
            if c == '/' and n == '/': state = 'line'; i += 2; continue
            if c == '/' and n == '*': state = 'block'; i += 2; continue
            if c == '"': state = 'string'; i += 1; continue
            if c == "'": state = 'char'; i += 1; continue
            if c == '{': depth += 1
            elif c == '}':
                depth -= 1
                if depth == 0: return line_start, i + 1
            i += 1; continue
        if state == 'line':
            if c == '\n': state = 'code'
            i += 1; continue
        if state == 'block':
            if c == '*' and n == '/': state = 'code'; i += 2; continue
            i += 1; continue
        if state == 'string':
            if c == '\\': i += 2; continue
            if c == '"': state = 'code'
            i += 1; continue
        if state == 'char':
            if c == '\\': i += 2; continue
            if c == "'": state = 'code'
            i += 1; continue
    raise SystemExit(f'unclosed method: {anchor}')


def replace_method(path, anchor, replacement):
    text = read(path)
    start, end = method_span(text, anchor)
    write(path, text[:start] + replacement.rstrip() + text[end:])


def remove_method(path, anchor):
    text = read(path)
    start, end = method_span(text, anchor)
    while end < len(text) and text[end] == '\n': end += 1
    write(path, text[:start] + text[end:])


# -----------------------------------------------------------------------------
# New coherent Combat execution/contact runtime.
# Target selection stays in DesertBatflyAI until B4, exposed through compatibility properties.
# -----------------------------------------------------------------------------
combat_runtime = r'''using DryCycle.Thirst;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R4 formal combat execution owner. Target discovery/motivation is migrated separately in B4;
/// this runtime owns combat phase progression, AttackSlot participation, contact state and
/// Attach/Interfere special physics. Ordinary approach/dive flight still goes through
/// DB_FlightMotor; contact pinning/impulses are explicit special physics.
/// </summary>
internal sealed class DB_CombatRuntime
{
    private readonly DesertBatflyAI ai;
    private readonly DesertBatfly fly;

    private int ticks;
    private int unseen;
    private int interest;
    private bool hasSlot;
    private float drainedWater;
    private Vector2 attachOffset;
    private Vector2 retaliationDirection;
    private BodyChunk attachedChunk;

    internal DB_CombatRuntime(DesertBatflyAI ai, DesertBatfly fly)
    {
        this.ai = ai;
        this.fly = fly;
    }

    internal bool HasSlot => hasSlot;
    internal bool PullingUp => ai.Mode == DesertBatflyAI.Activity.FakeDive &&
                               ticks > DesertBatflyTuning.FakeDivePullUpTicks;
    internal bool FormalAttack => hasSlot && ai.Mode is
        DesertBatflyAI.Activity.Approach or DesertBatflyAI.Activity.Circle or
        DesertBatflyAI.Activity.Dive or DesertBatflyAI.Activity.Attach or
        DesertBatflyAI.Activity.RetaliationCharge or DesertBatflyAI.Activity.Interfere;

    internal void Reset()
    {
        ticks = 0;
        unseen = 0;
        interest = 0;
        hasSlot = false;
        drainedWater = 0f;
        attachOffset = Vector2.zero;
        retaliationDirection = Vector2.zero;
        attachedChunk = null;
    }

    internal void OnModeChanged(DesertBatflyAI.Activity next)
    {
        ticks = 0;
        if (next is not (DesertBatflyAI.Activity.Attach or DesertBatflyAI.Activity.Interfere))
        {
            attachedChunk = null;
            attachOffset = Vector2.zero;
        }
        if (next is not (DesertBatflyAI.Activity.Observe or DesertBatflyAI.Activity.Approach or
            DesertBatflyAI.Activity.Circle or DesertBatflyAI.Activity.FakeDive or
            DesertBatflyAI.Activity.Dive or DesertBatflyAI.Activity.RetaliationCharge or
            DesertBatflyAI.Activity.Attach or DesertBatflyAI.Activity.Interfere))
        {
            hasSlot = false;
            unseen = 0;
            interest = 0;
            drainedWater = 0f;
        }
    }

    internal void ClearAttackState()
    {
        hasSlot = false;
        attachedChunk = null;
        attachOffset = Vector2.zero;
        retaliationDirection = Vector2.zero;
        drainedWater = 0f;
        interest = 0;
        unseen = 0;
    }

    internal bool TryExecuteOwned()
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat) ||
            fly.room == null || fly.dead || !fly.Consious || ai.RestrainedByNonFly() ||
            fly.inShortcut || fly.Injury.BlocksCombat || !ai.Valid(ai.Target) ||
            ai.Mode is not (DesertBatflyAI.Activity.Observe or DesertBatflyAI.Activity.Approach or
                DesertBatflyAI.Activity.Circle or DesertBatflyAI.Activity.FakeDive or
                DesertBatflyAI.Activity.Dive or DesertBatflyAI.Activity.Attach or
                DesertBatflyAI.Activity.RetaliationCharge or DesertBatflyAI.Activity.Interfere))
            return false;

        ticks++;
        DB_VisibilityChannel targetChannel = ai.Target is Player
            ? DB_VisibilityChannel.Player
            : DB_VisibilityChannel.Creature;
        if (!DB_VisibilityPolicy.CanObserve(fly, ai.Target.mainBodyChunk.pos, 430f, targetChannel))
            unseen++;
        else
            unseen = 0;

        if (++interest > DesertBatflyTuning.InterestTicks || unseen > 35 ||
            !Custom.DistLess(fly.mainBodyChunk.pos, ai.Target.mainBodyChunk.pos, 430f))
        {
            Finish(false);
            return true;
        }

        Vector2 center = ai.Target.mainBodyChunk.pos;
        float distance = Vector2.Distance(fly.mainBodyChunk.pos, center);

        switch (ai.Mode)
        {
            case DesertBatflyAI.Activity.Observe:
                ai.SteerOwned(center + Orbit(150f, 90f), 4.5f, DB_BehaviorOwner.Combat);
                if (ticks > fly.Personality.ObserveDuration)
                {
                    bool counter = ai.Target == ai.CombatAttacker && ai.CombatMemory > 0;
                    bool grudge = ai.Target is Player targetPlayer &&
                                  ai.IsRememberedPlayer(targetPlayer) &&
                                  !ai.IsTraumatizedPlayer(targetPlayer);

                    if ((counter || grudge) && ai.Target is Player retaliationTarget &&
                        !ai.IsTraumatizedPlayer(retaliationTarget) &&
                        ai.CombatRetaliationCharges > 0 && ai.CombatRetaliationRecovery <= 0)
                    {
                        float memoryBoost = grudge ? fly.DesertState.GrabMemoryStrength * 0.18f : 0f;
                        if (Random.value < Mathf.Clamp01(
                                fly.Personality.RetaliationChance * fly.Injury.AggressionScale + memoryBoost) &&
                            AcquireSlot())
                        {
                            ai.CombatRetaliationCharges--;
                            retaliationDirection = Custom.DirVec(
                                fly.mainBodyChunk.pos,
                                retaliationTarget.mainBodyChunk.pos);
                            ai.SetMode(DesertBatflyAI.Activity.RetaliationCharge);
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
                    if (grudge)
                        fakeChance *= Mathf.Lerp(0.8f, 0.48f, fly.DesertState.GrabMemoryStrength);
                    if (counter) fakeChance *= 0.82f;
                    if (ai.Target is Player learnedTarget)
                        fakeChance = DesertBatflyThreatTactics.AdjustFakeDiveChance(
                            fly, learnedTarget, fakeChance);

                    if (!wantsRealAttack || Random.value < fakeChance)
                        ai.SetMode(DesertBatflyAI.Activity.FakeDive);
                    else if (AcquireSlot())
                        ai.SetMode(DesertBatflyAI.Activity.Approach);
                    else
                        ticks = fly.Personality.ObserveDuration / 2;
                }
                break;

            case DesertBatflyAI.Activity.Approach:
                ai.SteerOwned(
                    center + Vector2.up * 100f,
                    6f + fly.Personality.AggressionDrive * 1.2f,
                    DB_BehaviorOwner.Combat);
                if (ticks > DesertBatflyTuning.ApproachTicks || distance < 110f)
                    ai.SetMode(DesertBatflyAI.Activity.Circle);
                break;

            case DesertBatflyAI.Activity.Circle:
                ai.SteerOwned(
                    center + Orbit(95f, 65f),
                    6.5f + fly.Personality.AggressionDrive,
                    DB_BehaviorOwner.Combat);
                if (ticks > DesertBatflyTuning.CircleTicks)
                    ai.SetMode(DesertBatflyAI.Activity.Dive);
                break;

            case DesertBatflyAI.Activity.FakeDive:
                if (distance < 52f || ticks > DesertBatflyTuning.FakeDivePullUpTicks)
                    ticks = Mathf.Max(DesertBatflyTuning.FakeDivePullUpTicks + 1, ticks);
                ai.SteerOwned(
                    PullingUp
                        ? center + Vector2.up * 160f + Custom.DirVec(center, fly.mainBodyChunk.pos) * 80f
                        : center,
                    PullingUp ? 10f : 12f,
                    DB_BehaviorOwner.Combat);
                if (ticks > DesertBatflyTuning.FakeDiveTicks)
                    ai.SetMode(DesertBatflyAI.Activity.Observe);
                break;

            case DesertBatflyAI.Activity.Dive:
                ai.SteerOwned(
                    center + ai.Target.mainBodyChunk.vel * 1.5f,
                    12f + fly.Personality.AggressionDrive * 1.5f,
                    DB_BehaviorOwner.Combat);
                BodyChunk contact = FindContact();
                if (contact != null && unseen == 0)
                {
                    attachedChunk = contact;
                    attachOffset = Custom.DirVec(contact.pos, fly.mainBodyChunk.pos) *
                        (contact.rad + fly.mainBodyChunk.rad * 0.5f);
                    drainedWater = 0f;
                    ai.SetMode(DesertBatflyAI.Activity.Attach);
                }
                else if (ticks > DesertBatflyTuning.DiveTicks)
                    Finish(false);
                break;

            case DesertBatflyAI.Activity.Attach:
                fly.movMode = Fly.MovementMode.Passive;
                if (ticks >= DesertBatflyTuning.AttachTicks)
                    Finish(drainedWater > 0.001f);
                break;

            case DesertBatflyAI.Activity.RetaliationCharge:
                if (ai.Target is not Player chargeTarget || ai.IsTraumatizedPlayer(chargeTarget))
                {
                    FinishRetaliation(false);
                    break;
                }

                Vector2 predicted = chargeTarget.mainBodyChunk.pos + chargeTarget.mainBodyChunk.vel * 1.15f;
                ai.SteerOwned(predicted, fly.Personality.RetaliationSpeed, DB_BehaviorOwner.Combat);
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
                    ai.SetMode(DesertBatflyAI.Activity.Interfere);
                }
                else if (ticks > DesertBatflyTuning.RetaliationChargeTicks)
                    FinishRetaliation(false);
                break;

            case DesertBatflyAI.Activity.Interfere:
                fly.movMode = Fly.MovementMode.Passive;
                if (ticks >= fly.Personality.RetaliationContactDuration)
                    FinishRetaliation(true);
                break;
        }
        return true;
    }

    internal void AfterPhysics(bool eu)
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat)) return;

        if (ai.Mode == DesertBatflyAI.Activity.Interfere)
        {
            UpdateInterference(eu);
            return;
        }

        if (ai.Mode != DesertBatflyAI.Activity.Attach) return;
        if (!ai.Valid(ai.Target) || attachedChunk == null || !fly.Consious ||
            ai.RestrainedByNonFly() || fly.inShortcut || ai.Target.inShortcut || !hasSlot ||
            !Custom.DistLess(fly.mainBodyChunk.pos, attachedChunk.pos, 70f))
        {
            Finish(drainedWater > 0.001f);
            return;
        }

        if (ai.Target is Player attachedPlayer && ai.IsTraumatizedPlayer(attachedPlayer))
        {
            ai.SuppressHostility(attachedPlayer);
            ai.BeginCombatEscape(attachedPlayer.mainBodyChunk.pos, 80);
            return;
        }

        Vector2 position = attachedChunk.pos + attachOffset;
        if (fly.room.GetTile(position).Solid || !fly.room.VisualContact(fly.mainBodyChunk.pos, position))
        {
            Finish(drainedWater > 0.001f);
            return;
        }

        fly.mainBodyChunk.MoveFromOutsideMyUpdate(eu, position);
        fly.mainBodyChunk.vel = attachedChunk.vel;

        if (ticks >= DesertBatflyTuning.DrainStartTicks && ticks <= DesertBatflyTuning.DrainEndTicks)
        {
            float amount = DesertBatflyTuning.AttackWaterPerSecond / ThirstConstants.SimulationTicksPerSecond;
            bool transferred = true;
            if (ai.Target is Player player)
            {
                transferred = ThirstStore.RemoveRuntime(
                    player,
                    amount / ThirstConstants.WaterValuePerPip);
                player.showKarmaFoodRainTime = Mathf.Max(
                    player.showKarmaFoodRainTime,
                    ThirstConstants.HydrationLossHudHoldFrames);
            }

            if (transferred)
            {
                drainedWater += amount;
                float fullWindowWater = DesertBatflyTuning.AttackWaterPerSecond *
                    (DesertBatflyTuning.DrainEndTicks - DesertBatflyTuning.DrainStartTicks + 1f) /
                    ThirstConstants.SimulationTicksPerSecond;
                fly.DesertState.Thirst = Mathf.Max(
                    0f,
                    fly.DesertState.Thirst - DesertBatflyTuning.DrainRelief *
                    (amount / Mathf.Max(0.001f, fullWindowWater)));
                fly.DesertState.Cooldown = DesertBatflyTuning.Cooldown;
            }
        }
    }

    private BodyChunk FindContact()
    {
        if (ai.Target?.bodyChunks == null) return null;
        foreach (BodyChunk chunk in ai.Target.bodyChunks)
            if (Custom.DistLess(
                    chunk.pos,
                    fly.mainBodyChunk.pos,
                    chunk.rad + fly.mainBodyChunk.rad + 3f))
                return chunk;
        return null;
    }

    private void ApplyInitialRetaliationImpact(Player player)
    {
        if (player?.bodyChunks == null) return;
        Vector2 impulse = retaliationDirection * fly.Personality.RetaliationImpact;
        foreach (BodyChunk chunk in player.bodyChunks)
            chunk.vel += impulse;
    }

    private void UpdateInterference(bool eu)
    {
        if (ai.Target is not Player player || ai.IsTraumatizedPlayer(player) ||
            !ai.Valid(player) || attachedChunk == null || !fly.Consious ||
            ai.RestrainedByNonFly() || fly.inShortcut || player.inShortcut || !hasSlot ||
            !Custom.DistLess(fly.mainBodyChunk.pos, attachedChunk.pos, 75f))
        {
            FinishRetaliation(false);
            return;
        }

        Vector2 position = attachedChunk.pos + attachOffset;
        if (fly.room.GetTile(position).Solid)
        {
            FinishRetaliation(false);
            return;
        }

        fly.mainBodyChunk.MoveFromOutsideMyUpdate(eu, position);
        fly.mainBodyChunk.vel = attachedChunk.vel;

        float drag = fly.Personality.RetaliationDrag;
        Vector2 push = retaliationDirection * fly.Personality.RetaliationPush;
        foreach (BodyChunk chunk in player.bodyChunks)
        {
            chunk.vel.x *= 1f - drag;
            chunk.vel.y *= 1f - drag * 0.22f;
            chunk.vel += push;
        }
    }

    private void FinishRetaliation(bool success)
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat)) return;
        Vector2 from = ai.Target?.mainBodyChunk.pos ?? fly.mainBodyChunk.pos - Vector2.up;
        ClearAttackState();
        ai.ClearCombatTarget();
        fly.DesertState.Cooldown = Mathf.Max(fly.DesertState.Cooldown, DesertBatflyTuning.RetaliationCooldown);
        ai.CombatRetaliationRecovery = success ? 120 : 75;
        ai.BeginCombatEscape(from, success ? 55 : 40);
        fly.mainBodyChunk.vel +=
            Custom.DirVec(from, fly.mainBodyChunk.pos) * 5.5f + Vector2.up * 2.5f;
    }

    private void Finish(bool success)
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.Combat)) return;
        Vector2 from = ai.Target?.mainBodyChunk.pos ?? fly.mainBodyChunk.pos - Vector2.up;
        ClearAttackState();
        ai.ClearCombatTarget();
        fly.DesertState.Cooldown = Mathf.Max(
            fly.DesertState.Cooldown,
            success ? DesertBatflyTuning.Cooldown : DesertBatflyTuning.FailedCooldown);
        ai.BeginCombatEscape(from, 75);
        fly.mainBodyChunk.vel +=
            Custom.DirVec(from, fly.mainBodyChunk.pos) * 5f + Vector2.up * 3f;
    }

    private bool AcquireSlot()
    {
        if (fly.Injury.BlocksCombat) { hasSlot = false; return false; }
        if (ai.Target is Player player && ai.IsTraumatizedPlayer(player))
        {
            hasSlot = false;
            return false;
        }

        int count = 0;
        DB_RoomContext context = DB_RoomContext.For(fly.room);
        var bats = context?.Bats;
        if (bats != null)
        {
            for (int i = 0; i < bats.Count; i++)
            {
                DesertBatfly other = bats[i];
                if (other == null || other == fly || !other.Consious ||
                    other.grabbedBy.Count != 0 || other.DesertAI.Target != ai.Target ||
                    !other.DesertAI.FormalAttack)
                    continue;
                count++;
            }
        }

        hasSlot = count < DesertBatflyTuning.AttackSlots;
        return hasSlot;
    }

    private Vector2 Orbit(float width, float height)
    {
        float angle = (fly.room.game.clock + (fly.Personality.VisualSeed & 1023)) * 0.025f;
        return new Vector2(
            Mathf.Cos(angle) * width,
            55f + Mathf.Sin(angle) * height * 0.45f);
    }
}
'''
write('src/Creatures/DesertBatfly/Combat/DB_CombatRuntime.cs', combat_runtime)


# -----------------------------------------------------------------------------
# DesertBatflyAI: retain compatibility read surfaces, remove execution/contact implementation.
# -----------------------------------------------------------------------------
path = 'src/Creatures/DesertBatfly/DesertBatflyAI.cs'
text = read(path)
text = replace_once(
    text,
    '    private readonly DesertBatfly fly;\n    internal bool HasImmediateDanger',
    '    private readonly DesertBatfly fly;\n    private readonly DB_CombatRuntime combat;\n    internal DB_CombatRuntime Combat => combat;\n    internal bool HasImmediateDanger',
    'combat field')
text = replace_once(
    text,
    '    internal Creature Target { get; private set; }\n\n    private Creature attacker;',
    '    internal Creature Target { get; private set; }\n\n    private Creature attacker;',
    'target compatibility no-op')
text = replace_once(
    text,
    '    private int memory, retreat, ticks, scan, pursuit, unseen, interest;\n    private int retaliationCharges, retaliationRecovery, recoverySearchCooldown;\n    private bool hasSlot, hasRoost;\n    private float drainedWater;\n    private Vector2 escapeFrom, attachOffset, roost, retaliationDirection;\n    private Vector2? recoveryRoostTarget;\n    private BodyChunk attachedChunk;\n\n    internal bool PullingUp => Mode == Activity.FakeDive &&\n                               ticks > DesertBatflyTuning.FakeDivePullUpTicks;\n    internal bool FormalAttack => hasSlot && Mode is\n        Activity.Approach or Activity.Circle or Activity.Dive or Activity.Attach or\n        Activity.RetaliationCharge or Activity.Interfere;\n',
    '    private int memory, retreat, ticks, scan, pursuit;\n    private int retaliationCharges, retaliationRecovery, recoverySearchCooldown;\n    private bool hasRoost;\n    private Vector2 escapeFrom, roost;\n    private Vector2? recoveryRoostTarget;\n\n    internal bool PullingUp => combat.PullingUp;\n    internal bool FormalAttack => combat.FormalAttack;\n    internal Creature CombatAttacker => attacker;\n    internal int CombatMemory => memory;\n    internal int CombatRetaliationCharges { get => retaliationCharges; set => retaliationCharges = Mathf.Max(0, value); }\n    internal int CombatRetaliationRecovery { get => retaliationRecovery; set => retaliationRecovery = Mathf.Max(0, value); }\n',
    'combat fields/properties')
text = replace_once(
    text,
    '    internal DesertBatflyAI(DesertBatfly fly)\n    {\n        this.fly = fly;\n    }',
    '    internal DesertBatflyAI(DesertBatfly fly)\n    {\n        this.fly = fly;\n        combat = new DB_CombatRuntime(this, fly);\n    }',
    'combat constructor')
write(path, text)

# Reset combat contact state when the realized room changes.
text = read(path)
text = replace_once(
    text,
    '        CancelAttack();\n        attacker = danger = null;',
    '        CancelAttack();\n        combat.Reset();\n        attacker = danger = null;',
    'ResetRoom combat reset')
write(path, text)

# Compatibility wrappers / runtime accessors.
replace_method(path, '    internal void CancelPhysicalAttack()', '''    internal void CancelPhysicalAttack()
    {
        if (Target != null || combat.HasSlot) CancelAttack();
        retaliationCharges = retaliationRecovery = 0;
    }''')
replace_method(path, '    internal bool ExecuteCombatOwned()', '''    internal bool ExecuteCombatOwned()
        => combat.TryExecuteOwned();''')
replace_method(path, '    internal void AfterPhysics(bool eu)', '''    // Compatibility surface for older tests/callers. Production calls Combat.AfterPhysics directly.
    internal void AfterPhysics(bool eu)
        => combat.AfterPhysics(eu);''')
replace_method(path, '    internal void CancelAttack()', '''    internal void CancelAttack()
    {
        combat.ClearAttackState();
        Target = null;
        SetMode(Activity.Flight);
    }''')

# Access used by DB_CombatRuntime.
text = read(path)
for old, new, label in [
    ('    private bool RestrainedByNonFly()\n', '    internal bool RestrainedByNonFly()\n', 'Restrained visibility'),
    ('    private bool IsRememberedPlayer(Player player)\n', '    internal bool IsRememberedPlayer(Player player)\n', 'remembered visibility'),
    ('    private bool IsTraumatizedPlayer(Player player)\n', '    internal bool IsTraumatizedPlayer(Player player)\n', 'trauma visibility'),
    ('    private void SetMode(Activity next)\n', '    internal void SetMode(Activity next)\n', 'SetMode visibility'),
    ('    private bool Valid(Creature creature)\n', '    internal bool Valid(Creature creature)\n', 'Valid visibility'),
    ('    private bool SteerOwned(Vector2 goal, float speed, DB_BehaviorOwner owner)\n', '    internal bool SteerOwned(Vector2 goal, float speed, DB_BehaviorOwner owner)\n', 'Steer visibility'),
]:
    text = replace_once(text, old, new, label)
write(path, text)

# Mode transition resets the independent combat timer/contact phase state.
text = read(path)
text = replace_once(
    text,
    '        Mode = next;\n        ticks = 0;\n    }',
    '        Mode = next;\n        ticks = 0;\n        combat.OnModeChanged(next);\n    }',
    'SetMode combat notification')
# Helpers for domain boundary.
insert_anchor = '    internal void RefreshDecisionState()\n'
helper = '''    internal void ClearCombatTarget() => Target = null;

    internal void BeginCombatEscape(Vector2 from, int retreatTicks)
    {
        escapeFrom = from;
        retreat = Mathf.Max(retreat, retreatTicks);
        SetMode(Activity.Escape);
    }

'''
text = replace_once(text, insert_anchor, helper + insert_anchor, 'combat helper insertion')
write(path, text)

# Replace direct contact reset blocks outside the moved methods.
text = read(path)
text = text.replace(
    '        hasRoost = false;\n        hasSlot = false;\n        attachedChunk = null;\n        Target = null;\n',
    '        hasRoost = false;\n        combat.ClearAttackState();\n        Target = null;\n')
text = text.replace(
    '                brain.hasRoost = false;\n                brain.hasSlot = false;\n                brain.attachedChunk = null;\n                brain.Target = null;\n                brain.drainedWater = 0f;\n                brain.interest = 0;\n',
    '                brain.hasRoost = false;\n                brain.combat.ClearAttackState();\n                brain.Target = null;\n')
write(path, text)

# Delete helpers now owned by DB_CombatRuntime.
for anchor in [
    '    private BodyChunk FindContact()',
    '    private void ApplyInitialRetaliationImpact(Player player)',
    '    private void UpdateInterference(bool eu)',
    '    private void FinishRetaliation(bool success)',
    '    private void Finish(bool success)',
    '    private bool AcquireSlot()',
    '    private Vector2 Orbit(float width, float height)',
]:
    remove_method(path, anchor)

# Ensure no moved execution/contact state leaked in the AI shell.
ai_text = read(path)
for identifier in ['attachedChunk', 'attachOffset', 'drainedWater', 'retaliationDirection', 'hasSlot', 'unseen', 'interest']:
    if re.search(rf'\b{identifier}\b', ai_text):
        raise SystemExit(f'AI shell still contains moved combat state identifier: {identifier}')


# -----------------------------------------------------------------------------
# Production executors should call DB_CombatRuntime directly, not AI compatibility wrappers.
# -----------------------------------------------------------------------------
path = 'src/Creatures/DesertBatfly/Runtime/DB_CombatExecutor.cs'
text = read(path)
text = replace_once(
    text,
    '        if (!bat.DesertAI.ExecuteCombatOwned()) return false;',
    '        if (!bat.DesertAI.Combat.TryExecuteOwned()) return false;',
    'CombatExecutor runtime call')
write(path, text)

path = 'src/Creatures/DesertBatfly/DesertBatfly.cs'
text = read(path)
text = replace_once(
    text,
    '            DesertAI.AfterPhysics(eu);',
    '            DesertAI.Combat.AfterPhysics(eu);',
    'Creature AfterPhysics runtime call')
write(path, text)


# -----------------------------------------------------------------------------
# Extend R4 tests/status to cover real Combat execution/contact extraction.
# -----------------------------------------------------------------------------
path = 'tests/DesertBatfly/Program.Task14R4.cs'
text = read(path)
text = replace_once(
    text,
    '        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);\n',
    '        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);\n        Type combatRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatRuntime", true);\n        Type combatExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatExecutor", true);\n',
    'test combat types')
insert = '''        Check(combatRuntime.GetMethod("TryExecuteOwned", Flags) != null &&
              combatRuntime.GetMethod("AfterPhysics", Flags) != null &&
              combatRuntime.GetProperty("FormalAttack", Flags) != null,
            "Task14 R4 Combat runtime owns formal phase execution, contact physics and formal-attack state");
        Check(MethodCallOffset(combatExecutor.GetMethod("TryExecute", Flags), combatRuntime, "TryExecuteOwned") >= 0,
            "Task14 R4 Combat executor calls DB_CombatRuntime rather than old AI state-machine implementation");
        Check(MethodCallOffset(creature.GetMethod("Update", Flags), combatRuntime, "AfterPhysics") >= 0,
            "Task14 R4 post-physics Attach/Interfere execution calls DB_CombatRuntime directly");
        Check(ai.GetMethod("FindContact", Flags) == null && ai.GetMethod("AcquireSlot", Flags) == null &&
              ai.GetMethod("UpdateInterference", Flags) == null && ai.GetMethod("Finish", Flags) == null,
            "Task14 R4 old AI shell no longer owns combat contact/slot/finish implementation");
'''
text = replace_once(
    text,
    '        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), motor, "Reset") >= 0 &&\n',
    insert + '        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), motor, "Reset") >= 0 &&\n',
    'test combat checks')
text = text.replace(
    'Task14 R4 B2: ordinary domain goal writers are centralized through FlightMotor; Combat responsibility extraction remains open.',
    'Task14 R4 B3: FlightMotor boundary is centralized and formal Combat execution/contact/AttackSlot responsibility is extracted from the AI shell; target selection/motivation remains for B4.')
write(path, text)

path = 'docs/Discussion/Task_14_R4_FlightMotorStatus.txt'
status = read(path)
status = status.replace('Revision: R4-B2 / 2026-09-07', 'Revision: R4-B3 / 2026-09-07')
status = status.replace('Status: 【R4 进行中 / B2 ordinary goal writer convergence】', 'Status: 【R4 进行中 / B3 Combat execution/contact extraction】')
status = status.replace(
    'R4-OPEN-01 — Combat state/motivation/contact responsibilities split out of DesertBatflyAI.\nR4-OPEN-02 — final source audit of every localGoal / sustained velocity writer after Combat extraction.\nR4-OPEN-03 — Observatory expose FlightMotor owner/goal/nominal speed and special-physics boundary.\nR4-OPEN-04 — managed compile/integration and Rain World live scenarios deferred to final validation stage per project decision.',
    'R4-B3 CLOSED — DB_CombatRuntime now owns formal phase execution, combat-local timers, AttackSlot counting, Attach/Interfere contact state and special contact physics. Production Combat executor and creature post-physics call it directly.\n\nR4-OPEN-01 — move target discovery / Harass motivation / retaliation preparation out of DesertBatflyAI into Combat domain.\nR4-OPEN-02 — final source audit of every localGoal / sustained velocity writer after Combat selection extraction.\nR4-OPEN-03 — Observatory expose FlightMotor owner/goal/nominal speed and special-physics boundary.\nR4-OPEN-04 — managed compile/integration and Rain World live scenarios deferred to final validation stage per project decision.')
status += '''\n\n======================================================================\n6. R4-B3 Combat responsibility extraction\n======================================================================\n\n新增 Combat/DB_CombatRuntime.cs。\n\n已迁出 DesertBatflyAI：\n- Observe/Approach/Circle/FakeDive/Dive/Attach/RetaliationCharge/Interfere 的 owner execution；\n- combat-local phase timer / unseen / interest；\n- AttackSlot membership/counting；\n- attached chunk / contact offset / drained water；\n- retaliation contact direction；\n- FindContact；\n- Attach post-physics；\n- Interfere post-physics；\n- initial retaliation impact；\n- Finish / FinishRetaliation 瞬时退出 impulse。\n\n特殊物理仍明确合法：Attach/Interfere 的 MoveFromOutsideMyUpdate + attachedChunk velocity 同步，以及完成攻击时一次性 retreat impulse。它们由 PrimaryOwner=Combat 和 special-physics state 约束，不属于第二套 ordinary FlightMotor。\n\nDesertBatflyAI 暂时保留 Target/attacker/memory/retaliation preparation、ScanCreatures/CanHarass 作为 B4 兼容选择层；Mode/Target/FormalAttack/PullingUp 对外查询面保持兼容，避免 Threat/Signals/Observatory 同批破坏。\n'''
write(path, status)

# Guard source structure.
ai = read('src/Creatures/DesertBatfly/DesertBatflyAI.cs')
combat = read('src/Creatures/DesertBatfly/Combat/DB_CombatRuntime.cs')
for required in ['TryExecuteOwned', 'AfterPhysics', 'AcquireSlot', 'UpdateInterference', 'FinishRetaliation', 'FindContact']:
    if required not in combat:
        raise SystemExit(f'combat runtime missing {required}')
for forbidden in ['private BodyChunk FindContact()', 'private bool AcquireSlot()', 'private void UpdateInterference(bool eu)', 'private void FinishRetaliation(bool success)', 'private void Finish(bool success)']:
    if forbidden in ai:
        raise SystemExit(f'AI still owns combat implementation: {forbidden}')
if 'bat.DesertAI.ExecuteCombatOwned()' in read('src/Creatures/DesertBatfly/Runtime/DB_CombatExecutor.cs'):
    raise SystemExit('CombatExecutor still calls old AI wrapper')
if 'DesertAI.AfterPhysics(eu)' in read('src/Creatures/DesertBatfly/DesertBatfly.cs'):
    raise SystemExit('Creature still calls old AI AfterPhysics wrapper')

# Migration machinery must not remain in final commit.
Path('scripts/task14_r4_b3_combat_apply.py').unlink()
wf = Path('.github/workflows/task14-r4-b3-combat-one-shot.yml')
if wf.exists(): wf.unlink()

print('R4 B3 combat extraction prepared successfully')
