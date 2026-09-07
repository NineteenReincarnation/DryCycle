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
        Type tactics = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyThreatTactics", true);
        Type hooks = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);

        Check(roomContext.Name == "DB_RoomContext" &&
              roomContext.GetMethod("For", Flags) != null &&
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

        string[] channels = Enum.GetNames(visibilityChannel);
        Check(Array.IndexOf(channels, "Creature") >= 0 &&
              Array.IndexOf(channels, "Player") >= 0 &&
              Array.IndexOf(channels, "Social") >= 0 &&
              Array.IndexOf(channels, "HeldItem") >= 0 &&
              Array.IndexOf(channels, "Projectile") >= 0,
            "Task14 R2 central visibility policy exposes creature/player/social/held/projectile channels");

        MethodInfo effectiveRange = visibility.GetMethod("EffectiveRange", Flags);
        object playerChannel = Enum.Parse(visibilityChannel, "Player");
        object projectileChannel = Enum.Parse(visibilityChannel, "Projectile");
        float clearPlayer = (float)effectiveRange.Invoke(null, new object[] { 430f, 1f, playerChannel, false });
        float densePlayer = (float)effectiveRange.Invoke(null, new object[] { 430f, 0.25f, playerChannel, false });
        float denseProjectile = (float)effectiveRange.Invoke(null, new object[] { 230f, 0.25f, projectileChannel, true });
        float closeFloor = (float)visibility.GetField("CloseProjectileFloor", Flags).GetRawConstantValue();
        Check(Math.Abs(clearPlayer - 430f) < 0.001f && densePlayer < clearPlayer,
            "Task14 R2 visibility confidence reduces long-range recognition but leaves clear weather unchanged");
        Check(denseProjectile >= Math.Min(230f, closeFloor) && denseProjectile <= 230f,
            "Task14 R2 DenseFog preserves a bounded close real-projectile fallback");

        Check(weaponPerception.GetMethod("TryFindIncomingProjectile", Flags) != null &&
              weaponPerception.GetMethod("TryFindImmediateThreat", Flags) != null &&
              weaponPerception.GetMethod("TryObserveHeldThreats", Flags) != null,
            "Task14 R2 weapon perception centralizes incoming, immediate and held-item observation");
        foreach (string field in new[]
        {
            "VisibleSpear", "VisibleRock", "VisibleExplosive", "VisibleStartle", "VisibleShock"
        })
            Check(heldObservation.GetField(field, Flags) != null,
                "Task14 R2 held threat observation exposes " + field);

        MethodInfo playerBySlot = tactics.GetMethod("PlayerBySlot", Flags);
        MethodInfo tryProfile = tactics.GetMethod("TryProfile", Flags);
        Check(MethodCallOffset(playerBySlot, roomContext, "For") >= 0,
            "Task14 R2 Threat tactics reuses shared room player observation instead of walking game players");
        Check(MethodCallOffset(tryProfile, weaponPerception, "TryObserveHeldThreats") >= 0,
            "Task14 R2 Threat tactics reuses shared held-item perception instead of reclassifying its own observation path");

        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), roomContext, "Reset") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), roomContext, "Reset") >= 0,
            "Task14 R2 RoomContext cache follows Desert Batfly enable/disable lifecycle");

        Check(roomContext.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              visibility.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              weaponPerception.Name.StartsWith("DB_", StringComparison.Ordinal),
            "Task14 R2 new architecture uses DB_ domain names and no TaskXX production type");

        Console.WriteLine(
            "Task14 R2 foundation: lazy RoomContext, centralized visibility/weapon perception, DenseFog close-projectile fallback, Threat held/player consumer migration and lifecycle guards verified.");
    }
}
