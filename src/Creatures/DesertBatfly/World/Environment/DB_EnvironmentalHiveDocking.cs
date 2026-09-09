using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Completes the final native-style docking step for environment-driven Home return.
/// EnvironmentRuntime already owns the Dijkstra approach to a BatHive. This helper only
/// handles the small gap that vanilla FlyAI.Update normally owns: once a returning bat is
/// physically inside a hive tile, settle it onto the entrance and transition to Burrow.
/// </summary>
internal static class DB_EnvironmentalHiveDocking
{
    internal static bool TryExecute(
        DB_Creature bat,
        in DB_BehaviorResolution resolution)
    {
        if (bat?.room == null || bat.AI == null || bat.mainBodyChunk == null ||
            bat.dead || !bat.Consious || bat.inShortcut || bat.Emergence?.Active == true ||
            resolution.PrimaryOwner is not (
                DB_BehaviorOwner.EnvironmentHardSurvival or
                DB_BehaviorOwner.EnvironmentLocalSurvival) ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, resolution.PrimaryOwner))
            return false;

        if (!DB_EnvironmentRuntime.TryGetInfluence(
                bat, out DB_EnvironmentInfluence influence) ||
            !DB_EnvironmentalPolicy.ShouldSeekHome(influence) ||
            bat.room.hives == null || bat.room.hives.Length == 0)
            return false;

        // Match ApplyNativeHomeAndBurrow's ordinary combat guard. Hard survival may still
        // forcibly cancel combat; a non-hard environmental return must not steal a live attack.
        if (bat.DesertAI.FormalAttack && !influence.HardSurvival)
            return false;

        IntVector2 tile = bat.room.GetTilePosition(bat.mainBodyChunk.pos);
        if (!bat.room.GetTile(tile).hive)
            return false;

        DB_SocialRuntime.CancelForPriority(
            bat, "environmental Home docking on BatHive tile");
        if (influence.HardSurvival)
            bat.DesertAI.CancelAttack();

        // Vanilla FlyAI.Update does this whenever a frightened fly is in a hive tile. The
        // downward bias is essential: Dijkstra navigation reaches the hive cell, but contact
        // with the entrance surface is what makes the native Burrow transition reliable.
        bat.AI.afraid = Mathf.Max(
            bat.AI.afraid,
            influence.HardSurvival ? 1.25f : 0.82f);
        bat.mainBodyChunk.vel.y -= 1f;

        // Do not teleport or force Burrow in mid-air. Preserve the native docking contract:
        // the fly must actually touch the lower entrance surface while occupying a hive tile.
        if (bat.mainBodyChunk.ContactPoint.y != -1)
            return false;

        bat.DesertAI.CancelAttack();
        bat.AI.leaveRoomDijkstra = -1;
        bat.AI.ChangeBehavior(FlyAI.Behavior.Burrow);
        bat.burrowOrHangSpot = bat.mainBodyChunk.pos;
        bat.movMode = Fly.MovementMode.Burrow;
        return true;
    }
}
