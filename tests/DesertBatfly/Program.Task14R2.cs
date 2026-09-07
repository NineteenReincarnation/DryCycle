using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunTask14R2()
    {
        Type roomContext = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_RoomContext", true);
        Type visibility = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_VisibilityPolicy", true);
        Type visibilityChannel = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_VisibilityChannel", true);
        Type weaponPerception = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_WeaponPerception", true);
        Type heldObservation = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_HeldThreatObservation", true);
        Type ai = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);
        Type threat = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyThreatRuntime", true);
        Type tactics = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatTactics", true);
        Type signalRuntime = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_SignalRuntime", true);
        Type signalRoomRuntime = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflySignalRoomRuntime", true);
        Type environmentRuntime = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_EnvironmentRoomRuntime", true);
        Type swarmRoom = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertSwarmRoom", true);
        Type hooks = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);

        Check(roomContext.Name == "DB_RoomContext" &&
              roomContext.GetMethod("For", Flags) != null &&
              roomContext.GetMethod("TryGetExisting", Flags) != null &&
              roomContext.GetMethod("Reset", Flags) != null &&
              roomContext.GetMethod("PlayerBySlot", Flags) != null,
            "Task14 R2 exposes one DB_RoomContext access/lifecycle surface");

        int refreshTicks = (int)roomContext.GetField("RefreshIntervalTicks", Flags).GetRawConstantValue();
        Check(refreshTicks >= 6 && refreshTicks <= 16,
            "Task14 R2 room observation refresh is bounded and low-frequency");
        foreach (string property in new[]
        {
            "Creatures", "Bats", "Players", "Weapons", "ThrownWeapons",
            "LastRefreshClock", "RefreshCount", "CreatureScanCount", "PhysicalObjectScanCount"
        })
            Check(roomContext.GetProperty(property, Flags) != null,
                "Task14 R2 RoomContext exposes " + property);

        MethodInfo batsGetter = roomContext.GetProperty("Bats", Flags).GetGetMethod(true);
        MethodInfo playersGetter = roomContext.GetProperty("Players", Flags).GetGetMethod(true);
        MethodInfo thrownGetter = roomContext.GetProperty("ThrownWeapons", Flags).GetGetMethod(true);
        Check(MethodCallOffset(batsGetter, roomContext, "PruneBats") >= 0 &&
              MethodCallOffset(playersGetter, roomContext, "PrunePlayers") >= 0 &&
              MethodCallOffset(thrownGetter, roomContext, "PruneWeapons") >= 0 &&
              MethodCallOffset(roomContext.GetMethod("PlayerBySlot", Flags), roomContext, "PrunePlayers") >= 0,
            "Task14 R2 caches candidate discovery but revalidates current membership/throw-state at consumption time");

        string[] channels = Enum.GetNames(visibilityChannel);
        foreach (string channel in new[]
                 { "Creature", "Player", "Social", "Signal", "HeldItem", "Projectile" })
            Check(Array.IndexOf(channels, channel) >= 0,
                "Task14 R2 central visibility policy exposes " + channel + " channel");

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
            "Task14 R2 visibility confidence reduces approved long-range recognition while clear weather stays unchanged");
        Check(denseProjectile >= Math.Min(230f, closeFloor) && denseProjectile <= 230f,
            "Task14 R2 DenseFog preserves a bounded close real-projectile fallback");

        Check(weaponPerception.GetMethod("TryFindIncomingProjectile", Flags) != null &&
              weaponPerception.GetMethod("TryFindIncomingProjectileFrom", Flags) != null &&
              weaponPerception.GetMethod("TryFindImmediateThreat", Flags) != null &&
              weaponPerception.GetMethod("TryObserveHeldThreats", Flags) != null,
            "Task14 R2 weapon perception centralizes incoming, instigator-filtered, immediate and held-item observation");
        foreach (string field in new[]
        {
            "VisibleSpear", "VisibleRock", "VisibleExplosive", "VisibleStartle", "VisibleShock"
        })
            Check(heldObservation.GetField(field, Flags) != null,
                "Task14 R2 held threat observation exposes " + field);

        MethodInfo aiScanWeapons = ai.GetMethod("ScanWeapons", Flags);
        MethodInfo aiScanCreatures = ai.GetMethod("ScanCreatures", Flags);
        MethodInfo acquireSlot = ai.GetMethod("AcquireSlot", Flags);
        MethodInfo socialHarass = ai.GetMethod("FindSocialHarassTarget", Flags);
        Check(MethodCallOffset(aiScanWeapons, weaponPerception, "TryFindImmediateThreat") >= 0,
            "Task14 R2 DesertBatflyAI weapon scan consumes shared DB_WeaponPerception");
        Check(MethodCallOffset(aiScanCreatures, roomContext, "For") >= 0 &&
              MethodCallOffset(aiScanCreatures, visibility, "CanObserve") >= 0,
            "Task14 R2 ordinary creature recognition consumes shared RoomContext + VisibilityPolicy");
        Check(MethodCallOffset(acquireSlot, roomContext, "For") >= 0,
            "Task14 R2 AttackSlots enumerate the shared active-bat view");
        Check(MethodCallOffset(socialHarass, roomContext, "For") >= 0 &&
              MethodCallOffset(socialHarass, visibility, "CanObserve") >= 0,
            "Task14 R2 social-harass candidate recognition reuses shared bat and visibility observations");

        Type threatRoomState = threat.GetNestedType("RoomState", BindingFlags.NonPublic);
        Check(threatRoomState != null &&
              threatRoomState.GetField("Players", Flags) == null &&
              threatRoomState.GetField("ThrownWeapons", Flags) == null &&
              threatRoomState.GetField("LastRefreshClock", Flags) == null &&
              threatRoomState.GetMethod("Refresh", Flags) == null,
            "Task14 R2 Threat RoomState owns temporal evidence only, not a second room scanner");
        foreach (string field in new[]
                 { "RecentSpearThrow", "RecentRockThrow", "RecentExplosion", "RecentGrab", "CasualtyWindowStart", "CasualtyCount" })
            Check(threatRoomState.GetField(field, Flags) != null,
                "Task14 R2 Threat RoomState retains threat-owned temporal evidence " + field);

        MethodInfo updateCue = threat.GetMethod("UpdateCue", Flags);
        MethodInfo nearestVisible = threat.GetMethod("NearestVisiblePlayer", Flags);
        Check(MethodCallOffset(updateCue, roomContext, "For") >= 0 &&
              MethodCallOffset(updateCue, weaponPerception, "TryObserveHeldThreats") >= 0 &&
              MethodCallOffset(updateCue, weaponPerception, "TryFindIncomingProjectileFrom") >= 0,
            "Task14 R2 Threat current cue consumes shared player/held/projectile observations");
        Check(MethodCallOffset(nearestVisible, visibility, "CanObserve") >= 0,
            "Task14 R2 Threat player recognition uses the central visibility policy");

        MethodInfo tacticPlayerBySlot = tactics.GetMethod("PlayerBySlot", Flags);
        MethodInfo tryProfile = tactics.GetMethod("TryProfile", Flags);
        Check(MethodCallOffset(tacticPlayerBySlot, roomContext, "For") >= 0,
            "Task14 R2 Threat tactics reuses shared room player observation instead of walking game players");
        Check(MethodCallOffset(tryProfile, weaponPerception, "TryObserveHeldThreats") >= 0,
            "Task14 R2 Threat tactics reuses shared held-item perception instead of reclassifying its own observation path");

        MethodInfo signalPerceive = signalRuntime.GetMethod("TryPerceive", Flags);
        Check(signalRuntime.GetMethod("VisualRadius", Flags) != null &&
              MethodCallOffset(signalPerceive, visibility, "EffectiveRange") >= 0 &&
              MethodCallOffset(signalPerceive, visibility, "CanObserve") >= 0,
            "Task14 R2 Task12 visual signal perception uses the central visibility authority while acoustic fallback stays local");

        Check(mod.GetType(
                  "DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSignalBridge", false) == null &&
              mod.GetType(
                  "DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalThreatBridge", false) == null,
            "Task14 R2 removes obsolete internal visual Signal/Threat detour bridges");

        MethodInfo updateRoom = hooks.GetMethod("UpdateRoom", Flags);
        int swarmUpdate = MethodCallOffset(updateRoom, swarmRoom, "UpdateRoom");
        int lazyGate = MethodCallOffset(updateRoom, roomContext, "TryGetExisting");
        int signalFor = MethodCallOffset(updateRoom, signalRoomRuntime, "For");
        int environmentUpdate = MethodCallOffset(updateRoom, environmentRuntime, "Update");
        Check(swarmUpdate >= 0 && lazyGate > swarmUpdate &&
              signalFor > lazyGate && environmentUpdate > lazyGate,
            "Task14 R2 preserves DESERTSWARMROOM spawning globally but gates DB-only Signal/Environment work behind lazy active-room context");

        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), roomContext, "Reset") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), roomContext, "Reset") >= 0,
            "Task14 R2 RoomContext cache follows Desert Batfly enable/disable lifecycle");

        Check(roomContext.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              visibility.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              weaponPerception.Name.StartsWith("DB_", StringComparison.Ordinal),
            "Task14 R2 new architecture uses DB_ domain names and no TaskXX production type");

        Console.WriteLine(
            "Task14 R2 complete: shared RoomContext with live membership revalidation, creature/AttackSlots/weapon candidate views, Threat current perception, Signal visual policy, lazy irrelevant-room gating and obsolete visual-bridge removal verified.");
    }
}
