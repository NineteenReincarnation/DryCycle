from pathlib import Path
import re

ROOT = Path('.')


def read(path):
    return (ROOT / path).read_text(encoding='utf-8')


def write(path, text):
    (ROOT / path).write_text(text, encoding='utf-8')


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
            if c == '/' and n == '/':
                state = 'line_comment'; i += 2; continue
            if c == '/' and n == '*':
                state = 'block_comment'; i += 2; continue
            if c == '"':
                state = 'string'; i += 1; continue
            if c == "'":
                state = 'char'; i += 1; continue
            if c == '{':
                depth += 1
            elif c == '}':
                depth -= 1
                if depth == 0:
                    return line_start, i + 1
            i += 1
            continue
        if state == 'line_comment':
            if c == '\n': state = 'code'
            i += 1; continue
        if state == 'block_comment':
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
    text = text[:start] + replacement.rstrip() + text[end:]
    write(path, text)


def transform_method(path, anchor, transform):
    text = read(path)
    start, end = method_span(text, anchor)
    segment = text[start:end]
    transformed = transform(segment)
    if transformed == segment:
        raise SystemExit(f'{anchor}: transform made no changes')
    write(path, text[:start] + transformed + text[end:])


# -----------------------------------------------------------------------------
# 1. FlightMotor: add native-goal guidance + same-owner tactical retarget.
# -----------------------------------------------------------------------------
path = 'src/Creatures/DesertBatfly/Runtime/DB_FlightMotor.cs'
text = read(path)
marker = '''    /// <summary>\n    /// Final R4 injury-flight pass. This does not choose a goal; it only modifies the velocity\n'''
insert = '''    /// <summary>\n    /// Owner-validated goal submission that deliberately leaves velocity production to Rain World\n    /// native Fly physics. This preserves legacy Social/Travel/projectile behavior while making\n    /// DB_FlightMotor the single Desert Batfly localGoal write boundary.\n    /// </summary>\n    internal static bool TryGuideNative(\n        DesertBatfly bat,\n        DB_BehaviorOwner owner,\n        Vector2 goal,\n        float nominalSpeed = 0f)\n    {\n        if (bat?.room == null || bat.AI == null || bat.mainBodyChunk == null ||\n            bat.dead || !bat.Consious || bat.inShortcut || bat.Emergence?.Active == true ||\n            bat.grabbedBy.Count > 0 || !DB_BehaviorArbiter.IsPrimaryOwner(bat, owner))\n            return false;\n\n        if (DB_BehaviorArbiter.TryGetResolution(bat, out DB_BehaviorResolution resolution) &&\n            resolution.SpecialPhysicsOwner != DB_SpecialPhysicsOwner.None)\n            return false;\n\n        goal = DB_FogGoalModifier.ModifyGoal(bat, owner, goal);\n        bat.AI.localGoal = goal;\n\n        State state = states.GetOrCreateValue(bat);\n        state.Clock = bat.room.game?.clock ?? int.MinValue;\n        state.Owner = owner;\n        state.Goal = goal;\n        state.NominalSpeed = Mathf.Max(0f, nominalSpeed);\n        return true;\n    }\n\n    /// <summary>\n    /// Same-owner tactical goal adjustment after an owner has already submitted its base intent.\n    /// It never changes ownership or injects a second velocity controller.\n    /// </summary>\n    internal static bool TryRetarget(DesertBatfly bat, DB_BehaviorOwner owner, Vector2 goal)\n    {\n        if (bat?.room == null || bat.AI == null ||\n            !DB_BehaviorArbiter.IsPrimaryOwner(bat, owner))\n            return false;\n        int clock = bat.room.game?.clock ?? int.MinValue;\n        if (!states.TryGetValue(bat, out State state) || state.Clock != clock || state.Owner != owner)\n            return false;\n        if (DB_BehaviorArbiter.TryGetResolution(bat, out DB_BehaviorResolution resolution) &&\n            resolution.SpecialPhysicsOwner != DB_SpecialPhysicsOwner.None)\n            return false;\n\n        goal = DB_FogGoalModifier.ModifyGoal(bat, owner, goal);\n        bat.AI.localGoal = goal;\n        state.Goal = goal;\n        return true;\n    }\n\n'''
text = replace_once(text, marker, insert + marker, 'insert FlightMotor guide/retarget')
write(path, text)


