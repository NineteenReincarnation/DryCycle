using System;
using System.Runtime.CompilerServices;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

internal static class WeatherSpatialRuntime
{
    [ThreadStatic]
    private static Room _queryRoom;

    private static bool _enabled;

    internal static Room QueryRoom => _queryRoom;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        WeatherSpatialRegistry.Reload();
        WeatherSpatialMapMenuRuntime.Enable();
        WeatherSpatialSelectionUiCleanup.Enable();
        WeatherSpatialHoverHelpRuntime.Enable();
        WeatherSpatialTogglePaintRuntime.Enable();
        WeatherSpatialBinaryRuleUiRuntime.Enable();
        WeatherSpatialIssueWrapRuntime.Enable();
        WeatherSpatialPreviewPersistenceRuntime.Enable();
        On.Room.Update += Room_Update;
        On.RoomCamera.Update += RoomCamera_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Room.Update -= Room_Update;
        On.RoomCamera.Update -= RoomCamera_Update;
        WeatherSpatialPreviewPersistenceRuntime.Disable();
        WeatherSpatialIssueWrapRuntime.Disable();
        WeatherSpatialBinaryRuleUiRuntime.Disable();
        WeatherSpatialTogglePaintRuntime.Disable();
        WeatherSpatialHoverHelpRuntime.Disable();
        WeatherSpatialSelectionUiCleanup.Disable();
        WeatherSpatialMapMenuRuntime.Disable();
        WeatherSpatialDevUI.Disable();
        WeatherSpatialPreview.Clear();
        _queryRoom = null;
        _enabled = false;
    }

    internal static float ApplyIntensity(
        World world,
        WeatherScheduleEventKind kind,
        string weatherId,
        float intensity)
    {
        if (intensity <= 0f || world == null || string.IsNullOrWhiteSpace(weatherId))
        {
            return intensity <= 0f ? 0f : intensity;
        }

        Room room = _queryRoom;
        if (room == null || room.world != world || room.abstractRoom == null)
        {
            // World-level systems retain the regional schedule. Per-room consumers are
            // filtered while Room.Update / RoomCamera.Update establishes exact context.
            // Scheduled LightRain is special: GlobalRain reads it outside Room.Update
            // and later feeds camera[0].roomSettings, so single-camera games can safely
            // resolve that exact room here.
            if (kind == WeatherScheduleEventKind.Weather &&
                WeatherSpatialCatalog.NormalizeId(weatherId) == "LIGHTRAIN" &&
                world.game?.cameras != null &&
                world.game.cameras.Length == 1 &&
                world.game.cameras[0]?.room?.world == world &&
                world.game.cameras[0].room.abstractRoom != null)
            {
                Room cameraRoom = world.game.cameras[0].room;
                return WeatherSpatialRegistry.IsAllowed(cameraRoom, kind, weatherId)
                    ? intensity
                    : 0f;
            }
            return intensity;
        }

        return WeatherSpatialRegistry.IsAllowed(room, kind, weatherId)
            ? intensity
            : 0f;
    }

    private static void Room_Update(On.Room.orig_Update orig, Room self)
    {
        Room previous = _queryRoom;
        _queryRoom = self;
        try
        {
            WeatherSpatialRegistry.PollHotReload(Time.frameCount);
            orig(self);
        }
        finally
        {
            _queryRoom = previous;
        }
    }

    private static void RoomCamera_Update(On.RoomCamera.orig_Update orig, RoomCamera self)
    {
        Room previous = _queryRoom;
        _queryRoom = self?.room;
        try
        {
            orig(self);
        }
        finally
        {
            _queryRoom = previous;
        }
    }
}

internal static class WeatherSpatialPreview
{
    private static WeakReference _world;
    private static WeatherScheduleEventKind _kind;
    private static string _weatherId;
    private static float _intensity;
    private static string _targetKey;

    internal static bool Active =>
        _world != null &&
        _world.IsAlive &&
        _world.Target is World &&
        !string.IsNullOrEmpty(_weatherId) &&
        _intensity > 0f;

    internal static string WeatherId => _weatherId;
    internal static WeatherScheduleEventKind Kind => _kind;
    internal static float Intensity => _intensity;
    internal static string TargetKey => _targetKey;

    internal static bool IsActiveFor(World world)
    {
        return Active &&
               world != null &&
               ReferenceEquals(_world.Target, world);
    }

    internal static void Set(
        World world,
        WeatherScheduleEventKind kind,
        string weatherId,
        float intensity)
    {
        if (world == null || string.IsNullOrWhiteSpace(weatherId) || intensity <= 0f)
        {
            Clear();
            return;
        }

        bool sameWorld = Active && ReferenceEquals(_world.Target, world);
        if (!sameWorld)
        {
            _targetKey = null;
        }

        _world = new WeakReference(world);
        _kind = kind;
        _weatherId = WeatherSpatialCatalog.CanonicalWeatherId(kind, weatherId);
        _intensity = Mathf.Clamp01(intensity);
    }

    internal static void SetEditorTargetKey(string targetKey)
    {
        if (!Active)
        {
            return;
        }

        _targetKey = string.IsNullOrWhiteSpace(targetKey)
            ? null
            : targetKey.Trim();
    }

    internal static void Clear([CallerMemberName] string caller = null)
    {
        // Closing/toggling DevUI destroys its visual nodes and can switch away from the
        // Map page. Those are UI lifecycle events, not an explicit request to stop the
        // weather test. Keep Preview alive so H can be closed while testing in-game.
        if (string.Equals(caller, "ClearSprites", StringComparison.Ordinal) ||
            string.Equals(caller, "DevUI_SwitchPage", StringComparison.Ordinal))
        {
            return;
        }

        _world = null;
        _weatherId = null;
        _intensity = 0f;
        _targetKey = null;
    }

    internal static bool TryGetIntensity(
        World world,
        WeatherScheduleEventKind kind,
        string[] ids,
        out float intensity,
        out string matchedId)
    {
        intensity = 0f;
        matchedId = null;
        if (!Active || world == null || kind != _kind || ids == null || ids.Length == 0)
        {
            return false;
        }

        if (!ReferenceEquals(_world.Target, world))
        {
            return false;
        }

        string previewNormalized = WeatherSpatialCatalog.NormalizeId(_weatherId);
        for (int i = 0; i < ids.Length; i++)
        {
            if (WeatherSpatialCatalog.NormalizeId(ids[i]) == previewNormalized)
            {
                intensity = _intensity;
                matchedId = _weatherId;
                return true;
            }
        }
        return false;
    }
}
