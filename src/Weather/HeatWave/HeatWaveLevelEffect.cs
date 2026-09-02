using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Applies Rain World's own LevelHeat shader while scheduled HeatWave is active.
/// This is the primary scene deformation layer: terrain/palette geometry receives the
/// same visual language as vanilla HeatWave instead of routing the whole camera through
/// a giant HeatDistortion pass.
/// </summary>
internal static class HeatWaveLevelEffect
{
    private const float Epsilon = 0.0001f;

    private static readonly ConditionalWeakTable<RoomCamera, CameraState> CameraStates = new();

    internal static float EvaluateWeatherAmount(float intensity, float solar)
    {
        float heat = Mathf.Clamp01(intensity);
        if (heat <= Epsilon)
        {
            return 0f;
        }

        // The ground/room can remain hot after direct sunlight weakens. Solar therefore
        // modulates, rather than gates, the vanilla-style level melt.
        return heat * Mathf.Lerp(0.72f, 1f, Mathf.Clamp01(solar));
    }

    internal static bool Apply(RoomCamera camera, Room room, float intensity, float solar)
    {
        if (camera?.levelGraphic == null || camera.game?.rainWorld == null || room == null)
        {
            return false;
        }

        if (HasConflictingVanillaLevelEffect(room))
        {
            Release(camera, room);
            return false;
        }

        float weatherAmount = EvaluateWeatherAmount(intensity, solar);
        float authoredAmount = room.roomSettings?.GetEffectAmount(RoomSettings.RoomEffect.Type.HeatWave) ?? 0f;
        float combinedAmount = Mathf.Max(authoredAmount, weatherAmount);
        if (combinedAmount <= Epsilon)
        {
            Release(camera, room);
            return false;
        }

        if (!camera.game.rainWorld.Shaders.TryGetValue("LevelHeat", out FShader levelHeat) ||
            levelHeat == null)
        {
            Release(camera, room);
            return false;
        }

        CameraState state = CameraStates.GetOrCreateValue(camera);
        state.OwnerRoom = room;
        state.Applied = true;

        camera.levelGraphic.shader = levelHeat;
        camera.levelGraphic.alpha = Mathf.Clamp01(combinedAmount) * 0.5f;
        return true;
    }

    internal static void Release(RoomCamera camera, Room ownerRoom)
    {
        if (camera == null || !CameraStates.TryGetValue(camera, out CameraState state))
        {
            return;
        }

        if (ownerRoom != null && state.OwnerRoom != ownerRoom)
        {
            return;
        }

        state.Applied = false;
        state.OwnerRoom = null;
        RestoreVanillaLevelState(camera, camera.room);
    }

    internal static void RestoreForRoom(Room room)
    {
        RainWorldGame game = room?.game;
        if (game?.cameras == null)
        {
            return;
        }

        for (int i = 0; i < game.cameras.Length; i++)
        {
            RoomCamera camera = game.cameras[i];
            if (camera == null)
            {
                continue;
            }

            if (CameraStates.TryGetValue(camera, out CameraState state) && state.OwnerRoom == room)
            {
                state.Applied = false;
                state.OwnerRoom = null;
                RestoreVanillaLevelState(camera, camera.room);
            }
        }
    }

    internal static bool IsApplied(Room room)
    {
        RainWorldGame game = room?.game;
        if (game?.cameras == null)
        {
            return false;
        }

        for (int i = 0; i < game.cameras.Length; i++)
        {
            RoomCamera camera = game.cameras[i];
            if (camera != null &&
                CameraStates.TryGetValue(camera, out CameraState state) &&
                state.Applied &&
                state.OwnerRoom == room)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasConflictingVanillaLevelEffect(Room room)
    {
        if (room?.roomSettings == null)
        {
            return false;
        }

        if (room.roomSettings.GetEffectAmount(RoomSettings.RoomEffect.Type.VoidMelt) > 0f)
        {
            return true;
        }

        return ModManager.MSC &&
               room.roomSettings.GetEffectAmount(MoreSlugcats.MoreSlugcatsEnums.RoomEffectType.BrokenPalette) != 0f;
    }

    private static void RestoreVanillaLevelState(RoomCamera camera, Room room)
    {
        if (camera?.levelGraphic == null || camera.game?.rainWorld == null || room?.roomSettings == null)
        {
            return;
        }

        RainWorld rainWorld = camera.game.rainWorld;

        if (ModManager.MSC &&
            room.roomSettings.GetEffectAmount(MoreSlugcats.MoreSlugcatsEnums.RoomEffectType.BrokenPalette) != 0f)
        {
            camera.levelGraphic.shader = FShader.defaultShader;
            return;
        }

        float voidMelt = room.roomSettings.GetEffectAmount(RoomSettings.RoomEffect.Type.VoidMelt);
        if (voidMelt > 0f && rainWorld.Shaders.TryGetValue("LevelMelt", out FShader levelMelt))
        {
            camera.levelGraphic.shader = levelMelt;
            camera.levelGraphic.alpha = voidMelt;
            return;
        }

        float authoredHeat = room.roomSettings.GetEffectAmount(RoomSettings.RoomEffect.Type.HeatWave);
        if (authoredHeat > 0f && rainWorld.Shaders.TryGetValue("LevelHeat", out FShader levelHeat))
        {
            camera.levelGraphic.shader = levelHeat;
            camera.levelGraphic.alpha = authoredHeat * 0.5f;
            return;
        }

        if (rainWorld.Shaders.TryGetValue("LevelColor", out FShader levelColor))
        {
            camera.levelGraphic.shader = levelColor;
        }
    }

    private sealed class CameraState
    {
        internal Room OwnerRoom;
        internal bool Applied;
    }
}