# -----------------------------------------------------------------------------
# 2. Social: keep native-flight semantics, move the actual localGoal write to motor.
# -----------------------------------------------------------------------------
social_method = '''    private static bool SocialSteer(DesertBatfly bat, Vector2 goal, float speed, int preferredSide)\n    {\n        if (bat?.room == null || bat.AI == null || bat.mainBodyChunk == null ||\n            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Social))\n            return false;\n        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, goal);\n        if (direction == Vector2.zero) return true;\n\n        Vector2 probe = bat.mainBodyChunk.pos + direction * 25f;\n        if (Obstructed(bat.room, probe))\n        {\n            Vector2 leftProbe = bat.mainBodyChunk.pos + Vector2.left * 28f;\n            Vector2 rightProbe = bat.mainBodyChunk.pos + Vector2.right * 28f;\n            bool leftBlocked = Obstructed(bat.room, leftProbe);\n            bool rightBlocked = Obstructed(bat.room, rightProbe);\n            if (leftBlocked && rightBlocked) return false;\n\n            float desiredSide = preferredSide == 0\n                ? Mathf.Sign(direction.x == 0f ? 1f : direction.x)\n                : Mathf.Sign(preferredSide);\n            if (desiredSide < 0f && leftBlocked) desiredSide = 1f;\n            if (desiredSide > 0f && rightBlocked) desiredSide = -1f;\n            goal = bat.mainBodyChunk.pos + Vector2.right * desiredSide * 58f + Vector2.up * 6f;\n            speed = Mathf.Min(speed, 4.8f);\n        }\n\n        bat.burrowOrHangSpot = null;\n        if (bat.AI.behavior != FlyAI.Behavior.Idle)\n            bat.AI.ChangeBehavior(FlyAI.Behavior.Idle);\n        bat.AI.followingDijkstraMap = -1;\n        bat.movMode = Fly.MovementMode.BatFlight;\n        return DB_FlightMotor.TryGuideNative(bat, DB_BehaviorOwner.Social, goal, speed);\n    }'''
replace_method(
    'src/Creatures/DesertBatfly/Social/DesertBatflySocialLife.cs',
    '    private static bool SocialSteer(',
    social_method)


# -----------------------------------------------------------------------------
# 3. Vengeance: preserve active velocity request, but route it through FlightMotor.
# -----------------------------------------------------------------------------
vengeance_method = '''    private static void ForceFlight(\n        DesertBatfly bat,\n        Vector2 goal,\n        float speed)\n    {\n        if (bat?.room == null ||\n            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Vengeance))\n            return;\n\n        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, goal);\n        Vector2 probe = bat.mainBodyChunk.pos + direction * 25f;\n        if (bat.room.GetTile(probe).Solid ||\n            (bat.room.terrain != null && bat.room.terrain.Contains(probe)))\n        {\n            goal = bat.mainBodyChunk.pos + Vector2.up * 75f;\n            speed = Mathf.Min(speed, 7f);\n        }\n\n        DB_FlightMotor.TrySteer(\n            bat,\n            DB_BehaviorOwner.Vengeance,\n            goal,\n            speed,\n            response: 0.28f);\n    }'''
replace_method(
    'src/Creatures/DesertBatfly/DesertBatflyIntimidation.cs',
    '    private static void ForceFlight(',
    vengeance_method)


