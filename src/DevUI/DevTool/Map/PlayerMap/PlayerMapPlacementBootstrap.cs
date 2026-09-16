using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Migrates existing authored Canon positions into the new Derived+Offset model without treating
/// vanilla RoomPanel's constructor grid as authored data.
///
/// A room that has a real map_XX.txt room record preserves its historical Canon position as Offset.
/// A room with no persisted record is genuinely new and starts at Offset=0, so World Layout is its
/// immediate and only default placement source.
/// </summary>
internal static class PlayerMapPlacementBootstrap
{
    private delegate void OrigInitializeState(MapPage page, PlayerMapSessionState state);
    private delegate void HookInitializeState(OrigInitializeState orig, MapPage page, PlayerMapSessionState state);

    private static readonly HookInitializeState InitializeHookDelegate = InitializeHook;
    private static IDisposable initializeHook;
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo initialize = typeof(PlayerMapWorkspaceRuntime).GetMethod(
                "InitializeState",
                flags,
                null,
                new[] { typeof(MapPage), typeof(PlayerMapSessionState) },
                null);
            if (initialize == null)
                throw new MissingMethodException("PlayerMapWorkspaceRuntime.InitializeState was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            initializeHook = constructor.Invoke(new object[] { initialize, InitializeHookDelegate }) as IDisposable;
            if (initializeHook == null)
                throw new InvalidOperationException("Player Map placement bootstrap hook was not created.");

            enabled = true;
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map placement bootstrap could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { initializeHook?.Dispose(); }
        catch { }
        initializeHook = null;
        enabled = false;
        log = null;
    }

    private static void InitializeHook(OrigInitializeState orig, MapPage page, PlayerMapSessionState state)
    {
        orig(page, state);
        if (!enabled || page?.subNodes == null || state == null) return;

        HashSet<string> persisted = ReadPersistedRoomRecords(page);
        bool changed = false;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
            AbstractRoom room = panel.roomRep.room;
            string roomName = room.name ?? string.Empty;
            if (persisted.Contains(roomName)) continue;
            if (!state.Rooms.TryGetValue(room.index, out PlayerMapRoomState roomState)) continue;

            Vector2 derived = PlayerMapCoordinateSystem.WorldLayoutToCanon(panel.devPos);
            bool needsReset = roomState.Mode != PlayerMapPlacementMode.Derived ||
                              roomState.Offset.sqrMagnitude > 0.000001f ||
                              (roomState.AbsolutePosition - derived).sqrMagnitude > 0.000001f ||
                              (panel.pos - derived).sqrMagnitude > 0.000001f;
            if (!needsReset) continue;

            roomState.Mode = PlayerMapPlacementMode.Derived;
            roomState.Offset = Vector2.zero;
            roomState.AbsolutePosition = derived;
            roomState.LastMirroredCanonical = derived;
            roomState.HasMirror = true;
            panel.pos = derived;
            changed = true;
        }

        if (!changed) return;
        // Initialization should not dirty the document, but force the normal Synchronize tail to
        // publish a fresh snapshot after this compatibility migration.
        state.Revision = state.Revision >= long.MaxValue ? 1L : state.Revision + 1L;
        state.ObservedBakeRevision = -1;
    }

    private static HashSet<string> ReadPersistedRoomRecords(MapPage page)
    {
        HashSet<string> knownRooms = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> persisted = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is RoomPanel panel && panel.roomRep?.room != null &&
                !string.IsNullOrWhiteSpace(panel.roomRep.room.name))
                knownRooms.Add(panel.roomRep.room.name);
        }

        string path = page.filePath;
        if (knownRooms.Count == 0 || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return persisted;

        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i] ?? string.Empty;
                if (line.IndexOf("><", StringComparison.Ordinal) < 0) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line.Substring(0, colon).Trim();
                if (knownRooms.Contains(key)) persisted.Add(key);
            }
        }
        catch (Exception error)
        {
            // Fail safe for existing projects: if the config cannot be inspected, preserve every
            // loaded RoomPanel Canon position instead of risking destruction of authored offsets.
            log?.LogWarning("Player Map could not classify persisted room placements: " + error.Message);
            foreach (string room in knownRooms) persisted.Add(room);
        }
        return persisted;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
