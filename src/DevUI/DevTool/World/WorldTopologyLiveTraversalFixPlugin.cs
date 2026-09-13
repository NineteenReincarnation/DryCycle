using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Closes the remaining gap between World Map authoring and Rain World's live shortcut runtime.
///
/// The editor already updates the lossless world.txt document and exact endpoint metadata, but a
/// running World may still hold the old AbstractRoom.connections array. Vanilla ShortcutHandler
/// consults that array before our exact-destination correction gets a chance to run; a newly linked
/// pipe can therefore look correct in the map and still strand the player inside the source pipe.
///
/// This guard synchronizes the live AbstractRoom connection whenever WorldTextRegistry mutates an
/// endpoint and, as a final safety net, repairs the source connection immediately before vanilla
/// ShortcutHandler.SuckInCreature executes. Exact destination-node selection remains owned by
/// WorldTopologyRuntime's pending-route system.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(global::DryCycle.Plugin.ModId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldTopologyLiveTraversalFixPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.WorldTopology.LiveTraversalFix";
    public const string PluginName = "DryCycle World Topology Live Traversal Fix";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() => WorldTopologyLiveTraversalFix.Enable(Logger);
    private void OnDisable() => WorldTopologyLiveTraversalFix.Disable();
}

internal static class WorldTopologyLiveTraversalFix
{
    private delegate bool OrigTrySetConnection(
        string region,
        string roomName,
        int exitIndex,
        string destinationRoom,
        out string error);

    private delegate bool HookTrySetConnection(
        OrigTrySetConnection orig,
        string region,
        string roomName,
        int exitIndex,
        string destinationRoom,
        out string error);

    private static readonly HookTrySetConnection TrySetConnectionHookDelegate = TrySetConnectionHook;

    private static ManualLogSource log;
    private static IDisposable trySetConnectionHook;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo trySetConnection = typeof(WorldTextRegistry).GetMethod(
                "TrySetConnection",
                flags,
                null,
                new[]
                {
                    typeof(string), typeof(string), typeof(int), typeof(string),
                    typeof(string).MakeByRefType()
                },
                null);

            if (trySetConnection == null)
                throw new MissingMethodException("WorldTextRegistry.TrySetConnection was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");

            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            trySetConnectionHook = constructor.Invoke(
                new object[] { trySetConnection, TrySetConnectionHookDelegate }) as IDisposable;
            if (trySetConnectionHook == null)
                throw new InvalidOperationException("Live world connection hook was not created.");

            On.ShortcutHandler.SuckInCreature += ShortcutHandler_SuckInCreature;
            enabled = true;
            log?.LogInfo("World topology live traversal synchronization enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("World topology live traversal synchronization could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        if (enabled)
            On.ShortcutHandler.SuckInCreature -= ShortcutHandler_SuckInCreature;

        try
        {
            trySetConnectionHook?.Dispose();
        }
        catch
        {
        }

        trySetConnectionHook = null;
        enabled = false;
        log = null;
    }

    private static bool TrySetConnectionHook(
        OrigTrySetConnection orig,
        string region,
        string roomName,
        int exitIndex,
        string destinationRoom,
        out string error)
    {
        bool result = orig(region, roomName, exitIndex, destinationRoom, out error);
        if (!result) return false;

        try
        {
            global::World world = ResolveLiveWorld(region);
            if (world != null)
                SynchronizeEndpoint(world, roomName, exitIndex, destinationRoom);
        }
        catch (Exception syncError)
        {
            // The editable world.txt mutation already succeeded. Do not roll it back merely because
            // a transient live World reference disappeared during a process/region transition.
            log?.LogWarning("WorldTopology live endpoint sync skipped: " + syncError.Message);
        }

        return true;
    }

    private static void ShortcutHandler_SuckInCreature(
        On.ShortcutHandler.orig_SuckInCreature orig,
        ShortcutHandler self,
        Creature creature,
        global::Room room,
        ShortcutData shortCut)
    {
        try
        {
            RepairSourceConnectionBeforeTraversal(room, shortCut);
        }
        catch (Exception error)
        {
            log?.LogWarning("WorldTopology pre-traversal repair failed: " + error.Message);
        }

        orig(self, creature, room, shortCut);
    }

