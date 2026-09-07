from pathlib import Path

ROOT=Path('.')

def rw(path): return (ROOT/path).read_text(encoding='utf-8')
def ww(path,text): (ROOT/path).write_text(text,encoding='utf-8')
def once(text,old,new,label):
    c=text.count(old)
    if c!=1: raise SystemExit(f'{label}: expected 1 match, found {c}')
    return text.replace(old,new,1)

# 1) FlightMotor debug state: expose submitted intent and post-injury result.
p='src/Creatures/DesertBatfly/Runtime/DB_FlightMotor.cs'
s=rw(p)
s=once(s,'namespace DryCycle.Creatures.DesertBatfly;\n\n/// <summary>','namespace DryCycle.Creatures.DesertBatfly;\n\ninternal readonly struct DB_FlightMotorDebugState\n{\n    internal readonly int Clock;\n    internal readonly DB_BehaviorOwner Owner;\n    internal readonly Vector2 Goal;\n    internal readonly float NominalSpeed;\n    internal readonly bool ActiveSteer;\n    internal readonly Vector2 RequestedVelocity;\n    internal readonly bool PostPhysicsApplied;\n    internal readonly Vector2 PostPhysicsVelocity;\n\n    internal DB_FlightMotorDebugState(int clock, DB_BehaviorOwner owner, Vector2 goal, float nominalSpeed, bool activeSteer, Vector2 requestedVelocity, bool postPhysicsApplied, Vector2 postPhysicsVelocity)\n    {\n        Clock=clock; Owner=owner; Goal=goal; NominalSpeed=nominalSpeed; ActiveSteer=activeSteer;\n        RequestedVelocity=requestedVelocity; PostPhysicsApplied=postPhysicsApplied; PostPhysicsVelocity=postPhysicsVelocity;\n    }\n}\n\n/// <summary>','motor debug struct')
s=once(s,'        internal float NominalSpeed;\n    }','        internal float NominalSpeed;\n        internal bool ActiveSteer;\n        internal Vector2 RequestedVelocity;\n        internal bool PostPhysicsApplied;\n        internal Vector2 PostPhysicsVelocity;\n    }','motor state fields')
s=once(s,'        state.NominalSpeed = nominalSpeed;\n        return true;','        state.NominalSpeed = nominalSpeed;\n        state.ActiveSteer = true;\n        state.RequestedVelocity = requested;\n        state.PostPhysicsApplied = false;\n        state.PostPhysicsVelocity = default;\n        return true;','TrySteer debug state')
s=once(s,'        state.NominalSpeed = Mathf.Max(0f, nominalSpeed);\n        return true;','        state.NominalSpeed = Mathf.Max(0f, nominalSpeed);\n        state.ActiveSteer = false;\n        state.RequestedVelocity = default;\n        state.PostPhysicsApplied = false;\n        state.PostPhysicsVelocity = default;\n        return true;','TryGuide debug state')
s=once(s,'        bat.mainBodyChunk.vel = bat.Injury.ModifyFlight(\n            previousVelocity,\n            bat.mainBodyChunk.vel,\n            nominalSpeed);','        bat.mainBodyChunk.vel = bat.Injury.ModifyFlight(\n            previousVelocity,\n            bat.mainBodyChunk.vel,\n            nominalSpeed);\n        if (states.TryGetValue(bat, out State postState) && postState.Clock == clock)\n        {\n            postState.PostPhysicsApplied = true;\n            postState.PostPhysicsVelocity = bat.mainBodyChunk.vel;\n        }','post physics debug')
marker='    internal static bool TryGetIntent(\n'
insert='    internal static bool TryGetDebugState(DesertBatfly bat, out DB_FlightMotorDebugState debug)\n    {\n        debug = default;\n        if (bat?.room == null || !states.TryGetValue(bat, out State state)) return false;\n        int clock = bat.room.game?.clock ?? int.MinValue;\n        if (state.Clock != clock) return false;\n        debug = new DB_FlightMotorDebugState(state.Clock, state.Owner, state.Goal, state.NominalSpeed, state.ActiveSteer, state.RequestedVelocity, state.PostPhysicsApplied, state.PostPhysicsVelocity);\n        return true;\n    }\n\n'
s=once(s,marker,insert+marker,'insert motor debug api')
ww(p,s)

