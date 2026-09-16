using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Projects the latest World Layout into immutable Player Map snapshots.
///
/// Derived rooms remain defined as Convert(WorldLayoutPosition) + PlayerMapOffset; Absolute rooms
/// remain independent. This class has no Render lifecycle responsibility: stale-render cancellation
/// is owned exclusively by PlayerMapRenderRevisionGuard.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapRuntimePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapDerivedLayoutBridgePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.DerivedLayoutBridge";
    public const string PluginName = "DryCycle Player Map Derived Layout Bridge";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() => PlayerMapDerivedLayoutBridge.Enable(Logger);
    private void OnDisable() => PlayerMapDerivedLayoutBridge.Disable();
}

internal static class PlayerMapDerivedLayoutBridge
{
    private delegate PlayerMapPresentationSnapshot OrigGetPresentation(EditorSession session);
    private delegate PlayerMapPresentationSnapshot HookGetPresentation(OrigGetPresentation orig, EditorSession session);

    private static readonly HookGetPresentation GetPresentationHookDelegate = GetPresentationHook;
    private static IDisposable getPresentationHook;
    private static ManualLogSource log;
    private static bool enabled;

    private static PlayerMapRoomSnapshot[] cachedSourceRooms;
    private static EditorMapRoomSnapshot[] cachedWorldRooms;
    private static PlayerMapRoomSnapshot[] cachedProjectedRooms;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo getPresentation = typeof(PlayerMapWorkspaceRuntime).GetMethod(
                "GetPresentation",
                flags,
                null,
                new[] { typeof(EditorSession) },
                null);
            if (getPresentation == null)
                throw new MissingMemberException("Player Map derived-layout presentation target was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            getPresentationHook = constructor.Invoke(new object[] { getPresentation, GetPresentationHookDelegate }) as IDisposable;
            if (getPresentationHook == null)
                throw new InvalidOperationException("Player Map derived-layout projection hook was not created.");

            enabled = true;
            log?.LogInfo("Player Map live derived-layout projection enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map derived-layout bridge could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        Dispose(ref getPresentationHook);
        cachedSourceRooms = null;
        cachedWorldRooms = null;
        cachedProjectedRooms = null;
        enabled = false;
        log = null;
    }

    private static PlayerMapPresentationSnapshot GetPresentationHook(OrigGetPresentation orig, EditorSession session)
    {
        PlayerMapPresentationSnapshot source = orig(session);
        if (!enabled || source?.Available != true) return source;

        EditorMapPresentationSnapshot world = MapEditorPresentationHub.Current;
        if (world?.Available != true ||
            !string.Equals(source.RegionName ?? string.Empty, world.RegionName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            return source;

        PlayerMapRoomSnapshot[] projected = ProjectRooms(source.Rooms, world.Rooms);
        if (ReferenceEquals(projected, source.Rooms)) return source;

        long mapRevision = session == null ? 0L : EditorRevisionHub.Get(session, EditorRevisionKind.Map);
        long projectedRevision;
        unchecked { projectedRevision = source.Revision * 397L + mapRevision; }

        return new PlayerMapPresentationSnapshot
        {
            Available = source.Available,
            RegionName = source.RegionName,
            Dirty = source.Dirty,
            Revision = projectedRevision,
            SelectedRoomIndex = world.SelectedRoomIndex,
            Rooms = projected,
            DefaultMaterials = source.DefaultMaterials,
            RenderReport = source.RenderReport,
            Preview = source.Preview
        };
    }

    private static PlayerMapRoomSnapshot[] ProjectRooms(
        PlayerMapRoomSnapshot[] sourceRooms,
        EditorMapRoomSnapshot[] worldRooms)
    {
        sourceRooms ??= Array.Empty<PlayerMapRoomSnapshot>();
        worldRooms ??= Array.Empty<EditorMapRoomSnapshot>();
        if (ReferenceEquals(cachedSourceRooms, sourceRooms) &&
            ReferenceEquals(cachedWorldRooms, worldRooms) &&
            cachedProjectedRooms != null)
            return cachedProjectedRooms;

        Dictionary<int, EditorMapRoomSnapshot> worldByIndex = new(worldRooms.Length);
        for (int i = 0; i < worldRooms.Length; i++)
        {
            EditorMapRoomSnapshot room = worldRooms[i];
            if (room != null) worldByIndex[room.RoomIndex] = room;
        }

        PlayerMapRoomSnapshot[] result = null;
        for (int i = 0; i < sourceRooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = sourceRooms[i];
            if (room == null || !worldByIndex.TryGetValue(room.RoomIndex, out EditorMapRoomSnapshot world))
                continue;

            Vector2 worldPosition = new(world.X, world.Y);
            Vector2 derivedBase = PlayerMapCoordinateSystem.WorldLayoutToCanon(worldPosition);
            Vector2 effective = room.Mode == PlayerMapPlacementMode.Derived
                ? derivedBase + room.Offset
                : room.AbsolutePosition;
            bool changed =
                (room.WorldPosition - worldPosition).sqrMagnitude > 0.000001f ||
                (room.DerivedBasePosition - derivedBase).sqrMagnitude > 0.000001f ||
                (room.EffectivePosition - effective).sqrMagnitude > 0.000001f ||
                room.Layer != world.Layer ||
                room.Disabled != world.Disabled ||
                room.Selected != world.Selected;
            if (!changed) continue;

            result ??= (PlayerMapRoomSnapshot[])sourceRooms.Clone();
            result[i] = new PlayerMapRoomSnapshot
            {
                RoomIndex = room.RoomIndex,
                Name = room.Name,
                Mode = room.Mode,
                WorldPosition = worldPosition,
                DerivedBasePosition = derivedBase,
                Offset = room.Offset,
                AbsolutePosition = room.AbsolutePosition,
                EffectivePosition = effective,
                Layer = world.Layer,
                Disabled = world.Disabled,
                Selected = world.Selected,
                Bake = room.Bake
            };
        }

        cachedSourceRooms = sourceRooms;
        cachedWorldRooms = worldRooms;
        cachedProjectedRooms = result ?? sourceRooms;
        return cachedProjectedRooms;
    }

    private static void Dispose(ref IDisposable hook)
    {
        try { hook?.Dispose(); }
        catch { }
        hook = null;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