# -----------------------------------------------------------------------------
# 4. Projectile evade: legacy code only set goal + nominal injury speed; preserve that.
# -----------------------------------------------------------------------------
projectile_method = '''    internal static bool ApplyProjectileEvadeOwned(DesertBatfly bat, Vector2 evade)\n    {\n        if (bat?.room == null || bat.AI == null || bat.dead || !bat.Consious || bat.inShortcut ||\n            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.ImmediateProjectileEvade))\n            return false;\n\n        bat.LoseAllGrasps();\n        bat.burrowOrHangSpot = null;\n        if (bat.AI.behavior != FlyAI.Behavior.Idle)\n            bat.AI.ChangeBehavior(FlyAI.Behavior.Idle);\n        bat.AI.followingDijkstraMap = -1;\n        bat.movMode = Fly.MovementMode.BatFlight;\n        if (!DB_FlightMotor.TryGuideNative(\n                bat, DB_BehaviorOwner.ImmediateProjectileEvade, evade, 9f))\n            return false;\n        DesertBatflySocialLife.CancelForPriority(bat, "R3 PrimaryOwner=ImmediateProjectileEvade");\n        TraceAdjustment(\n            bat,\n            "ThreatEvadeStarted",\n            evade,\n            "real projectile trajectory owns this motor goal; Rain World native Fly locomotion executes the dodge");\n        return true;\n    }'''
replace_method(
    'src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatTactics.cs',
    '    internal static bool ApplyProjectileEvadeOwned(',
    projectile_method)


# -----------------------------------------------------------------------------
# 5. Threat tactical geometry: convert every Combat localGoal write into same-owner retarget.
# -----------------------------------------------------------------------------
def threat_transform(segment):
    before_writes = len(re.findall(r'bat\\.AI\\.localGoal\\s*(?:\\+=|=)', segment))
    if before_writes < 6:
        raise SystemExit(f'Threat tactical: expected >=6 localGoal writes, found {before_writes}')
    # += first so the plain assignment transform cannot consume it.
    segment = re.sub(
        r'bat\\.AI\\.localGoal\\s*\\+=\\s*(.*?);',
        r'DB_FlightMotor.TryRetarget(bat, DB_BehaviorOwner.Combat, bat.AI.localGoal + (\\1));',
        segment,
        flags=re.S)
    segment = re.sub(
        r'bat\\.AI\\.localGoal\\s*=\\s*(.*?);',
        r'DB_FlightMotor.TryRetarget(bat, DB_BehaviorOwner.Combat, \\1);',
        segment,
        flags=re.S)
    after = re.findall(r'bat\\.AI\\.localGoal\\s*(?:\\+=|=)', segment)
    if after:
        raise SystemExit('Threat tactical: direct localGoal writes remain after transform')
    if segment.count('DB_FlightMotor.TryRetarget') < before_writes:
        raise SystemExit('Threat tactical: not all goal writes became motor retargets')
    return segment

transform_method(
    'src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatRuntime.cs',
    '    private static void ApplyTacticalAdjustment(',
    threat_transform)


