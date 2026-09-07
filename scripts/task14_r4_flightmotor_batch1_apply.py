from pathlib import Path

AI = Path('src/Creatures/DesertBatfly/DesertBatflyAI.cs')
INJURY = Path('src/Creatures/DesertBatfly/DesertBatflyInjury.cs')
CREATURE = Path('src/Creatures/DesertBatfly/DesertBatfly.cs')
HOOKS = Path('src/Creatures/DesertBatfly/DesertBatflyHooks.cs')
ENV = Path('src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalBehavior.cs')
MOTOR = Path('src/Creatures/DesertBatfly/Runtime/DB_FlightMotor.cs')
FOG = Path('src/Creatures/DesertBatfly/Runtime/DB_FogGoalModifier.cs')
TEST = Path('tests/DesertBatfly/Program.Task14R4.cs')
DISPATCH = Path('tests/DesertBatfly/Program.RejectedTask02.cs')
STATUS = Path('docs/Discussion/Task_14_R4_FlightMotorStatus.txt')
WORKFLOW = Path('.github/workflows/task14-r4-flightmotor-batch1-one-shot.yml')
SCRIPT = Path('scripts/task14_r4_flightmotor_batch1_apply.py')


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{label}: expected 1 occurrence, found {count}')
    return text.replace(old, new, 1)


def replace_count(text, old, new, expected, label):
    count = text.count(old)
    if count != expected:
        raise SystemExit(f'{label}: expected {expected} occurrences, found {count}')
    return text.replace(old, new)

if MOTOR.exists() or FOG.exists() or TEST.exists() or STATUS.exists():
    raise SystemExit('R4 batch1 target already exists; refusing non-idempotent migration')