    private static void RepairSourceConnectionBeforeTraversal(global::Room room, ShortcutData shortcut)
    {
        AbstractRoom sourceRoom = room?.abstractRoom;
        global::World world = room?.world;
        if (sourceRoom == null || world == null ||
            shortcut.shortCutType != ShortcutData.Type.RoomExit || shortcut.destNode < 0)
            return;

        if (!TryResolveAuthoredRoute(
                world.name,
                sourceRoom.name,
                shortcut.destNode,
                out WorldConnectionEndpoint destination,
                out bool allowsTravel) ||
            !allowsTravel)
            return;

        AbstractRoom targetRoom = world.GetAbstractRoom(destination.Room);
        if (!IsValidExit(targetRoom, destination.NodeIndex))
            return;

        SetRuntimeConnection(sourceRoom, shortcut.destNode, targetRoom.index);
    }

    private static global::World ResolveLiveWorld(string region)
    {
        if (WorldConnectionSyntax.TryGetLoadedWorld(region, out global::World loaded) && loaded != null)
            return loaded;

        global::World sessionWorld = DevToolRuntime.ActiveSession?.World;
        if (sessionWorld != null &&
            string.Equals(sessionWorld.name, NormalizeRegion(region), StringComparison.OrdinalIgnoreCase))
            return sessionWorld;

        return null;
    }

    private static void SynchronizeEndpoint(
        global::World world,
        string sourceRoomName,
        int sourceNode,
        string destinationToken)
    {
        if (world == null || sourceNode < 0 || string.IsNullOrWhiteSpace(sourceRoomName)) return;
        AbstractRoom sourceRoom = world.GetAbstractRoom(sourceRoomName);
        if (sourceRoom == null) return;

        int targetRoomIndex = -1;
        if (WorldConnectionSyntax.TryParseDestination(
                destinationToken,
                out string targetRoomName,
                out _) &&
            !string.IsNullOrWhiteSpace(targetRoomName) &&
            !string.Equals(targetRoomName, "DISCONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            AbstractRoom targetRoom = world.GetAbstractRoom(targetRoomName);
            if (targetRoom == null)
            {
                log?.LogWarning(
                    "WorldTopology live sync could not resolve target room '" + targetRoomName + "'.");
                return;
            }
            targetRoomIndex = targetRoom.index;
        }

        SetRuntimeConnection(sourceRoom, sourceNode, targetRoomIndex);
    }

    private static void SetRuntimeConnection(AbstractRoom sourceRoom, int sourceNode, int targetRoomIndex)
    {
        if (sourceRoom == null || sourceNode < 0) return;

        if (sourceRoom.connections == null || sourceNode >= sourceRoom.connections.Length)
        {
            int oldLength = sourceRoom.connections?.Length ?? 0;
            int nextLength = Math.Max(sourceNode + 1, oldLength);
            int[] next = new int[nextLength];
            for (int i = 0; i < next.Length; i++) next[i] = -1;
            if (sourceRoom.connections != null)
                Array.Copy(sourceRoom.connections, next, sourceRoom.connections.Length);
            sourceRoom.connections = next;
        }

        sourceRoom.connections[sourceNode] = targetRoomIndex;
    }

    private static bool TryResolveAuthoredRoute(
        string region,
        string sourceRoom,
        int sourceNode,
        out WorldConnectionEndpoint destination,
        out bool allowsTravel)
    {
        if (WorldConnectionSyntax.TryGetLoadedRoute(region, sourceRoom, sourceNode, out destination))
        {
            allowsTravel = true;
            return destination.IsValid;
        }

        destination = default;
        allowsTravel = false;
        WorldConnectionEndpoint source = new(sourceRoom, sourceNode);
        WorldConnectionEdge[] edges = WorldTopologyRegistry.GetRegionEdges(region);
        for (int i = 0; i < edges.Length; i++)
        {
            WorldConnectionEdge edge = edges[i];
            if (edge == null) continue;
            if (edge.A.Equals(source))
            {
                destination = edge.B;
                allowsTravel = edge.AllowsFromA;
                return destination.IsValid;
            }
            if (edge.B.Equals(source))
            {
                destination = edge.A;
                allowsTravel = edge.AllowsFromB;
                return destination.IsValid;
            }
        }

        return false;
    }

    private static bool IsValidExit(AbstractRoom room, int nodeIndex) =>
        room?.nodes != null &&
        nodeIndex >= 0 && nodeIndex < room.nodes.Length &&
        room.nodes[nodeIndex].type == AbstractRoomNode.Type.Exit;

    private static string NormalizeRegion(string region) =>
        string.IsNullOrWhiteSpace(region) ? string.Empty : region.Trim().ToUpperInvariant();

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