# -----------------------------------------------------------------------------
# 6. Travel refuge holding: native Dijkstra/geometry goal writes through FlightMotor only.
# -----------------------------------------------------------------------------
travel_method = '''    private static bool HoldAtRefuge(DesertBatfly bat, TravelIntent intent)\n    {\n        if (bat?.room == null || intent == null) return false;\n        intent.Suspended = false;\n        bat.AI.afraid = Mathf.Max(bat.AI.afraid, 1.25f);\n        if (intent.RefugeGoalRefresh > 0) intent.RefugeGoalRefresh--;\n        if (intent.RefugeGoalRefresh > 0) return true;\n        intent.RefugeGoalRefresh = RefugeGoalRefreshTicks;\n\n        // Prefer an authored/native abstract node. Route planning remains Task09-era native\n        // Dijkstra semantics; R4 only centralizes the realized localGoal write.\n        if (intent.RefugeNode >= 0 && bat.room.abstractRoom?.nodes != null &&\n            intent.RefugeNode < bat.room.abstractRoom.nodes.Length)\n        {\n            AbstractRoomNode.Type nodeType = bat.room.abstractRoom.nodes[intent.RefugeNode].type;\n            if (nodeType == AbstractRoomNode.Type.Den || nodeType == AbstractRoomNode.Type.BatHive)\n            {\n                int mapped = bat.room.abstractRoom.CommonToCreatureSpecificNodeIndex(\n                    intent.RefugeNode, bat.Template);\n                if (mapped >= 0)\n                {\n                    bat.AI.followingDijkstraMap = mapped;\n                    Vector2 nextGoal = bat.AI.ProgressLocalGoalAlongDijkstraMap(bat.AI.localGoal, mapped);\n                    DB_FlightMotor.TryGuideNative(bat, DB_BehaviorOwner.Travel, nextGoal);\n                    intent.StatusReason = "holding refuge via native den/hive Dijkstra";\n                    return true;\n                }\n            }\n        }\n\n        if (DesertBatflyRefuge.TryGetKnownShelterPoint(bat.room, out Vector2 shelterPoint))\n        {\n            if (Vector2.Distance(bat.mainBodyChunk.pos, shelterPoint) > 55f &&\n                Vector2.Distance(bat.AI.localGoal, shelterPoint) > 45f)\n                DB_FlightMotor.TryGuideNative(bat, DB_BehaviorOwner.Travel, shelterPoint);\n            intent.StatusReason = "holding geometry refuge near covered point";\n        }\n        else\n        {\n            intent.StatusReason = "holding refuge; no local shelter point override";\n        }\n        return true;\n    }'''
replace_method(
    'src/Creatures/DesertBatfly/DesertBatflyTravelNavigation.cs',
    '    private static bool HoldAtRefuge(',
    travel_method)


# -----------------------------------------------------------------------------
# 7. Environment shelter goal: B1 used active steering; restore legacy goal-only semantics.
# -----------------------------------------------------------------------------
def env_behavior_transform(segment):
    if segment.count('DB_FlightMotor.TrySteer(') != 1:
        raise SystemExit('Environment ApplyLocalBehavior: expected one TrySteer')
    return segment.replace('DB_FlightMotor.TrySteer(', 'DB_FlightMotor.TryGuideNative(', 1)

transform_method(
    'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalBehavior.cs',
    '    private static void ApplyLocalBehavior(',
    env_behavior_transform)


# -----------------------------------------------------------------------------
# 8. Survival bridge: stop save/restore localGoal hacks; current Arbiter owner is authority.
# -----------------------------------------------------------------------------
survival_update = '''    private static void UpdateHook(EnvironmentalUpdateOrig orig, DesertBatfly bat)\n    {\n        orig(bat);\n        ApplySecondaryLightRainMoisture(bat);\n        ApplyNativeHomeAndBurrow(bat);\n    }'''
replace_method(
    'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalSurvivalBridge.cs',
    '    private static void UpdateHook(',
    survival_update)

