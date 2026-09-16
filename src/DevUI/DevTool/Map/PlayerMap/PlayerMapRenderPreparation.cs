using System;
using System.Reflection;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.World;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

public sealed class PlayerMapRenderPreparationSnapshot
{
    public static readonly PlayerMapRenderPreparationSnapshot Idle = new();

    public bool Running { get; init; }
    public bool ExportRequested { get; init; }
    public bool CanCancel { get; init; }
    public int ReadyRooms { get; init; }
    public int TotalRooms { get; init; }
    public float Progress { get; init; }
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Render request gate at the scheduler boundary. A render request no longer fails merely because
/// some room/authored-terrain bakes are still progressing through their bounded cache pipeline.
/// Every caller of PlayerMapRenderScheduler.Begin (UI, shortcut, future API) therefore receives the
/// same preparation semantics without depending on command-hook ordering.
///
/// Authoring state is fingerprinted at request time. Bake readiness is allowed to advance; room
/// placement/layer/Def_Mat/topology edits cancel the request instead of silently changing the output
/// the author asked to render.
/// </summary>
internal static class PlayerMapRenderPreparationController
{
    private sealed class Request
    {
        internal EditorSession Session;
        internal MapPage Page;
        internal string Region = string.Empty;
        internal bool Export;
        internal ulong AuthoringFingerprint;
        internal int TopologyRevision;
        internal int WorldTextRevision;
    }

    private delegate bool OrigBegin(
        EditorSession session,
        MapPage page,
        PlayerMapPresentationSnapshot snapshot,
        bool export);
    private delegate bool HookBegin(
        OrigBegin orig,
        EditorSession session,
        MapPage page,
        PlayerMapPresentationSnapshot snapshot,
        bool export);
    private delegate void OrigSynchronize(EditorSession session);
    private delegate void HookSynchronize(OrigSynchronize orig, EditorSession session);

    private static readonly HookBegin BeginHookDelegate = BeginHook;
    private static readonly HookSynchronize SynchronizeHookDelegate = SynchronizeHook;

    private static IDisposable beginHook;
    private static IDisposable synchronizeHook;
    private static ManualLogSource log;
    private static bool enabled;
    private static Request active;
    private static PlayerMapRenderPreparationSnapshot progress = PlayerMapRenderPreparationSnapshot.Idle;

    internal static bool IsRunning => active != null;
    internal static PlayerMapRenderPreparationSnapshot Progress => progress;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo begin = typeof(PlayerMapRenderScheduler).GetMethod(
                "Begin",
                flags,
                null,
                new[] { typeof(EditorSession), typeof(MapPage), typeof(PlayerMapPresentationSnapshot), typeof(bool) },
                null);
            MethodInfo synchronize = typeof(PlayerMapWorkspaceRuntime).GetMethod(
                "Synchronize",
                flags,
                null,
                new[] { typeof(EditorSession) },
                null);
            if (begin == null || synchronize == null)
                throw new MissingMemberException("Player Map render-preparation targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            beginHook = constructor.Invoke(new object[] { begin, BeginHookDelegate }) as IDisposable;
            synchronizeHook = constructor.Invoke(new object[] { synchronize, SynchronizeHookDelegate }) as IDisposable;
            if (beginHook == null || synchronizeHook == null)
                throw new InvalidOperationException("Player Map render-preparation hooks were not created.");

            enabled = true;
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map render preparation could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        Dispose(ref synchronizeHook);
        Dispose(ref beginHook);
        Reset();
        enabled = false;
        log = null;
    }

    internal static void Reset()
    {
        active = null;
        progress = PlayerMapRenderPreparationSnapshot.Idle;
    }

    internal static void RequestCancel()
    {
        if (active != null)
            Cancel("Render preparation cancelled. Existing output files were preserved.");
    }

    private static bool BeginHook(
        OrigBegin orig,
        EditorSession session,
        MapPage page,
        PlayerMapPresentationSnapshot snapshot,
        bool export)
    {
        if (!enabled)
            return orig(session, page, snapshot, export);
        if (active != null || PlayerMapRenderScheduler.IsRunning)
            return false;
        if (session == null || page?.world == null || snapshot?.Available != true)
            return orig(session, page, snapshot, export);

        bool waiting = NeedsPreparation(snapshot, out int ready, out int pending, out bool hardFailure);
        if (hardFailure || !waiting)
            return orig(session, page, snapshot, export);

        active = new Request
        {
            Session = session,
            Page = page,
            Region = snapshot.RegionName ?? string.Empty,
            Export = export,
            AuthoringFingerprint = AuthoringFingerprint(snapshot),
            TopologyRevision = WorldTopologyRegistry.Revision,
            WorldTextRevision = WorldTextRegistry.Revision
        };
        Publish(ready, ready + pending,
            pending + " room/terrain bake(s) are still preparing.");
        // The request was accepted by the Render chain even though composition has not started yet.
        return true;
    }

