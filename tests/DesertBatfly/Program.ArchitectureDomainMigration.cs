using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunArchitectureDomainMigration()
    {
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);
        Type runtimePatch = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RuntimePatch", true);
        Type sandbox = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Sandbox", true);
        Type warp = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_WarpCompatibility", true);

        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureHooks", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureRuntimePatch", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSandbox", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureWarpCompatibility", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_PlatformRoostRuntime", false) == null,
            "R6 current architecture keeps retired Integration/platform-hook identities physically absent");

        MethodInfo modsInit = hooks.GetMethod("RainWorld_OnModsInit", Flags);
        Check(MethodCallOffset(modsInit, sandbox, "Enable") >= 0 &&
              MethodCallOffset(modsInit, warp, "Enable") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), sandbox, "Disable") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), warp, "Disable") >= 0,
            "R6 current architecture preserves Sandbox/Warp lifecycle");
        Check(MethodCallOffset(sandbox.GetMethod("Enable", Flags), runtimePatch, "Create") >= 0 &&
              MethodCallOffset(sandbox.GetMethod("Enable", Flags), runtimePatch, "Patch") >= 0 &&
              MethodCallOffset(warp.GetMethod("Enable", Flags), runtimePatch, "Create") >= 0 &&
              MethodCallOffset(warp.GetMethod("Enable", Flags), runtimePatch, "Patch") >= 0,
            "R6 current architecture preserves the explicit external compatibility patch boundary");

        Type observatory = mod.GetType("DryCycle.Debugging.AI.DB_ObservatorySource", true);
        Type travelDebug = mod.GetType("DryCycle.Debugging.AI.DB_TravelDebugSource", true);
        Type socialDebug = mod.GetType("DryCycle.Debugging.AI.DB_SocialDebugSource", true);
        Type threatDebug = mod.GetType("DryCycle.Debugging.AI.DB_ThreatDebugSource", true);
        Type signalDebug = mod.GetType("DryCycle.Debugging.AI.DB_SignalDebugSource", true);
        Type environmentDebug = mod.GetType("DryCycle.Debugging.AI.DB_EnvironmentDebugSource", true);
        Check(travelDebug.GetField("inner", Flags)?.FieldType == observatory &&
              socialDebug.GetField("inner", Flags)?.FieldType == travelDebug &&
              threatDebug.GetField("inner", Flags)?.FieldType == socialDebug &&
              signalDebug.GetField("inner", Flags)?.FieldType == threatDebug &&
              environmentDebug.GetField("inner", Flags)?.FieldType == signalDebug,
            "R6 current architecture preserves Observatory enrichment order");

        Type creature = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Creature", true);
        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_AI", true);
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SocialRuntime", true);
        Type threat = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatRuntime", true);
        Type signal = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalRuntime", true);
        Type signalRoom = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalRoomRuntime", true);
        Type fear = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FearRuntime", true);
        Type vengeance = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VengeanceRuntime", true);
        Type environment = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentRuntime", true);
        Type travel = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_TravelRuntime", true);
        Type colony = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ColonyRuntime", true);
        Type eventHub = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EventHub", true);
        Type roomContext = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RoomContext", true);
        Type frameContext = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FrameContext", true);
        Type arbiter = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorArbiter", true);
        Type motor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FlightMotor", true);
        Type roostPolicy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RoostPolicy", true);
        Type roostAnchor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RoostAnchor", true);
        Type injuryRecovery = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_InjuryRecovery", true);

        Check(creature != null && ai != null && social != null && threat != null &&
              signal != null && signalRoom != null && fear != null && vengeance != null &&
              environment != null && travel != null && colony != null && eventHub != null &&
              roomContext != null && frameContext != null && arbiter != null && motor != null &&
              roostPolicy != null && roostAnchor != null,
            "R6 current architecture exposes all formal core and domain owners established so far");

        Check(roostAnchor.GetField("Tile", Flags) != null &&
              roostAnchor.GetField("Kind", Flags) != null &&
              roostAnchor.GetField("Spot", Flags) != null &&
              roostPolicy.GetMethod("TryGetAnchor", Flags) != null &&
              roostPolicy.GetMethod("IsStillValid", Flags) != null,
            "Floor/native tile roosts use one canonical tile-kind-spot anchor policy");

        Check(ai.GetField("roost", Flags)?.FieldType == roostAnchor,
            "DB_AI retains the canonical roost anchor instead of only a world-space Vector2");
        MethodInfo setRoostClaim = ai.GetMethod("SetRoostClaim", Flags);
        Type claimParameter = setRoostClaim?.GetParameters().Length == 1
            ? setRoostClaim.GetParameters()[0].ParameterType
            : null;
        Check(claimParameter != null &&
              (claimParameter == roostAnchor ||
               (claimParameter.IsByRef && claimParameter.GetElementType() == roostAnchor)),
            "DB_AI roost ownership accepts the canonical anchor fact");

        FieldInfo injuryTarget = injuryRecovery.GetField("recoveryRoostTarget", Flags);
        Check(injuryTarget != null && Nullable.GetUnderlyingType(injuryTarget.FieldType) == roostAnchor,
            "injury recovery preserves the canonical anchor across search/revalidation/commit");

        Type socialState = social.GetNestedType("State", Flags);
        FieldInfo socialRoost = socialState?.GetField("RoostAnchor", Flags);
        Check(socialRoost != null && Nullable.GetUnderlyingType(socialRoost.FieldType) == roostAnchor,
            "social tile-roost invitations preserve the canonical anchor instead of re-deriving a tile from Spot");

        Console.WriteLine("Architecture domain migration checkpoint checks pass: current owners, Integration lifecycle, canonical roost anchors and Observatory chain are protected.");
    }
}