motor = r'''using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R4 single entry point for ordinary Desert Batfly flight steering.
/// It owns goal intent + requested speed, while Rain World still owns collision and Fly physics.
/// Special physics (grasp/shortcut/emergence/chain/attach/interfere/instant impulses) bypasses it.
/// </summary>
internal static class DB_FlightMotor
{
    private sealed class State
    {
        internal int Clock = int.MinValue;
        internal DB_BehaviorOwner Owner;
        internal Vector2 Goal;
        internal float NominalSpeed;
    }

    private static ConditionalWeakTable<DesertBatfly, State> states = new();

    internal static void Reset()
    {
        states = new ConditionalWeakTable<DesertBatfly, State>();
        DB_FogGoalModifier.Reset();
    }

    internal static void Forget(DesertBatfly bat)
    {
        if (bat == null) return;
        states.Remove(bat);
        DB_FogGoalModifier.Forget(bat);
    }

    internal static bool TrySteer(
        DesertBatfly bat,
        DB_BehaviorOwner owner,
        Vector2 goal,
        float nominalSpeed,
        bool preserveDijkstra = false,
        float response = 0.22f)
    {
        if (bat?.room == null || bat.AI == null || bat.mainBodyChunk == null ||
            bat.dead || !bat.Consious || bat.inShortcut || bat.Emergence?.Active == true ||
            bat.grabbedBy.Count > 0 || !DB_BehaviorArbiter.IsPrimaryOwner(bat, owner))
            return false;

        if (DB_BehaviorArbiter.TryGetResolution(bat, out DB_BehaviorResolution resolution) &&
            resolution.SpecialPhysicsOwner != DB_SpecialPhysicsOwner.None)
            return false;

        nominalSpeed = Mathf.Max(0.1f, nominalSpeed);
        response = Mathf.Clamp01(response);
        goal = DB_FogGoalModifier.ModifyGoal(bat, owner, goal);

        bat.LoseAllGrasps();
        bat.burrowOrHangSpot = null;
        if (bat.AI.behavior == FlyAI.Behavior.Chain)
            bat.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        else
            bat.AI.behavior = FlyAI.Behavior.Idle;
        if (!preserveDijkstra)
            bat.AI.followingDijkstraMap = -1;
        bat.movMode = Fly.MovementMode.BatFlight;

        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, goal);
        Vector2 probe = bat.mainBodyChunk.pos + direction * 25f;
        if (bat.room.GetTile(probe).Solid ||
            (bat.room.terrain != null && bat.room.terrain.Contains(probe)))
        {
            goal = bat.mainBodyChunk.pos + Vector2.up * 70f;
            nominalSpeed = Mathf.Min(nominalSpeed, 4f);
            direction = Custom.DirVec(bat.mainBodyChunk.pos, goal);
        }

        bat.AI.localGoal = goal;
        Vector2 requested = direction * nominalSpeed;
        bat.mainBodyChunk.vel = Vector2.Lerp(bat.mainBodyChunk.vel, requested, response);

        State state = states.GetOrCreateValue(bat);
        state.Clock = bat.room.game?.clock ?? int.MinValue;
        state.Owner = owner;
        state.Goal = goal;
        state.NominalSpeed = nominalSpeed;
        return true;
    }

    /// <summary>
    /// Final R4 injury-flight pass. This does not choose a goal; it only modifies the velocity
    /// produced by the selected owner/native Fly physics using the pure Injury modifier math.
    /// </summary>
    internal static void ApplyPostPhysics(DesertBatfly bat, Vector2 previousVelocity)
    {
        if (bat?.room == null || bat.mainBodyChunk == null || bat.dead || !bat.Consious ||
            bat.inShortcut || bat.grabbedBy.Count > 0 || bat.Emergence?.Active == true ||
            bat.AI?.behavior == FlyAI.Behavior.Chain || bat.movMode != Fly.MovementMode.BatFlight)
            return;

        float nominalSpeed = 12f;
        int clock = bat.room.game?.clock ?? int.MinValue;
        if (states.TryGetValue(bat, out State state) && state.Clock == clock && state.NominalSpeed > 0f)
            nominalSpeed = state.NominalSpeed;

        bat.mainBodyChunk.vel = bat.Injury.ModifyFlight(
            previousVelocity,
            bat.mainBodyChunk.vel,
            nominalSpeed);
    }

    internal static bool TryGetIntent(
        DesertBatfly bat,
        out DB_BehaviorOwner owner,
        out Vector2 goal,
        out float nominalSpeed)
    {
        owner = DB_BehaviorOwner.None;
        goal = default;
        nominalSpeed = 0f;
        if (bat?.room == null || !states.TryGetValue(bat, out State state)) return false;
        int clock = bat.room.game?.clock ?? int.MinValue;
        if (state.Clock != clock) return false;
        owner = state.Owner;
        goal = state.Goal;
        nominalSpeed = state.NominalSpeed;
        return true;
    }
}
'''
MOTOR.write_text(motor, encoding='utf-8')

