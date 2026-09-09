using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunArchitecturePerception()
    {
        Type roomContext = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_RoomContext", true);
        Type visibility = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_VisibilityPolicy", true);
        Type visibilityChannel = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_VisibilityChannel", true);
        Type weaponPerception = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_WeaponPerception", true);
        Type creaturePerception = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_CreaturePerception", true);
        Type heldObservation = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_HeldThreatObservation", true);
        Type ai = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_AI", true);
        Type combat = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_CombatRuntime", true);
        Type frameContextRuntime = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_FrameContextRuntime", true);
        Type threat = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatRuntime", true);
        Type tactics = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatTactics", true);
        Type signalRuntime = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_SignalRuntime", true);
        Type signalRoomRuntime = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_SignalRoomRuntime", true);
        Type environmentRuntime = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_EnvironmentRoomRuntime", true);
        Type swarmRoom = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_SwarmRoom", true);
        Type hooks = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);

        Check(roomContext.Name == "DB_RoomContext" &&
              roomContext.GetMethod("For", Flags) != null &&
              roomContext.GetMethod("TryGetExisting", Flags) != null &&
              roomContext.GetMethod("Reset", Flags) != null &&
              roomContext.GetMethod("PlayerBySlot", Flags) != null,
            "Architecture shared perception exposes one DB_RoomContext access/lifecycle surface");

        int refreshTicks = (int)roomContext.GetField("RefreshIntervalTicks", Flags).GetRawConstantValue();
        Check(refreshTicks >= 6 && refreshTicks <= 16,
            "Architecture shared perception room observation refresh is bounded and low-frequency");
        foreach (string property in new[]
        {
            "Creatures", "Bats", "Players", "Weapons", "ThrownWeapons",
            "LastRefreshClock", "RefreshCount", "CreatureScanCount", "PhysicalObjectScanCount"
        })
            Check(roomContext.GetProperty(property, Flags) != null,
                "Architecture shared perception RoomContext exposes " + property);

        MethodInfo batsGetter = roomContext.GetProperty("Bats", Flags).GetGetMethod(true);
        MethodInfo playersGetter = roomContext.GetProperty("Players", Flags).GetGetMethod(true);
        MethodInfo thrownGetter = roomContext.GetProperty("ThrownWeapons", Flags).GetGetMethod(true);
        Check(MethodCallOffset(batsGetter, roomContext, "PruneBats") >= 0 &&
              MethodCallOffset(playersGetter, roomContext, "PrunePlayers") >= 0 &&
              MethodCallOffset(thrownGetter, roomContext, "PruneWeapons") >= 0 &&
              MethodCallOffset(roomContext.GetMethod("PlayerBySlot", Flags), roomContext, "PrunePlayers") >= 0,
            "Architecture shared perception caches candidate discovery but revalidates current membership/throw-state at consumption time");

        string[] channels = Enum.GetNames(visibilityChannel);
        foreach (string channel in new[]
                 { "Creature", "Player", "Social", "Signal", "HeldItem", "Projectile" })
            Check(Array.IndexOf(channels, channel) >= 0,
                "Architecture shared perception central visibility policy exposes " + channel + " channel");

        MethodInfo effectiveRange = visibility.GetMethod("EffectiveRange", Flags);
        object playerChannel = Enum.Parse(visibilityChannel, "Player");
        object signalChannel = Enum.Parse(visibilityChannel, "Signal");
        object projectileChannel = Enum.Parse(visibilityChannel, "Projectile");
        float clearPlayer = (float)effectiveRange.Invoke(null, new object[] { 430f, 1f, playerChannel, false });
        float densePlayer = (float)effectiveRange.Invoke(null, new object[] { 430f, 0.25f, playerChannel, false });
        float denseSignal = (float)effectiveRange.Invoke(null, new object[] { 300f, 0.25f, signalChannel, false });
        float denseProjectile = (float)effectiveRange.Invoke(null, new object[] { 230f, 0.25f, projectileChannel, true });
        float closeFloor = (float)visibility.GetField("CloseProjectileFloor", Flags).GetRawConstantValue();
        Check(Math.Abs(clearPlayer - 430f) < 0.001f && densePlayer < clearPlayer && denseSignal < 300f,
            "Architecture shared perception visibility confidence reduces approved long-range recognition while clear weather stays unchanged");
        Check(denseProjectile >= Math.Min(230f, closeFloor) && denseProjectile <= 230f,
            "Architecture shared perception DenseFog preserves a bounded close real-projectile fallback");

        Check(weaponPerception.GetMethod("TryFindIncomingProjectile", Flags) != null &&
              weaponPerception.GetMethod("TryFindIncomingProjectileFrom", Flags) != null &&
              weaponPerception.GetMethod("TryFindImmediateThreat", Flags) != null &&
              weaponPerception.GetMethod("TryObserveHeldThreats", Flags) != null,
            "Architecture shared perception weapon perception centralizes incoming, instigator-filtered, immediate and held-item observation");
        foreach (string field in new[]
        {
            "VisibleSpear", "VisibleRock", "VisibleExplosive", "VisibleStartle", "VisibleShock"
        })
            Check(heldObservation.GetField(field, Flags) != null,
                "Architecture shared perception held threat observation exposes " + field);

        // R6 moved these responsibilities out of DB_AI. The managed probe must validate the
        // current domain owners instead of keeping deleted facade methods alive for tests.
        Check(ai.GetMethod("ScanWeapons", Flags) == null &&
              ai.GetMethod("ScanCreatures", Flags) == null &&
              ai.GetMethod("AcquireSlot", Flags) == null &&
              ai.GetMethod("FindSocialHarassTarget", Flags) == null,
            "Architecture shared perception does not resurrect pre-R6 DB_AI scan/slot facades");

        MethodInfo perceptionScan = creaturePerception.GetMethod("ScanCreatures", Flags);
        Check(perceptionScan != null &&
              MethodCallOffset(perceptionScan, roomContext, "For") >= 0 &&
              MethodCallOffset(perceptionScan, visibility, "CanObserve") >= 0,
            "Architecture shared perception ordinary creature recognition belongs to DB_CreaturePerception and consumes RoomContext + VisibilityPolicy");

        int canObserveOffset = MethodCallOffset(perceptionScan, visibility, "CanObserve");
        int exactDistanceOffset = MethodCallOffset(perceptionScan, typeof(UnityEngine.Vector2), "Distance");
        Check(canObserveOffset >= 0 && exactDistanceOffset > canObserveOffset,
            "Architecture performance keeps exact creature distance sqrt behind visibility/range rejection");

        int scanInterval = (int)creaturePerception.GetField("ScanIntervalTicks", Flags).GetRawConstantValue();
        MethodInfo scanPhase = creaturePerception.GetMethod("ScanPhase", Flags);
        bool[] phaseBuckets = new bool[scanInterval];
        bool phasesBounded = scanInterval == 8 && scanPhase != null;
        if (phasesBounded)
        {
            for (int seed = 0; seed < 256; seed++)
            {
                int phase = (int)scanPhase.Invoke(null, new object[] { seed });
                if (phase < 1 || phase > scanInterval)
                {
                    phasesBounded = false;
                    break;
                }
                phaseBuckets[phase - 1] = true;
            }
        }
        bool allBucketsUsed = phasesBounded;
        for (int i = 0; i < phaseBuckets.Length; i++) allBucketsUsed &= phaseBuckets[i];
        Check(phasesBounded && allBucketsUsed,
            "Architecture performance disperses newly-realized creature scans across all stable 1..8 phases without extending the old maximum interval");

        MethodInfo acquireSlot = combat.GetMethod("AcquireSlot", Flags);
        MethodInfo socialHarass = combat.GetMethod("FindSocialHarassTarget", Flags);
        Check(acquireSlot != null && MethodCallOffset(acquireSlot, roomContext, "For") >= 0,
            "Architecture shared perception Combat AttackSlots enumerate the shared active-bat view");
        Check(socialHarass != null && MethodCallOffset(socialHarass, visibility, "CanObserve") >= 0,
            "Architecture shared perception Combat social-harass target recognition reuses central visibility observations");

        MethodInfo captureFrame = frameContextRuntime.GetMethod("Capture", Flags);
        Check(captureFrame != null &&
              MethodCallOffset(captureFrame, roomContext, "For") >= 0 &&
              MethodCallOffset(captureFrame, visibility, "CanObserve") >= 0 &&
              MethodCallOffset(captureFrame, weaponPerception, "TryFindIncomingProjectile") >= 0,
            "Architecture shared perception FrameContext consumes shared creature/player/projectile facts without a second scanner");

        Type threatRoomState = threat.GetNestedType("RoomState", BindingFlags.NonPublic);
        Check(threatRoomState != null &&
              threatRoomState.GetField("Players", Flags) == null &&
              threatRoomState.GetField("ThrownWeapons", Flags) == null &&
              threatRoomState.GetField("LastRefreshClock", Flags) == null &&
              threatRoomState.GetMethod("Refresh", Flags) == null,
            "Architecture shared perception Threat RoomState owns temporal evidence only, not a second room scanner");
        foreach (string field in new[]
                 { "RecentSpearThrow", "RecentRockThrow", "RecentExplosion", "RecentGrab", "CasualtyWindowStart", "CasualtyCount" })
            Check(threatRoomState.GetField(field, Flags) != null,
                "Architecture shared perception Threat RoomState retains threat-owned temporal evidence " + field);

        MethodInfo updateCue = threat.GetMethod("UpdateCue", Flags);
        MethodInfo nearestVisible = threat.GetMethod("NearestVisiblePlayer", Flags);
        Check(MethodCallOffset(updateCue, roomContext, "For") >= 0 &&
              MethodCallOffset(updateCue, weaponPerception, "TryObserveHeldThreats") >= 0 &&
              MethodCallOffset(updateCue, weaponPerception, "TryFindIncomingProjectileFrom") >= 0,
            "Architecture shared perception Threat current cue consumes shared player/held/projectile observations");
        Check(MethodCallOffset(nearestVisible, visibility, "CanObserve") >= 0,
            "Architecture shared perception Threat player recognition uses the central visibility policy");

        MethodInfo tryProfile = tactics.GetMethod("TryProfile", Flags);
        Check(tactics.GetMethod("PlayerBySlot", Flags) == null,
            "Architecture shared perception Threat tactics no longer owns the retired player-lookup helper from the ordinary evade facade");
        Check(MethodCallOffset(tryProfile, weaponPerception, "TryObserveHeldThreats") >= 0,
            "Architecture shared perception Threat tactics receives an already-selected player and reuses shared held-item perception");

        MethodInfo signalPerceive = signalRuntime.GetMethod("TryPerceive", Flags);
        Check(signalRuntime.GetMethod("VisualRadius", Flags) != null &&
              MethodCallOffset(signalPerceive, visibility, "EffectiveRange") >= 0 &&
              MethodCallOffset(signalPerceive, visibility, "CanObserve") >= 0,
            "Architecture shared perception Signals visual signal perception uses the central visibility authority while acoustic fallback stays local");

        MethodInfo updateRoom = hooks.GetMethod("UpdateRoom", Flags);
        int swarmUpdate = MethodCallOffset(updateRoom, swarmRoom, "UpdateRoom");
        int lazyGate = MethodCallOffset(updateRoom, roomContext, "TryGetExisting");
        int signalFor = MethodCallOffset(updateRoom, signalRoomRuntime, "For");
        int environmentUpdate = MethodCallOffset(updateRoom, environmentRuntime, "Update");
        Check(swarmUpdate >= 0 && lazyGate > swarmUpdate &&
              signalFor > lazyGate && environmentUpdate > lazyGate,
            "Architecture shared perception preserves DESERTSWARMROOM spawning globally but gates DB-only Signal/Environment work behind lazy active-room context");

        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), roomContext, "Reset") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), roomContext, "Reset") >= 0,
            "Architecture shared perception RoomContext cache follows Desert Batfly enable/disable lifecycle");

        Check(roomContext.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              visibility.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              weaponPerception.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              creaturePerception.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              combat.Name.StartsWith("DB_", StringComparison.Ordinal),
            "Architecture shared perception current domains use DB_ names and no TaskXX production type");

        Console.WriteLine(
            "Architecture shared perception complete: current Perception/Combat/FrameContext owners, staggered scan phases, distance/visibility ordering, shared RoomContext, Threat/Signal visual policy and lazy irrelevant-room gating verified.");
    }
}
