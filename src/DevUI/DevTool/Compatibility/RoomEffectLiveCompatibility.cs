using System;
using System.Reflection;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Repairs the small lifecycle gap between the retained vanilla DevInterface page and the
/// replacement DevTool frontend.
///
/// A number of third-party RoomEffects are not purely data-driven: their runtime controller is
/// created from Room.Loaded and their camera state is initialized from RoomCamera.ApplyPalette.
/// Adding/changing an effect in an already-realized room therefore needs those two live pieces to
/// be reconciled as well. Vanilla DevInterface normally gets this behavior from effect-specific UI
/// hooks or from re-entering the room; the replacement frontend edits the same RoomSettings object
/// without reloading the room.
///
/// RegionKit DenseFog is the important compatibility case here. RegionKit creates
/// RegionKit.Modules.Effects.DenseFogGradient only from its Room.Loaded hook, then configures the
/// built-in Fog fullscreen shader from its RoomCamera.ApplyPalette hook. We deliberately avoid a
/// compile-time RegionKit reference: if RegionKit is not loaded this bridge becomes a no-op apart
/// from the normal palette refresh.
/// </summary>
internal static class RoomEffectLiveCompatibility
{
    private const string DenseFogEffectType = "DenseFog";
    private const string DenseFogRuntimeTypeName = "RegionKit.Modules.Effects.DenseFogGradient";

    private static Type denseFogRuntimeType;
    private static bool denseFogCtorWarningLogged;
    private static bool denseFogSpawnWarningLogged;

    /// <summary>
    /// Reconcile live runtime state after a persistent RoomEffect edit.
    /// Call only on the game/DevUI thread.
    /// </summary>
    internal static void Reconcile(EditorSession session)
    {
        global::Room room = session?.Room;
        RoomSettings settings = session?.RoomSettings;
        if (room == null || settings?.effects == null) return;

        if (HasEffect(settings, DenseFogEffectType))
            EnsureRegionKitDenseFogRuntime(room);

        // Effect hooks are allowed to extend RoomCamera.ApplyPalette. Re-running it after a
        // persistent edit is both cheaper and substantially safer than replaying Room.Loaded.
        // It also fixes RegionKit DenseFog's fullscreen Fog shader immediately when its amount is
        // changed from 0 to a visible value, and clears that shader when the effect is removed.
        RefreshRoomPalettes(room);
    }

    private static bool HasEffect(RoomSettings settings, string typeName)
    {
        if (settings?.effects == null || string.IsNullOrEmpty(typeName)) return false;
        for (int i = 0; i < settings.effects.Count; i++)
        {
            RoomSettings.RoomEffect effect = settings.effects[i];
            if (effect?.type != null &&
                string.Equals(effect.type.value, typeName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void EnsureRegionKitDenseFogRuntime(global::Room room)
    {
        Type runtimeType = ResolveDenseFogRuntimeType();
        if (runtimeType == null || !typeof(UpdatableAndDeletable).IsAssignableFrom(runtimeType))
            return;

        // A room loaded with DenseFog already owns this controller through RegionKit's Room.Loaded
        // hook. Never create a duplicate. A controller created by us also remains harmless at amount
        // 0 after live removal, mirroring RegionKit's room-lifetime controller model, so re-adding
        // the effect simply reuses it.
        if (HasLiveRuntimeObject(room, runtimeType)) return;

        ConstructorInfo constructor;
        try
        {
            constructor = runtimeType.GetConstructor(new[] { typeof(global::Room) });
        }
        catch
        {
            constructor = null;
        }

        if (constructor == null)
        {
            if (!denseFogCtorWarningLogged)
            {
                denseFogCtorWarningLogged = true;
                Plugin.Logger?.LogWarning(
                    "DevTool RegionKit DenseFog compatibility could not find DenseFogGradient(Room). " +
                    "DenseFog will still receive palette refreshes, but its load-time gradient controller " +
                    "cannot be materialized live.");
            }
            return;
        }

        try
        {
            if (constructor.Invoke(new object[] { room }) is not UpdatableAndDeletable runtime)
                return;
            room.AddObject(runtime);
            Plugin.Logger?.LogDebug(
                "DevTool materialized RegionKit DenseFogGradient for a DenseFog effect added to an already-loaded room.");
        }
        catch (Exception error)
        {
            if (!denseFogSpawnWarningLogged)
            {
                denseFogSpawnWarningLogged = true;
                Plugin.Logger?.LogWarning(
                    "DevTool could not materialize RegionKit DenseFogGradient for the live room: " + error.Message);
            }
        }
    }

    private static bool HasLiveRuntimeObject(global::Room room, Type runtimeType)
    {
        if (room?.updateList == null || runtimeType == null) return false;
        for (int i = 0; i < room.updateList.Count; i++)
        {
            UpdatableAndDeletable candidate = room.updateList[i];
            if (candidate == null || candidate.slatedForDeletetion) continue;
            if (runtimeType.IsInstanceOfType(candidate)) return true;
        }
        return false;
    }

    private static Type ResolveDenseFogRuntimeType()
    {
        if (denseFogRuntimeType != null) return denseFogRuntimeType;

        Assembly[] assemblies;
        try { assemblies = AppDomain.CurrentDomain.GetAssemblies(); }
        catch { return null; }

        for (int i = 0; i < assemblies.Length; i++)
        {
            Assembly assembly = assemblies[i];
            if (assembly == null) continue;
            try
            {
                Type candidate = assembly.GetType(DenseFogRuntimeTypeName, throwOnError: false, ignoreCase: false);
                if (candidate == null) continue;
                denseFogRuntimeType = candidate;
                return candidate;
            }
            catch
            {
                // Optional compatibility must never make another mod's assembly-load problem fatal.
            }
        }

        return null;
    }

    private static void RefreshRoomPalettes(global::Room room)
    {
        RoomCamera[] cameras = room?.game?.cameras;
        if (cameras == null) return;

        for (int i = 0; i < cameras.Length; i++)
        {
            RoomCamera camera = cameras[i];
            if (camera == null || !ReferenceEquals(camera.room, room)) continue;
            try
            {
                camera.ApplyPalette();
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning(
                    "DevTool RoomEffect live palette refresh failed for camera " + i + ": " + error.Message);
            }
        }
    }
}