survival_home = '''    private static void ApplyNativeHomeAndBurrow(DesertBatfly bat)\n    {\n        if (bat?.room?.aimap == null || bat.AI == null || bat.dead || !bat.Consious ||\n            bat.inShortcut || bat.Emergence?.Active == true)\n            return;\n        if (!DB_BehaviorArbiter.TryGetResolution(bat, out DB_BehaviorResolution ownership) ||\n            ownership.PrimaryOwner is not (\n                DB_BehaviorOwner.EnvironmentHardSurvival or DB_BehaviorOwner.EnvironmentLocalSurvival))\n            return;\n        DB_BehaviorOwner owner = ownership.PrimaryOwner;\n        if (!DesertBatflyEnvironmentalBehavior.TryGetInfluence(bat, out DesertBatflyEnvironmentalInfluence influence))\n            return;\n        if (!ShouldSeekHome(influence) && !ShouldBurrow(influence)) return;\n        if (bat.room.hives == null || bat.room.hives.Length == 0) return;\n\n        int bestMap = -1;\n        int bestHive = -1;\n        int bestDistance = int.MaxValue;\n        IntVector2 current = bat.room.GetTilePosition(bat.mainBodyChunk.pos);\n        for (int i = 0; i < bat.room.hives.Length; i++)\n        {\n            IntVector2[] hive = bat.room.hives[i];\n            if (hive == null || hive.Length == 0) continue;\n            int map = bat.room.exitAndDenIndex.Length + i;\n            int distance = bat.room.aimap.ExitDistanceForCreature(current, map, bat.Template);\n            if (distance < 0 || distance >= bestDistance) continue;\n            bestDistance = distance;\n            bestHive = i;\n            bestMap = map;\n        }\n        if (bestHive < 0 || bestMap < 0) return;\n\n        bool onHiveTile = bat.room.GetTile(bat.mainBodyChunk.pos).hive;\n        if (onHiveTile && ShouldBurrow(influence))\n        {\n            DesertBatflySocialLife.CancelForPriority(bat, "Task13 environmental Burrow priority");\n            bat.DesertAI.CancelAttack();\n            bat.AI.ChangeBehavior(FlyAI.Behavior.Burrow);\n            bat.burrowOrHangSpot = bat.mainBodyChunk.pos;\n            bat.movMode = Fly.MovementMode.Burrow;\n            bat.AI.afraid = Mathf.Max(bat.AI.afraid, influence.HardSurvival ? 1.25f : 0.62f);\n            return;\n        }\n\n        if (!ShouldSeekHome(influence) && influence.BurrowDrive < 0.45f) return;\n        if (bat.DesertAI.FormalAttack && !influence.HardSurvival) return;\n\n        DesertBatflySocialLife.CancelForPriority(bat, "Task13 same-room Home/Hive retreat");\n        if (influence.HardSurvival) bat.DesertAI.CancelAttack();\n        bat.AI.leaveRoomDijkstra = -1;\n        bat.AI.followingDijkstraMap = bestMap;\n        Vector2 nextGoal = bat.AI.ProgressLocalGoalAlongDijkstraMap(bat.AI.localGoal, bestMap);\n        DB_FlightMotor.TryGuideNative(bat, owner, nextGoal);\n        bat.AI.afraid = Mathf.Max(bat.AI.afraid, influence.HardSurvival ? 1.10f : 0.35f);\n    }'''
replace_method(
    'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalSurvivalBridge.cs',
    '    private static void ApplyNativeHomeAndBurrow(',
    survival_home)


# -----------------------------------------------------------------------------
# 9. Explicit native special-physics states: Burrow and Chain bypass ordinary motor.
# -----------------------------------------------------------------------------
path = 'src/Creatures/DesertBatfly/Runtime/DB_BehaviorProposal.cs'
text = read(path)
text = replace_once(
    text,
    '    CombatAttach,\n    CombatInterfere\n}',
    '    CombatAttach,\n    CombatInterfere,\n    NativeBurrow,\n    NativeChain\n}',
    'special physics enum')
write(path, text)

frame_special = '''    private static DB_SpecialPhysicsOwner ResolveSpecialPhysicsOwner(DesertBatfly bat, bool restrained)\n    {\n        if (bat == null || bat.dead || !bat.Consious) return DB_SpecialPhysicsOwner.CreaturePhysics;\n        if (restrained) return DB_SpecialPhysicsOwner.Grasp;\n        if (bat.inShortcut) return DB_SpecialPhysicsOwner.Shortcut;\n        if (bat.Emergence?.Active == true) return DB_SpecialPhysicsOwner.Emergence;\n        if (bat.DesertAI?.Mode == DesertBatflyAI.Activity.Attach) return DB_SpecialPhysicsOwner.CombatAttach;\n        if (bat.DesertAI?.Mode == DesertBatflyAI.Activity.Interfere) return DB_SpecialPhysicsOwner.CombatInterfere;\n        if (bat.AI?.behavior == FlyAI.Behavior.Burrow) return DB_SpecialPhysicsOwner.NativeBurrow;\n        if (bat.AI?.behavior == FlyAI.Behavior.Chain) return DB_SpecialPhysicsOwner.NativeChain;\n        return DB_SpecialPhysicsOwner.None;\n    }'''
replace_method(
    'src/Creatures/DesertBatfly/Runtime/DB_FrameContext.cs',
    '    private static DB_SpecialPhysicsOwner ResolveSpecialPhysicsOwner(',
    frame_special)


