using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.DayNight;

internal static class PaletteLighting
{
    private sealed class CameraState
    {
        public int PaletteA = int.MinValue;
        public int PaletteB = int.MinValue;
        public float Blend = -1f;
        public bool ForceRefresh = true;
    }

    private static ConditionalWeakTable<RoomCamera, CameraState> _cameraStates = new();
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.RoomCamera.UpdateDayNightPalette += RoomCamera_UpdateDayNightPalette;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RoomCamera.UpdateDayNightPalette -= RoomCamera_UpdateDayNightPalette;
        _cameraStates = new ConditionalWeakTable<RoomCamera, CameraState>();
        _enabled = false;
    }

    public static void ForceRefresh(RoomCamera camera)
    {
        if (camera == null)
        {
            return;
        }

        CameraState state = _cameraStates.GetOrCreateValue(camera);
        state.ForceRefresh = true;

        if (camera.room?.world != null
            && WorldClockHooks.TryGetClock(camera.room.world, out WorldClock clock))
        {
            ApplyAuthoredPaletteBlend(camera, clock, force: true);
            state.ForceRefresh = false;
        }
    }

    private static void RoomCamera_UpdateDayNightPalette(
        On.RoomCamera.orig_UpdateDayNightPalette orig,
        RoomCamera self)
    {
        CameraState state = self == null
            ? null
            : _cameraStates.GetOrCreateValue(self);

        if (self?.room?.world == null
            || !WorldClockHooks.TryGetClock(self.room.world, out WorldClock clock))
        {
            // Vanilla/DLC/mod logic is about to own the actual camera palette. Invalidate
            // DryCycle's numeric cache now, otherwise returning to an enabled region can
            // incorrectly compare against the OLD DryCycle values and skip reapplying
            // them even though the camera was changed while DryCycle was inactive.
            if (state != null)
            {
                state.PaletteA = int.MinValue;
                state.PaletteB = int.MinValue;
                state.Blend = -1f;
                state.ForceRefresh = true;
            }

            orig(self);
            return;
        }

        self.dayNightNeedsRefresh = false;

        if (!state.ForceRefresh && self.frameCount % 4 != 0)
        {
            return;
        }

        ApplyAuthoredPaletteBlend(self, clock, state.ForceRefresh);
        state.ForceRefresh = false;
    }

    private static void ApplyAuthoredPaletteBlend(RoomCamera camera, WorldClock clock, bool force)
    {
        if (camera?.room?.roomSettings == null)
        {
            return;
        }

        RoomSettings roomSettings = camera.room.roomSettings;
        DayNightPaletteSettings.Values settings = DayNightPaletteSettings.Get(roomSettings);

        int basePalette = Math.Max(0, roomSettings.Palette);
        int duskPalette = Math.Max(0, settings.DuskPalette);
        int nightPalette = Math.Max(0, settings.NightPalette);

        PaletteTransition transition = PaletteTransition.FromDayProgress(
            clock.DayProgress,
            basePalette,
            duskPalette,
            nightPalette);

        CameraState state = _cameraStates.GetOrCreateValue(camera);
        if (!force
            && state.PaletteA == transition.PaletteA
            && state.PaletteB == transition.PaletteB
            && Mathf.Abs(state.Blend - transition.Blend) < 0.0025f)
        {
            return;
        }

        state.PaletteA = transition.PaletteA;
        state.PaletteB = transition.PaletteB;
        state.Blend = transition.Blend;

        camera.ChangeBothPalettes(
            transition.PaletteA,
            transition.PaletteB,
            transition.Blend);
    }

    private readonly struct PaletteTransition
    {
        public readonly int PaletteA;
        public readonly int PaletteB;
        public readonly float Blend;

        public PaletteTransition(int paletteA, int paletteB, float blend)
        {
            PaletteA = paletteA;
            PaletteB = paletteB;
            Blend = Mathf.Clamp01(blend);
        }

        public static PaletteTransition FromDayProgress(
            float dayProgress,
            int basePalette,
            int duskPalette,
            int nightPalette)
        {
            float p = Mathf.Repeat(dayProgress, 1f);

            if (p < 0.420f)
            {
                return new PaletteTransition(basePalette, -1, 0f);
            }

            if (p < 0.500f)
            {
                float t = Smooth01(Mathf.InverseLerp(0.420f, 0.500f, p));
                return new PaletteTransition(basePalette, duskPalette, t);
            }

            if (p < 0.600f)
            {
                float t = Smooth01(Mathf.InverseLerp(0.500f, 0.600f, p));
                return new PaletteTransition(duskPalette, nightPalette, t);
            }

            if (p < 0.920f)
            {
                return new PaletteTransition(nightPalette, -1, 0f);
            }

            float preDawn = Smooth01(Mathf.InverseLerp(0.920f, 1f, p));
            return new PaletteTransition(nightPalette, basePalette, preDawn);
        }

        private static float Smooth01(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * (3f - 2f * t);
        }
    }
}