fog = r'''using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R4 goal-space navigation uncertainty for authorized DryCycle Fog/DenseFog.
/// It never owns locomotion and never writes velocity/localGoal by itself.
/// </summary>
internal static class DB_FogGoalModifier
{
    private sealed class State
    {
        internal Vector2 Offset;
        internal int OffsetUntil;
        internal int WeatherOrdinal = -1;
    }

    private static ConditionalWeakTable<DesertBatfly, State> states = new();

    internal static void Reset() => states = new ConditionalWeakTable<DesertBatfly, State>();

    internal static void Forget(DesertBatfly bat)
    {
        if (bat != null) states.Remove(bat);
    }

    internal static Vector2 ModifyGoal(DesertBatfly bat, DB_BehaviorOwner owner, Vector2 goal)
    {
        if (bat?.room == null || owner is not (
                DB_BehaviorOwner.EnvironmentHardSurvival or DB_BehaviorOwner.EnvironmentLocalSurvival) ||
            !DesertBatflyEnvironmentalBehavior.TryGetInfluence(bat, out DesertBatflyEnvironmentalInfluence influence) ||
            influence.NavigationUncertainty <= 0.05f ||
            influence.Weather is not (
                DesertBatflyEnvironmentalWeather.Fog or DesertBatflyEnvironmentalWeather.DenseFog))
            return goal;

        State state = states.GetOrCreateValue(bat);
        int tick = bat.room.game?.clock ?? 0;
        int weatherOrdinal = (int)influence.Weather;
        if (tick >= state.OffsetUntil || state.WeatherOrdinal != weatherOrdinal)
        {
            float angle = Stable01(bat.Personality.VisualSeed ^ tick / 120) * Mathf.PI * 2f;
            float radius = Mathf.Lerp(8f, 70f, influence.NavigationUncertainty);
            state.Offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            state.OffsetUntil = tick + 90 + StableBucket(bat.Personality.VisualSeed, 70);
            state.WeatherOrdinal = weatherOrdinal;
        }

        float distance = Vector2.Distance(bat.mainBodyChunk.pos, goal);
        float fade = Mathf.InverseLerp(70f, 320f, distance);
        return goal + state.Offset * fade;
    }

    private static int StableBucket(int seed, int count)
    {
        if (count <= 1) return 0;
        return Mathf.Clamp(Mathf.FloorToInt(Stable01(seed) * count), 0, count - 1);
    }

    private static float Stable01(int seed)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return (x & 0x00ffffffu) / 16777215f;
        }
    }
}
'''
FOG.write_text(fog, encoding='utf-8')

ai = AI.read_text(encoding='utf-8')
old_recovery = '''        Vector2 next = fly.AI.ProgressLocalGoalAlongDijkstraMap(fly.mainBodyChunk.pos, bestMap);\n        fly.AI.localGoal = next;\n        fly.Injury.NominalFlightSpeed = 4.2f;\n        Vector2 desired = Custom.DirVec(fly.mainBodyChunk.pos, next) * 4.2f;\n        fly.mainBodyChunk.vel = Vector2.Lerp(fly.mainBodyChunk.vel, desired, 0.20f);\n        return true;'''
new_recovery = '''        Vector2 dijkstraInput = fly.AI.localGoal;\n        if (dijkstraInput == Vector2.zero || fly.room.GetTile(dijkstraInput).Solid)\n            dijkstraInput = fly.mainBodyChunk.pos;\n        Vector2 next = fly.AI.ProgressLocalGoalAlongDijkstraMap(dijkstraInput, bestMap);\n        return DB_FlightMotor.TrySteer(\n            fly,\n            DB_BehaviorOwner.InjuryRecovery,\n            next,\n            4.2f,\n            preserveDijkstra: true,\n            response: 0.20f);'''
ai = replace_once(ai, old_recovery, new_recovery, 'Injury recovery Dijkstra semantics')
steer_start = ai.index('    private bool SteerOwned(Vector2 goal, float speed, DB_BehaviorOwner owner)\n    {')
steer_end = ai.index('    private void TryPlanRoost()', steer_start)
new_steer = '''    private bool SteerOwned(Vector2 goal, float speed, DB_BehaviorOwner owner)\n    {\n        if (!DB_FlightMotor.TrySteer(fly, owner, goal, speed)) return false;\n        hasRoost = false;\n        return true;\n    }\n\n'''
ai = ai[:steer_start] + new_steer + ai[steer_end:]
AI.write_text(ai, encoding='utf-8')

injury = INJURY.read_text(encoding='utf-8')
injury = replace_once(injury, '    internal float NominalFlightSpeed;\n', '', 'remove Injury nominal speed ownership')
injury = replace_count(injury, '        NominalFlightSpeed = 0f;\n', '', 2, 'remove Injury nominal resets')
apply_start = injury.index('    internal void ApplyFlight(Vector2 previous)\n    {')
# ApplyFlight is the final method in this class. Remove it while preserving the class brace.
apply_end = injury.rfind('\n}')
if apply_end <= apply_start:
    raise SystemExit('cannot locate Injury.ApplyFlight final method')