# -----------------------------------------------------------------------------
# 10. Expand R4 contract tests to B2 migration surface.
# -----------------------------------------------------------------------------
test_path = ROOT / 'tests/DesertBatfly/Program.Task14R4.cs'
test_path.write_text(r'''using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunTask14R4()
    {
        Type motor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FlightMotor", true);
        Type fog = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FogGoalModifier", true);
        Type owner = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorOwner", true);
        Type special = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SpecialPhysicsOwner", true);
        Type arbiter = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorArbiter", true);
        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);
        Type injury = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyInjury", true);
        Type creature = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatfly", true);
        Type environment = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalBehavior", true);
        Type survival = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSurvivalBridge", true);
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialLife", true);
        Type vengeance = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyIntimidation", true);
        Type tactics = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatTactics", true);
        Type threat = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatRuntime", true);
        Type travel = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyTravelNavigation", true);
        Type frame = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FrameContextRuntime", true);
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);

        Check(motor.GetMethod("Reset", Flags) != null &&
              motor.GetMethod("Forget", Flags) != null &&
              motor.GetMethod("TrySteer", Flags) != null &&
              motor.GetMethod("TryGuideNative", Flags) != null &&
              motor.GetMethod("TryRetarget", Flags) != null &&
              motor.GetMethod("ApplyPostPhysics", Flags) != null &&
              motor.GetMethod("TryGetIntent", Flags) != null,
            "Task14 R4 FlightMotor exposes active steer, native guide, same-owner retarget and final modifier surfaces");
        Check(fog.GetMethod("ModifyGoal", Flags) != null &&
              fog.GetMethod("Reset", Flags) != null && fog.GetMethod("Forget", Flags) != null,
            "Task14 R4 Fog uncertainty is an explicit goal modifier with lifecycle ownership");
        Check(MethodCallOffset(motor.GetMethod("TrySteer", Flags), arbiter, "IsPrimaryOwner") >= 0 &&
              MethodCallOffset(motor.GetMethod("TryGuideNative", Flags), arbiter, "IsPrimaryOwner") >= 0 &&
              MethodCallOffset(motor.GetMethod("TryRetarget", Flags), arbiter, "IsPrimaryOwner") >= 0,
            "all R4 FlightMotor write surfaces require the same-tick PrimaryOwner");
        Check(injury.GetField("NominalFlightSpeed", Flags) == null &&
              injury.GetMethod("ApplyFlight", Flags) == null &&
              injury.GetMethod("ModifyFlight", Flags) != null,
            "Task14 R4 Injury stays a pure flight modifier");
        Check(MethodCallOffset(creature.GetMethod("Update", Flags), motor, "ApplyPostPhysics") >= 0,
            "Task14 R4 creature final injury pass goes through DB_FlightMotor");
        Check(MethodCallOffset(ai.GetMethod("SteerOwned", Flags), motor, "TrySteer") >= 0 &&
              MethodCallOffset(ai.GetMethod("TryDriveRecoveryHive", Flags), motor, "TrySteer") >= 0,
            "core species steering and InjuryRecovery use FlightMotor");
        Check(MethodCallOffset(environment.GetMethod("ApplyLocalBehavior", Flags), motor, "TryGuideNative") >= 0,
            "Environment shelter navigation preserves native flight while centralizing its goal write");
        Check(MethodCallOffset(social.GetMethod("SocialSteer", Flags), motor, "TryGuideNative") >= 0,
            "Social goal submission goes through FlightMotor native guidance");
        Check(MethodCallOffset(vengeance.GetMethod("ForceFlight", Flags), motor, "TrySteer") >= 0,
            "Vengeance active velocity request goes through FlightMotor");
        Check(MethodCallOffset(tactics.GetMethod("ApplyProjectileEvadeOwned", Flags), motor, "TryGuideNative") >= 0,
            "Projectile evade goal/nominal speed goes through FlightMotor");
        Check(MethodCallOffset(threat.GetMethod("ApplyTacticalAdjustment", Flags), motor, "TryRetarget") >= 0,
            "Threat learned geometry can only retarget the existing Combat motor intent");
        Check(MethodCallOffset(travel.GetMethod("HoldAtRefuge", Flags), motor, "TryGuideNative") >= 0,
            "Travel refuge holding centralizes realized localGoal writes through FlightMotor");
        Check(MethodCallOffset(survival.GetMethod("ApplyNativeHomeAndBurrow", Flags), motor, "TryGuideNative") >= 0,
            "same-room environmental Home retreat uses FlightMotor while native Burrow stays special physics");
        Check(Enum.IsDefined(special, "NativeBurrow") && Enum.IsDefined(special, "NativeChain"),
            "Task14 R4 explicitly classifies native Burrow and Chain as special-physics owners");
        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), motor, "Reset") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), motor, "Reset") >= 0 &&
              MethodCallOffset(hooks.GetMethod("FlyNewRoom", Flags), motor, "Forget") >= 0,
            "FlightMotor/Fog transient state follows species lifecycle");

        Console.WriteLine("Task14 R4 B2: ordinary domain goal writers are centralized through FlightMotor; Combat responsibility extraction remains open.");
    }
}
''', encoding='utf-8')