# 2) Combat debug properties, replacing stale AI reflection consumers.
p='src/Creatures/DesertBatfly/Combat/DB_CombatRuntime.cs'
s=rw(p)
s=once(s,'    internal bool HasSlot => hasSlot;','    internal bool HasSlot => hasSlot;\n    internal int InterestTicks => interest;\n    internal int UnseenTicks => unseen;\n    internal int PhaseTicks => ticks;','combat debug properties')
ww(p,s)

# 3) Observatory source: stop reflecting moved fields and add FlightMotor section.
p='src/Debug/AIDebugger/Sources/DesertBatflyDebugSource.cs'
s=rw(p)
for line in [
'    private static readonly FieldInfo MemoryField = typeof(DesertBatflyAI).GetField("memory", PrivateInstance);\n',
'    private static readonly FieldInfo InterestField = typeof(DesertBatflyAI).GetField("interest", PrivateInstance);\n',
'    private static readonly FieldInfo UnseenField = typeof(DesertBatflyAI).GetField("unseen", PrivateInstance);\n',
'    private static readonly FieldInfo HasSlotField = typeof(DesertBatflyAI).GetField("hasSlot", PrivateInstance);\n']:
    if line not in s: raise SystemExit('missing stale reflection line: '+line.strip())
    s=s.replace(line,'')
s=once(s,'.Add("field.memory", "DesertBatflyAI.memory", Read<int>(MemoryField, ai))\n            .Add("field.interest", "DesertBatflyAI.interest", Read<int>(InterestField, ai))\n            .Add("field.pursuit", "DesertBatflyAI.pursuit", Read<int>(PursuitField, ai))\n            .Add("field.unseen", "DesertBatflyAI.unseen", Read<int>(UnseenField, ai))\n            .Add("field.has_slot", "DesertBatflyAI.hasSlot", Read<bool>(HasSlotField, ai)));','.Add("field.memory", "DB_CombatRuntime.Memory", ai.Combat.Memory)\n            .Add("field.interest", "DB_CombatRuntime.InterestTicks", ai.Combat.InterestTicks)\n            .Add("field.pursuit", "DesertBatflyAI.pursuit", Read<int>(PursuitField, ai))\n            .Add("field.unseen", "DB_CombatRuntime.UnseenTicks", ai.Combat.UnseenTicks)\n            .Add("field.has_slot", "DB_CombatRuntime.HasSlot", ai.Combat.HasSlot));','combat observatory fields')
s=once(s,'        BuildArbiterSection(snapshot, bat);\n        BuildDecisionStack(snapshot, bat);','        BuildArbiterSection(snapshot, bat);\n        BuildFlightMotorSection(snapshot, bat);\n        BuildDecisionStack(snapshot, bat);','motor section call')
marker='    private static void BuildDecisionStack(AIDebugSnapshot snapshot, DesertBatfly bat)\n'
method='''    private static void BuildFlightMotorSection(AIDebugSnapshot snapshot, DesertBatfly bat)\n    {\n        var section = new AIDebugSection("section.flight_motor");\n        if (bat?.room == null || !DB_FlightMotor.TryGetDebugState(bat, out DB_FlightMotorDebugState motor))\n        {\n            DB_SpecialPhysicsOwner special = DB_SpecialPhysicsOwner.None;\n            if (bat != null && DB_BehaviorArbiter.TryGetResolution(bat, out DB_BehaviorResolution unresolvedResolution))\n                special = unresolvedResolution.SpecialPhysicsOwner;\n            section.Add("field.motor_active", "DB_FlightMotor.TryGetDebugState", false)\n                .Add("field.motor_special_physics", "DB_BehaviorResolution.SpecialPhysicsOwner", special);\n            snapshot.Sections.Add(section);\n            return;\n        }\n\n        DB_SpecialPhysicsOwner specialOwner = DB_SpecialPhysicsOwner.None;\n        if (DB_BehaviorArbiter.TryGetResolution(bat, out DB_BehaviorResolution resolution))\n            specialOwner = resolution.SpecialPhysicsOwner;\n\n        section.Add("field.motor_active", "DB_FlightMotorDebugState", true)\n            .Add("field.motor_owner", "DB_FlightMotorDebugState.Owner", motor.Owner)\n            .Add("field.motor_goal", "DB_FlightMotorDebugState.Goal", motor.Goal)\n            .Add("field.motor_nominal_speed", "DB_FlightMotorDebugState.NominalSpeed", motor.NominalSpeed)\n            .Add("field.motor_active_steer", "DB_FlightMotorDebugState.ActiveSteer", motor.ActiveSteer)\n            .Add("field.motor_requested_velocity", "DB_FlightMotorDebugState.RequestedVelocity", motor.ActiveSteer ? motor.RequestedVelocity.ToString() : "native Fly physics")\n            .Add("field.motor_post_physics", "DB_FlightMotorDebugState.PostPhysicsApplied", motor.PostPhysicsApplied)\n            .Add("field.motor_post_velocity", "DB_FlightMotorDebugState.PostPhysicsVelocity", motor.PostPhysicsApplied ? motor.PostPhysicsVelocity.ToString() : "—")\n            .Add("field.motor_special_physics", "DB_BehaviorResolution.SpecialPhysicsOwner", specialOwner);\n        snapshot.Sections.Add(section);\n    }\n\n'''
s=once(s,marker,method+marker,'insert flight motor observatory')
ww(p,s)