    private static void SynchronizeHook(OrigSynchronize orig, EditorSession session)
    {
        orig(session);
        Request request = active;
        if (!enabled || request == null || !ReferenceEquals(request.Session, session))
            return;

        if (session?.Owner?.activePage is not MapPage page || !ReferenceEquals(page, request.Page) || page.world == null ||
            !string.Equals(page.world.name ?? string.Empty, request.Region, StringComparison.OrdinalIgnoreCase))
        {
            Cancel("Render preparation cancelled because the active map/region changed.");
            return;
        }
        if (WorldTopologyRegistry.Revision != request.TopologyRevision || WorldTextRegistry.Revision != request.WorldTextRevision)
        {
            Cancel("Render preparation cancelled because world connection topology changed.");
            return;
        }

        PlayerMapPresentationSnapshot snapshot = PlayerMapWorkspaceRuntime.GetPresentation(session);
        if (AuthoringFingerprint(snapshot) != request.AuthoringFingerprint)
        {
            Cancel("Render preparation cancelled because Player Map authoring data changed.");
            return;
        }

        bool waiting = NeedsPreparation(snapshot, out int ready, out int pending, out bool hardFailure);
        if (hardFailure)
        {
            Cancel("Render preparation stopped because a room bake is missing or failed.");
            return;
        }
        if (waiting)
        {
            Publish(ready, ready + pending,
                pending + " room/terrain bake(s) remaining.");
            return;
        }

        bool export = request.Export;
        active = null;
        progress = PlayerMapRenderPreparationSnapshot.Idle;
        // Re-enter the single scheduler boundary. Our Begin hook sees that all bakes are ready and
        // immediately delegates to the authoritative incremental renderer.
        if (!PlayerMapRenderScheduler.Begin(session, page, snapshot, export))
            log?.LogWarning("Player Map render preparation completed, but the render scheduler did not accept the job.");
    }

    private static bool NeedsPreparation(
        PlayerMapPresentationSnapshot snapshot,
        out int ready,
        out int pending,
        out bool hardFailure)
    {
        ready = 0;
        pending = 0;
        hardFailure = false;
        if (snapshot?.Available != true)
        {
            hardFailure = true;
            return false;
        }

        PlayerMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        int enabledRooms = 0;
        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room == null || room.Disabled) continue;
            enabledRooms++;
            RoomMapBakeStatus status = room.Bake?.Status ?? RoomMapBakeStatus.Missing;
            switch (status)
            {
                case RoomMapBakeStatus.Ready:
                    ready++;
                    break;
                case RoomMapBakeStatus.Pending:
                    pending++;
                    break;
                case RoomMapBakeStatus.Missing:
                case RoomMapBakeStatus.Failed:
                    hardFailure = true;
                    break;
            }
        }
        if (enabledRooms == 0) hardFailure = true;
        return !hardFailure && pending > 0;
    }

    private static ulong AuthoringFingerprint(PlayerMapPresentationSnapshot snapshot)
    {
        unchecked
        {
            ulong hash = 1469598103934665603UL;
            PlayerMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
            for (int i = 0; i < rooms.Length; i++)
            {
                PlayerMapRoomSnapshot room = rooms[i];
                if (room == null) continue;
                Mix(ref hash, room.RoomIndex);
                Mix(ref hash, room.EffectivePosition.x.GetHashCode());
                Mix(ref hash, room.EffectivePosition.y.GetHashCode());
                Mix(ref hash, room.Layer);
                Mix(ref hash, room.Disabled ? 1 : 0);
                Mix(ref hash, (int)room.Mode);
            }

            PlayerMapDefMaterialSnapshot[] defs = snapshot?.DefaultMaterials ?? Array.Empty<PlayerMapDefMaterialSnapshot>();
            for (int i = 0; i < defs.Length; i++)
            {
                PlayerMapDefMaterialSnapshot item = defs[i];
                Mix(ref hash, item.Id);
                Mix(ref hash, item.A.x.GetHashCode());
                Mix(ref hash, item.A.y.GetHashCode());
                Mix(ref hash, item.B.x.GetHashCode());
                Mix(ref hash, item.B.y.GetHashCode());
                Mix(ref hash, item.Air ? 1 : 0);
            }
            return hash;
        }
    }

    private static void Mix(ref ulong hash, int value)
    {
        unchecked
        {
            hash ^= (uint)value;
            hash *= 1099511628211UL;
        }
    }

    private static void Publish(int ready, int total, string detail)
    {
        progress = new PlayerMapRenderPreparationSnapshot
        {
            Running = true,
            ExportRequested = active?.Export == true,
            CanCancel = true,
            ReadyRooms = ready,
            TotalRooms = Math.Max(1, total),
            Progress = total <= 0 ? 0f : Mathf.Clamp01((float)ready / total),
            Detail = detail ?? string.Empty
        };
    }

    private static void Cancel(string detail)
    {
        bool export = active?.Export == true;
        active = null;
        progress = new PlayerMapRenderPreparationSnapshot
        {
            Running = false,
            ExportRequested = export,
            CanCancel = false,
            Progress = 0f,
            Detail = detail ?? string.Empty
        };
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