# -----------------------------------------------------------------------------
# 11. Status document: mark B2 domain steering convergence, keep Combat/Observatory open.
# -----------------------------------------------------------------------------
status = ROOT / 'docs/Discussion/Task_14_R4_FlightMotorStatus.txt'
status.write_text('''Desert Batfly Task 14 — R4 FlightMotor / AI Responsibility Split Status
Revision: R4-B2 / 2026-09-07
Branch: task14-r4-flightmotor
Base: R3 code-side closed at a2260b110af22f84317c90d8dcd7ec6ec128a67c
Status: 【R4 进行中 / B2 ordinary goal writer convergence】

======================================================================
1. 已完成
======================================================================

R4-B1：
- DB_FlightMotor foundation；
- InjuryRecovery Dijkstra localGoal progression fix；
- Injury final flight modifier ownership moved to motor；
- DB_FogGoalModifier；
- core AI ordinary SteerOwned -> FlightMotor。

R4-B2：
- DB_FlightMotor.TryGuideNative：只提交 owner-validated localGoal + nominal speed，保留 Rain World native Fly physics；
- DB_FlightMotor.TryRetarget：Threat 等 modifier 只能调整同 owner、同 tick 已存在 motor intent；
- Social localGoal writer -> FlightMotor；
- Travel refuge localGoal writer -> FlightMotor；
- Vengeance ForceFlight -> FlightMotor；
- immediate projectile evade goal -> FlightMotor；
- Threat learned Combat geometry -> FlightMotor retarget；
- Environment shelter goal恢复为 goal-only native guidance，避免 B1 意外把旧 goal-only 行为升级成第二个主动速度控制；
- Environmental same-room Home Dijkstra -> FlightMotor；
- SurvivalBridge 不再 save/orig/restore localGoal；
- Native Burrow / Native Chain 被显式标记为 special-physics owner。

======================================================================
2. R4 movement boundary
======================================================================

普通 Desert Batfly localGoal 的目标边界：

    domain decision / executor
        -> DB_FlightMotor.TrySteer / TryGuideNative / TryRetarget
        -> FlyAI.localGoal
        -> Rain World native collision / Fly physics
        -> DB_FlightMotor.ApplyPostPhysics
        -> Injury.ModifyFlight

TrySteer：用于旧行为本来就主动请求 velocity 的 owner，例如 Combat/Vengeance。
TryGuideNative：用于旧行为只提交 localGoal 的 owner，例如 Social/Travel/Environment/projectile evade。
TryRetarget：用于 Threat learned tactics 这类 modifier；不得改变 PrimaryOwner。

特殊物理继续允许绕过普通 motor：
- Creature physics / external grasp；
- Shortcut；
- Emergence；
- Combat Attach；
- Combat Interfere；
- native Burrow；
- native Chain；
- 瞬时 hit/collision/retaliation impulse。

======================================================================
3. 行为保持说明
======================================================================

B2 不把所有 owner 强制改成同一种飞法。

- Social 原来只写 localGoal -> 继续只提交 native goal；
- Travel refuge 原来只写 localGoal/Dijkstra -> 继续 native guidance；
- projectile evade 原来只写 localGoal + nominal injury speed -> 继续 native guidance；
- Environment 原来只写 shelter localGoal -> 恢复为 goal-only guidance；
- Vengeance 原来会主动 Lerp velocity -> 继续 active TrySteer；
- Threat 仍只改变“怎么打”的几何，不获得 locomotion owner。

======================================================================
4. 当前仍 OPEN
======================================================================

R4-OPEN-01 — Combat state/motivation/contact responsibilities split out of DesertBatflyAI.
R4-OPEN-02 — final source audit of every localGoal / sustained velocity writer after Combat extraction.
R4-OPEN-03 — Observatory expose FlightMotor owner/goal/nominal speed and special-physics boundary.
R4-OPEN-04 — managed compile/integration and Rain World live scenarios deferred to final validation stage per project decision.

======================================================================
5. R4 完成条件
======================================================================

R4 代码侧完成要求：
- 普通飞行 localGoal 只由 DB_FlightMotor 写；
- Injury 只做 modifier；
- Fog 只做 goal modifier；
- Threat 只能 same-owner retarget；
- Combat responsibility 从旧 AI 拆出；
- special physics owner 完整；
- Observatory 显示 motor intent；
- source architecture regressions 通过。

Rain World live scenarios 统一留到最终重构验收阶段。
''', encoding='utf-8')


