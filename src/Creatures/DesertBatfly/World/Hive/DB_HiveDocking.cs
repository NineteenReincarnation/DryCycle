using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Single realized authority for the final BatHive tile -> Burrow transition.
/// Higher-level domains decide why a Desert Batfly is returning and which hive map to follow;
/// this class alone owns the last physical docking step once the bat reaches a hive tile.
/// DB_HiveTraffic separately serializes the approach corridor and keeps its claim until the
/// Burrowed hook confirms that vanilla actually moved the bat into the hive list.
/// </summary>
internal static class DB_HiveDocking
{
    internal static bool TryHandleEnvironment(
        DB_Creature bat,
        in DB_BehaviorResolution resolution)
    {
        if (bat?.room == null || bat.AI == null || bat.mainBodyChunk == null ||
            resolution.PrimaryOwner is not (
                DB_BehaviorOwner.EnvironmentHardSurvival or
                DB_BehaviorOwner.EnvironmentLocalSurvival) ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, resolution.PrimaryOwner))
            return false;

        if (!DB_EnvironmentRuntime.TryGetInfluence(
                bat, out DB_EnvironmentInfluence influence) ||
            (!DB_EnvironmentalPolicy.ShouldSeekHome(influence) &&
             !DB_EnvironmentalPolicy.ShouldBurrow(influence)))
            return false;

        // Ordinary environmental retreat must not steal an active formal attack. Hard
        // survival already suppresses combat and may force the final hive ingress.
        if (bat.DesertAI.FormalAttack && !influence.HardSurvival)
            return false;

        return TryHandleDocking(
            bat,
            resolution.PrimaryOwner,
            influence.HardSurvival ? 1.25f : 0.82f,
            influence.HardSurvival,
            "environmental Home docking on BatHive tile");
    }

    internal static bool TryHandleTravelReturnHome(DB_Creature bat)
    {
        return TryHandleDocking(
            bat,
            DB_BehaviorOwner.Travel,
            1.05f,
            true,
            "ReturnHome final BatHive ingress");
    }

    /// <summary>
    /// Travel-owned refuge holding may deliberately choose a BatHive node. Unlike ReturnHome it
    /// keeps its EmergencyRefuge intent after docking, but physically it uses the same native
    /// BatHive ingress transition. The Travel runtime already suppresses emergence while waiting
    /// at a refuge, and the room passive-release queue also excludes bats with a Travel intent.
    /// </summary>
    internal static bool TryHandleTravelRefuge(DB_Creature bat)
    {
        return TryHandleDocking(
            bat,
            DB_BehaviorOwner.Travel,
            1.25f,
            true,
            "EmergencyRefuge final BatHive ingress");
    }

    internal static bool TryHandleInjuryRecovery(DB_Creature bat)
    {
        return TryHandleDocking(
            bat,
            DB_BehaviorOwner.InjuryRecovery,
            0.80f,
            true,
            "injury recovery final BatHive ingress");
    }

    internal static bool TryHandleNativeRain(DB_Creature bat)
    {
        return TryHandleDocking(
            bat,
            DB_BehaviorOwner.NativeSpecial,
            2f,
            true,
            "native rain final BatHive ingress");
    }

    /// <summary>
    /// Returns true whenever this frame is physically inside an eligible hive tile and the
    /// accepted owner should keep control of docking. A true result therefore means either
    /// "settling onto the entrance" or "Burrow transition completed"; callers must not run a
    /// second steering/docking controller in the same frame.
    /// </summary>
    private static bool TryHandleDocking(
        DB_Creature bat,
        DB_BehaviorOwner owner,
        float afraidFloor,
        bool cancelCombat,
        string reason)
    {
        if (bat?.room == null || bat.AI == null || bat.mainBodyChunk == null ||
            bat.dead || !bat.Consious || bat.inShortcut || bat.Emergence?.Active == true ||
            bat.room.hives == null || bat.room.hives.Length == 0 ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, owner))
            return false;

        IntVector2 tile = bat.room.GetTilePosition(bat.mainBodyChunk.pos);
        if (!bat.room.GetTile(tile).hive)
            return false;

        DB_SocialRuntime.CancelForPriority(bat, reason);
        if (cancelCombat)
            bat.DesertAI.CancelAttack();

        bat.AI.leaveRoomDijkstra = -1;
        bat.AI.afraid = Mathf.Max(bat.AI.afraid, afraidFloor);

        // Match vanilla FlyAI docking: merely entering a hive tile is not enough. Keep a
        // small downward bias until the body touches the lower entrance surface; ContactPoint
        // y == -1 is the same condition vanilla uses before switching to Burrow.
        bat.mainBodyChunk.vel.y -= 1f;
        if (bat.mainBodyChunk.ContactPoint.y != -1)
            return true;

        bat.DesertAI.CancelAttack();
        bat.AI.ChangeBehavior(FlyAI.Behavior.Burrow);
        bat.burrowOrHangSpot = bat.mainBodyChunk.pos;
        bat.movMode = Fly.MovementMode.Burrow;
        return true;
    }
}
