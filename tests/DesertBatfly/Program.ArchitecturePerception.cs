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
        Type heldObservation = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_HeldThreatObservation", true);
        Type ai = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_AI", true);
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

        MethodInfo aiScanWeapons = ai.GetMethod("ScanWeapons", Flags);
        MethodInfo aiScanCreatures = ai.GetMethod("ScanCreatures", Flags);
        MethodInfo acquireSlot = ai.GetMethod("AcquireSlot", Flags);
        MethodInfo socialHarass = ai.GetMethod("FindSocialHarassTarget", Flags);
        Check(MethodCallOffset(aiScanWeapons, weaponPerception, "TryFindImmediateThreat") >= 0,
            "Architecture shared perception DB_AI weapon scan consumes shared DB_WeaponPerception");
        Check(MethodCallOffset(aiScanCreatures, roomContext, "For") >= 0 &&
              MethodCallOffset(aiScanCreatures, visibility, "CanObserve") >= 0,
            "Architecture shared perception ordinary creature recognition consumes shared RoomContext + VisibilityPolicy");
        Check(MethodCallOffset(acquireSlot, roomContext, "For") >= 0,
            "Architecture shared perception AttackSlots enumerate the shared active-bat view");
        Check(MethodCallOffset(socialHarass, roomContext, "For") >= 0 &&
              MethodCallOffset(socialHarass, visibility, "CanObserve") >= 0,
            "Architecture shared perception social-harass candidate recognition reuses shared bat and visibility observations");

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

        MethodInfo tacticPlayerBySlot = tactics.GetMethod("PlayerBySlot", Flags);
        MethodInfo tryProfile = tactics.GetMethod("TryProfile", Flags);
        Check(MethodCallOffset(tacticPlayerBySlot, roomContext, "For") >= 0,
            "Architecture shared perception Threat tactics reuses shared room player observation instead of walking game players");
        Check(MethodCallOffset(tryProfile, weaponPerception, "TryObserveHeldThreats") >= 0,
            "Architecture shared perception Threat tactics reuses shared held-item perception instead of reclassifying its own observation path");

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
              weaponPerception.Name.StartsWith("DB_", StringComparison.Ordinal),
            "Architecture shared perception new architecture uses DB_ domain names and no TaskXX production type");

        Console.WriteLine(
            "Architecture shared perception complete: shared RoomContext with live membership revalidation, creature/AttackSlots/weapon candidate views, Threat current perception, Signal visual policy, lazy irrelevant-room gating and obsolete visual-bridge removal verified.");
    }
}