# 4) Localization: add R4 motor section/fields in EN and CN dictionaries.
p='src/Debug/AIDebugger/AIDebugLocalization.cs'
s=rw(p)
s=once(s,'        ["section.arbiter"] = "R3 Arbiter",\n        ["section.generic_ai"] = "Vanilla AI",','        ["section.arbiter"] = "R3 Arbiter",\n        ["section.flight_motor"] = "R4 Flight Motor",\n        ["section.generic_ai"] = "Vanilla AI",','english motor section')
s=once(s,'        ["field.arbiter_rejected"] = "Rejected",\n','        ["field.arbiter_rejected"] = "Rejected",\n        ["field.motor_active"] = "Motor intent active",\n        ["field.motor_owner"] = "Motor owner",\n        ["field.motor_goal"] = "Motor goal",\n        ["field.motor_nominal_speed"] = "Nominal speed",\n        ["field.motor_active_steer"] = "Active velocity steer",\n        ["field.motor_requested_velocity"] = "Requested velocity",\n        ["field.motor_post_physics"] = "Injury modifier applied",\n        ["field.motor_post_velocity"] = "Post-modifier velocity",\n        ["field.motor_special_physics"] = "Special physics boundary",\n','english motor fields')
s=once(s,'        ["section.arbiter"] = "R3 仲裁器",\n        ["section.generic_ai"] = "原版 AI",','        ["section.arbiter"] = "R3 仲裁器",\n        ["section.flight_motor"] = "R4 飞行执行器",\n        ["section.generic_ai"] = "原版 AI",','chinese motor section')
s=once(s,'        ["field.arbiter_rejected"] = "被拒提案",\n','        ["field.arbiter_rejected"] = "被拒提案",\n        ["field.motor_active"] = "飞行意图有效",\n        ["field.motor_owner"] = "飞行执行控制者",\n        ["field.motor_goal"] = "飞行目标",\n        ["field.motor_nominal_speed"] = "名义速度",\n        ["field.motor_active_steer"] = "主动速度控制",\n        ["field.motor_requested_velocity"] = "请求速度",\n        ["field.motor_post_physics"] = "已应用伤病修正",\n        ["field.motor_post_velocity"] = "修正后速度",\n        ["field.motor_special_physics"] = "特殊物理边界",\n','chinese motor fields')
ww(p,s)