injury = injury[:apply_start] + injury[apply_end:]
INJURY.write_text(injury, encoding='utf-8')

creature = CREATURE.read_text(encoding='utf-8')
creature = replace_once(
    creature,
    '        Injury.ApplyFlight(previousFlightVelocity);',
    '        DB_FlightMotor.ApplyPostPhysics(this, previousFlightVelocity);',
    'Creature final injury-flight pass')
CREATURE.write_text(creature, encoding='utf-8')

env = ENV.read_text(encoding='utf-8')
env = replace_once(env, '        internal Vector2 FogGoalOffset;\n        internal int FogGoalOffsetUntil;\n', '', 'remove Environment fog goal state')
env = replace_once(
    env,
    '        ApplyLocalBehavior(bat, state, bat.room.game?.clock ?? 0);',
    '        ApplyLocalBehavior(bat, state);',
    'Environment apply call')
env = replace_once(
    env,
    '    private static void ApplyLocalBehavior(DesertBatfly bat, State state, int tick)\n    {',
    '    private static void ApplyLocalBehavior(DesertBatfly bat, State state)\n    {',
    'Environment apply signature')
old_fog_block = '''        Vector2 goal = shelterPoint;\n        if (influence.NavigationUncertainty > 0.05f &&\n            influence.Weather is DesertBatflyEnvironmentalWeather.Fog or DesertBatflyEnvironmentalWeather.DenseFog)\n        {\n            if (tick >= state.FogGoalOffsetUntil || state.LastWeatherOrdinal != (int)influence.Weather)\n            {\n                float angle = Stable01(bat.Personality.VisualSeed ^ tick / 120) * Mathf.PI * 2f;\n                float radius = Mathf.Lerp(8f, 70f, influence.NavigationUncertainty);\n                state.FogGoalOffset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;\n                state.FogGoalOffsetUntil = tick + 90 + StableBucket(bat.Personality.VisualSeed, 70);\n            }\n            float distance = Vector2.Distance(bat.mainBodyChunk.pos, shelterPoint);\n            float fade = Mathf.InverseLerp(70f, 320f, distance);\n            goal += state.FogGoalOffset * fade;\n        }\n\n        if (!bat.room.terrain.Contains(goal))\n            bat.AI.localGoal = goal;\n        else\n            bat.AI.localGoal = shelterPoint;'''
new_fog_block = '''        DB_BehaviorOwner owner = influence.HardSurvival\n            ? DB_BehaviorOwner.EnvironmentHardSurvival\n            : DB_BehaviorOwner.EnvironmentLocalSurvival;\n        DB_FlightMotor.TrySteer(\n            bat,\n            owner,\n            shelterPoint,\n            Mathf.Lerp(4f, 7f, influence.ShelterDrive));'''
env = replace_once(env, old_fog_block, new_fog_block, 'move Fog goal modification into FlightMotor')
ENV.write_text(env, encoding='utf-8')

hooks = HOOKS.read_text(encoding='utf-8')
hooks = replace_count(
    hooks,
    '        DB_BehaviorArbiter.Reset();\n',
    '        DB_BehaviorArbiter.Reset();\n        DB_FlightMotor.Reset();\n',
    2,
    'FlightMotor lifecycle reset')
hooks = replace_once(
    hooks,
    '            DB_BehaviorArbiter.Forget(desert);\n',
    '            DB_BehaviorArbiter.Forget(desert);\n            DB_FlightMotor.Forget(desert);\n',
    'FlightMotor room-change forget')
HOOKS.write_text(hooks, encoding='utf-8')

