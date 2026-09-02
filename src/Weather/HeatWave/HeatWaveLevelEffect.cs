using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Applies Rain World's own LevelHeat shader while scheduled HeatWave is active.
///
/// DryCycle borrows ownership of levelGraphic only for the active weather interval. The
/// exact shader/alpha state that existed before DryCycle took ownership is restored once
/// on release; after that DryCycle no longer touches the camera level shader. This keeps
/// HeatWave compatible with authored vanilla effects and with other systems that may
/// legitimately replace levelGraphic later.
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

        // Heat stored in the room remains visible when direct sunlight weakens. Solar
        // amplifies the melt but never gates the weather presentation.
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
        float authoredAmount =
            room.roomSettings?.GetEffectAmount(RoomSettings.RoomEffect.Type.HeatWave) ?? 0f;
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
        if (state.Applied && state.OwnerRoom != room)
        {
            Release(camera, state.OwnerRoom);
        }

        if (!state.Applied)
        {
            state.PreviousShader = camera.levelGraphic.shader;
            state.PreviousAlpha = camera.levelGraphic.alpha;
            state.OwnerRoom = room;
            state.AppliedShader = levelHeat;
            state.Applied = true;
        }
        else
        {
            state.OwnerRoom = room;
            state.AppliedShader = levelHeat;
        }

        camera.levelGraphic.shader = levelHeat;
        camera.levelGraphic.alpha = Mathf.Clamp01(combinedAmount) * 0.5f;
        return true;
    }

    internal static void Release(RoomCamera camera, Room ownerRoom)
    {
        if (camera == null ||
            !CameraStates.TryGetValue(camera, out CameraState state) ||
            !state.Applied)
        {
            return;
        }

        if (ownerRoom != null && state.OwnerRoom != ownerRoom)
        {
            return;
        }

        FShader previousShader = state.PreviousShader;
        float previousAlpha = state.PreviousAlpha;
        FShader appliedShader = state.AppliedShader;

        // If another system has already replaced our shader, it owns the camera now and
        // must not be overwritten by our cleanup.
        bool stillOwnsLevelGraphic =
            camera.levelGraphic != null &&
            camera.levelGraphic.shader == appliedShader;

        state.Applied = false;
        state.OwnerRoom = null;
        state.AppliedShader = null;
        state.PreviousShader = null;
        state.PreviousAlpha = 1f;

        if (!stillOwnsLevelGraphic || camera.levelGraphic == null)
        {
            return;
        }

        if (previousShader != null)
        {
            camera.levelGraphic.shader = previousShader;
        }
        else
        {
            camera.levelGraphic.shader = FShader.defaultShader;
        }
        camera.levelGraphic.alpha = previousAlpha;
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
            if (camera != null &&
                CameraStates.TryGetValue(camera, out CameraState state) &&
                state.Applied &&
                state.OwnerRoom == room)
            {
                Release(camera, room);
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
               room.roomSettings.GetEffectAmount(
                   MoreSlugcats.MoreSlugcatsEnums.RoomEffectType.BrokenPalette) != 0f;
    }

    private sealed class CameraState
    {
        internal Room OwnerRoom;
        internal FShader PreviousShader;
        internal FShader AppliedShader;
        internal float PreviousAlpha = 1f;
        internal bool Applied;
    }
}