# -----------------------------------------------------------------------------
# 12. Strong source guards before committing.
# -----------------------------------------------------------------------------
checks = {
    'Social localGoal': ('src/Creatures/DesertBatfly/Social/DesertBatflySocialLife.cs', r'bat\.AI\.localGoal\s*(?:\+=|=)'),
    'Vengeance localGoal': ('src/Creatures/DesertBatfly/DesertBatflyIntimidation.cs', r'bat\.AI\.localGoal\s*(?:\+=|=)'),
    'ThreatRuntime localGoal': ('src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatRuntime.cs', r'bat\.AI\.localGoal\s*(?:\+=|=)'),
    'ThreatTactics localGoal': ('src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatTactics.cs', r'bat\.AI\.localGoal\s*(?:\+=|=)'),
}
for label, (p, pattern) in checks.items():
    if re.search(pattern, read(p)):
        raise SystemExit(f'{label}: direct localGoal writer remains')

if 'NominalFlightSpeed' in read('src/Creatures/DesertBatfly/Social/DesertBatflySocialLife.cs'):
    raise SystemExit('Social still references removed Injury.NominalFlightSpeed')
if 'NominalFlightSpeed' in read('src/Creatures/DesertBatfly/DesertBatflyIntimidation.cs'):
    raise SystemExit('Vengeance still references removed Injury.NominalFlightSpeed')
if 'NominalFlightSpeed' in read('src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatTactics.cs'):
    raise SystemExit('Threat tactics still references removed Injury.NominalFlightSpeed')

# Self-delete so migration machinery is not part of final R4 diff.
Path('scripts/task14_r4_batch2_apply.py').unlink()
workflow = Path('.github/workflows/task14-r4-b2-one-shot.yml')
if workflow.exists(): workflow.unlink()

print('R4 B2 migration prepared successfully')