test = r'''using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunTask14R4()
    {
        Type motor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FlightMotor", true);
        Type fog = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FogGoalModifier", true);
        Type owner = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorOwner", true);
        Type arbiter = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorArbiter", true);
        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);
        Type injury = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyInjury", true);
        Type creature = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatfly", true);
        Type environment = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalBehavior", true);
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);

        Check(motor.GetMethod("Reset", Flags) != null &&
              motor.GetMethod("Forget", Flags) != null &&
              motor.GetMethod("TrySteer", Flags) != null &&
              motor.GetMethod("ApplyPostPhysics", Flags) != null &&
              motor.GetMethod("TryGetIntent", Flags) != null,
            "Task14 R4 FlightMotor exposes explicit lifecycle, steer and final injury-pass surfaces");
        Check(fog.GetMethod("ModifyGoal", Flags) != null &&
              fog.GetMethod("Reset", Flags) != null && fog.GetMethod("Forget", Flags) != null,
            "Task14 R4 Fog uncertainty is an explicit goal modifier with lifecycle ownership");
        Check(MethodCallOffset(motor.GetMethod("TrySteer", Flags), arbiter, "IsPrimaryOwner") >= 0 &&
              MethodCallOffset(motor.GetMethod("TrySteer", Flags), fog, "ModifyGoal") >= 0,
            "Task14 R4 FlightMotor requires same-tick owner and applies Fog before steering");
        Check(injury.GetField("NominalFlightSpeed", Flags) == null &&
              injury.GetMethod("ApplyFlight", Flags) == null &&
              injury.GetMethod("ModifyFlight", Flags) != null,
            "Task14 R4 Injury is a pure flight modifier rather than nominal-speed/final-controller owner");
        Check(MethodCallOffset(creature.GetMethod("Update", Flags), motor, "ApplyPostPhysics") >= 0,
            "Task14 R4 creature final flight modifier passes through DB_FlightMotor");
        Check(MethodCallOffset(ai.GetMethod("SteerOwned", Flags), motor, "TrySteer") >= 0 &&
              MethodCallOffset(ai.GetMethod("TryDriveRecoveryHive", Flags), motor, "TrySteer") >= 0,
            "Task14 R4 core AI and InjuryRecovery submit ordinary steering through FlightMotor");
        Check(MethodCallOffset(environment.GetMethod("ApplyLocalBehavior", Flags), motor, "TrySteer") >= 0,
            "Task14 R4 Environment submits shelter goal to FlightMotor instead of writing localGoal directly");
        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), motor, "Reset") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), motor, "Reset") >= 0 &&
              MethodCallOffset(hooks.GetMethod("FlyNewRoom", Flags), motor, "Forget") >= 0,
            "Task14 R4 FlightMotor/Fog transient state follows species lifecycle");

        Console.WriteLine("Task14 R4 batch1: FlightMotor foundation, Injury Dijkstra semantics and Fog goal modifier are code-migrated; remaining domain steering and Combat responsibility split stay open.");
    }
}
'''
TEST.write_text(test, encoding='utf-8')

dispatch = DISPATCH.read_text(encoding='utf-8')
dispatch = replace_once(dispatch, '        RunTask14R3();\n', '        RunTask14R3();\n        RunTask14R4();\n', 'R4 test dispatcher')
DISPATCH.write_text(dispatch, encoding='utf-8')