# 5) R4 regression: require motor debug + observatory and stale reflection removal.
p='tests/DesertBatfly/Program.Task14R4.cs'
s=rw(p)
s=once(s,'        Type combatExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatExecutor", true);','        Type combatExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatExecutor", true);\n        Type motorDebug = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FlightMotorDebugState", true);\n        Type debugSource = mod.GetType("DryCycle.Debugging.AI.DesertBatflyDebugSource", true);','test types')
s=once(s,'              motor.GetMethod("TryGetIntent", Flags) != null,','              motor.GetMethod("TryGetIntent", Flags) != null &&\n              motor.GetMethod("TryGetDebugState", Flags) != null,','motor test debug api')
insert='''        Check(motorDebug.GetField("Owner", Flags) != null &&\n              motorDebug.GetField("Goal", Flags) != null &&\n              motorDebug.GetField("NominalSpeed", Flags) != null &&\n              motorDebug.GetField("PostPhysicsVelocity", Flags) != null,\n            "Task14 R4 FlightMotor exposes owner/goal/speed/post-injury debug state");\n        Check(debugSource.GetMethod("BuildFlightMotorSection", Flags) != null,\n            "Task14 R4 Observatory exposes FlightMotor intent and special-physics boundary");\n        Check(debugSource.GetField("MemoryField", Flags) == null &&\n              debugSource.GetField("InterestField", Flags) == null &&\n              debugSource.GetField("UnseenField", Flags) == null &&\n              debugSource.GetField("HasSlotField", Flags) == null,\n            "Task14 R4 Observatory no longer reflects Combat fields from the old AI shell");\n'''
s=once(s,'        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), motor, "Reset") >= 0 &&',insert+'        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), motor, "Reset") >= 0 &&','insert closeout tests')
s=s.replace('Task14 R4 B4: Combat execution, target selection, Harass motivation and retaliation preparation are all owned by DB_CombatRuntime; final Observatory/audit closeout remains.','Task14 R4 code-side complete: single ordinary FlightMotor boundary, Combat extraction, Observatory motor/debug migration and source writer audit are all guarded; live validation is deferred to final refactor acceptance.')
ww(p,s)

# 6) Status document closeout.
p='docs/Discussion/Task_14_R4_FlightMotorStatus.txt'
s=rw(p)
s=s.replace('Revision: R4-B4 / 2026-09-07','Revision: R4-Final / 2026-09-07')
s=s.replace('Status: 【R4 进行中 / B4 Combat responsibility extraction complete】','Status: 【R4 代码侧完成 / 实机验收统一延后】')
s=s.replace('R4-OPEN-01 — final source audit of every localGoal / sustained velocity writer and classify every remaining special-physics exception.\nR4-OPEN-02 — Observatory expose FlightMotor owner/goal/nominal speed/modifier result and special-physics boundary.\nR4-OPEN-03 — managed compile/integration and Rain World live scenarios deferred to final validation stage per project decision.','R4-CLOSED-01 — final source audit confirms ordinary localGoal writes are centralized in DB_FlightMotor; remaining direct writers are classified special physics.\nR4-CLOSED-02 — Observatory exposes FlightMotor owner/goal/nominal speed/requested velocity/post-Injury result/special-physics boundary and reads migrated Combat state from DB_CombatRuntime.\nR4-DEFERRED-VALIDATION — managed compile/integration and Rain World live scenarios are intentionally deferred to final refactor acceptance per project decision.')
s += '\n\n======================================================================\n8. R4-Final closeout\n======================================================================\n\nFinal source audit contract:\n- ordinary FlyAI.localGoal assignment authority: DB_FlightMotor only;\n- DesertBatflyEmergence direct localGoal remains explicit Emergence special physics;\n- sustained direct mainBodyChunk.vel writers outside DB_FlightMotor are limited to Emergence and Combat Attach/Interfere contact pinning;\n- one-shot collision/retaliation impulses remain classified instantaneous physics, not continuous locomotion controllers.\n\nObservatory now reads migrated Combat memory/interest/unseen/slot data from DB_CombatRuntime rather than stale DesertBatflyAI reflection, and exposes the current R4 FlightMotor intent plus post-Injury velocity result.\n\nR4 may proceed to R5 without intermediate live testing; all Rain World live validation remains part of the final refactor acceptance matrix.\n'
ww(p,s)

# remove one-shot machinery from final tree
for path in ['scripts/task14_r4_b5_closeout.py','.github/workflows/task14-r4-b5-closeout.yml']:
    q=ROOT/path
    if q.exists(): q.unlink()
print('R4 B5 closeout prepared')