status = r'''Desert Batfly Task 14 — R4 FlightMotor / AI Responsibility Split Status
Revision: R4-B1 / 2026-09-07
Branch: task14-r4-flightmotor
Base: R3 code-side closed at a2260b110af22f84317c90d8dcd7ec6ec128a67c
Status: 【R4 已开始 / Batch 1 代码迁移】

======================================================================
1. 本批范围
======================================================================

R4-B1 只处理：

1. 建立 DB_FlightMotor；
2. 将 DesertBatflyAI.SteerOwned 收敛到 FlightMotor；
3. 修复 InjuryRecovery Hive Dijkstra input semantics；
4. 将 Injury final flight pass 从 Injury controller 迁到 FlightMotor；
5. 将 Fog/DenseFog 的稳定短期 goal error 从 Environment direct localGoal 写入迁为 motor 前 goal modifier；
6. 建立 lifecycle 与 architecture regression guard。

本批不提前做：

- R5 internal bridge debt removal；
- R6 全量 rename / move；
- Combat 全状态机迁出 DesertBatflyAI；
- Travel/Social/Vengeance 所有 direct steering 的最终迁移；
- Rain World live validation。

======================================================================
2. DB_FlightMotor
======================================================================

职责：

- same-tick PrimaryOwner guard；
- 接收 owner / goal / nominalSpeed；
- Fog goal modifier；
- ordinary BatFlight intent preparation；
- localGoal intent；
- 单次 velocity request；
- final Injury flight modifier pass；
- transient per-frame intent debug state。

明确不负责：

- world route planning；
- Behavior arbitration；
- terrain bypass；
- teleport；
- fixed Y；
- special Attach/Interfere/Grasp/Shortcut/Emergence physics。

======================================================================
3. Injury Dijkstra fix
======================================================================

旧实现：

ProgressLocalGoalAlongDijkstraMap(mainBodyChunk.pos, map)

R4-B1：

- 使用 FlyAI.localGoal 作为 progression input；
- localGoal 无效时仅做一次 current-position seed；
- 后续推进遵守 native localGoal -> next localGoal 语义；
- preserveDijkstra=true，FlightMotor 不清除 recovery map。

目标是消除 body-position 每帧重置造成的 short-step jitter / 狭窄地形反复切换风险。

======================================================================
4. Injury flight responsibility
======================================================================

DesertBatflyInjury 继续拥有：

- ForwardControl；
- TurnControl；
- LiftControl；
- AccelerationControl；
- WingBias；
- ModifyFlight(previous, requested, nominalSpeed)。

不再拥有：

- NominalFlightSpeed runtime intent；
- ApplyFlight final controller。

最终 flight pass 改由 DB_FlightMotor.ApplyPostPhysics 调用纯 Injury modifier。

======================================================================
5. Fog goal modifier
======================================================================

旧 Environment ApplyLocalBehavior：

- 维护 FogGoalOffset；
- 直接改 goal；
- 直接写 FlyAI.localGoal。

R4-B1：

- DB_FogGoalModifier 独立维护稳定 offset；
- Fog/DenseFog authority 仍来自 Task13 已批准的 DryCycle EnvironmentInfluence；
- 保持约 90+stable bucket 的短期稳定误差；
- 距离接近时继续 fade；
- modifier 自身不写 localGoal / velocity；
- DB_FlightMotor 在提交 environment goal 前调用 modifier。

当前为 behavior-preserving migration，不扩大 Fog 对其他 owner 的影响范围。

======================================================================
6. 当前仍 OPEN
======================================================================

R4-OPEN-01 — Travel / Social / Vengeance / other normal steering migrate to FlightMotor.
R4-OPEN-02 — Combat state/motivation/contact responsibility split out of DesertBatflyAI.
R4-OPEN-03 — audit every direct localGoal / ordinary velocity writer and classify special physics exceptions.
R4-OPEN-04 — Observatory expose FlightMotor intent + nominal speed + modifier result.
R4-OPEN-05 — managed compile/integration and Rain World live scenarios deferred to final validation stage per project decision.

======================================================================
7. R4 完成条件
======================================================================

R4 只有在：

- 普通飞行只通过 DB_FlightMotor；
- Injury 只做 modifier；
- Fog 只做 goal modifier；
- Combat responsibility 已从旧 AI 拆出；
- special physics owner 与 ordinary motor 边界完整；
- source/managed regressions 通过；

后才能标记代码侧完成。
'''
STATUS.write_text(status, encoding='utf-8')

# Remove one-shot artifacts from the resulting commit.
SCRIPT.unlink()
WORKFLOW.unlink()
print('R4 FlightMotor batch1 migration prepared successfully')
