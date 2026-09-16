using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.World;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

public sealed class PlayerMapPreflightSnapshot
{
    public static readonly PlayerMapPreflightSnapshot Empty = new();

    public bool Available { get; init; }
    public string RegionName { get; init; } = string.Empty;
    public int EnabledRooms { get; init; }
    public int ReadyRooms { get; init; }
    public int PendingRooms { get; init; }
    public int MissingRooms { get; init; }
    public int FailedRooms { get; init; }
    public int ExactConnections { get; init; }
    public int AmbiguousConnections { get; init; }
    public int InvalidEndpoints { get; init; }
    public int DuplicateEndpointClaims { get; init; }
    public int OverlapPairs { get; init; }
    public int Width { get; init; }
    public int LayerHeight { get; init; }
    public int Height { get; init; }
    public bool OutputSafe { get; init; }
    public bool CanRender { get; init; }
    public string[] Errors { get; init; } = Array.Empty<string>();
    public string[] Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Pure preflight shared by the live inspector and Render Map semantics. It consumes only rebuilt
/// immutable Player Map / exact topology snapshots; it never asks vanilla MapPage, MiniMap or
/// RoomRepresentation to update. Results are cached by presentation + topology revisions so stable
/// UI frames do not repeatedly scan every room/connection.
/// </summary>
internal static class PlayerMapPreflightDiagnostics
{
    private const int MaxDimension = 16384;
    private const long MaxPixels = 160_000_000L;

    private static string cachedRegion = string.Empty;
    private static long cachedPresentationRevision = long.MinValue;
    private static int cachedTopologyRevision = int.MinValue;
    private static int cachedWorldTextRevision = int.MinValue;
    private static PlayerMapPreflightSnapshot cached = PlayerMapPreflightSnapshot.Empty;

    internal static PlayerMapPreflightSnapshot Evaluate(
        PlayerMapPresentationSnapshot player,
        EditorMapPresentationSnapshot world)
    {
        if (player?.Available != true || world?.Available != true ||
            !string.Equals(player.RegionName ?? string.Empty, world.RegionName ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
            return PlayerMapPreflightSnapshot.Empty;

        string region = player.RegionName ?? string.Empty;
        int topologyRevision = WorldTopologyRegistry.Revision;
        int worldTextRevision = WorldTextRegistry.Revision;
        if (cachedPresentationRevision == player.Revision &&
            cachedTopologyRevision == topologyRevision &&
            cachedWorldTextRevision == worldTextRevision &&
            string.Equals(cachedRegion, region, StringComparison.OrdinalIgnoreCase))
            return cached;

        cachedRegion = region;
        cachedPresentationRevision = player.Revision;
        cachedTopologyRevision = topologyRevision;
        cachedWorldTextRevision = worldTextRevision;
        cached = Build(player, world);
        return cached;
    }

    internal static void Reset()
    {
        cachedRegion = string.Empty;
        cachedPresentationRevision = long.MinValue;
        cachedTopologyRevision = int.MinValue;
        cachedWorldTextRevision = int.MinValue;
        cached = PlayerMapPreflightSnapshot.Empty;
    }

    private static PlayerMapPreflightSnapshot Build(
        PlayerMapPresentationSnapshot player,
        EditorMapPresentationSnapshot world)
    {
        List<string> errors = new();
        List<string> warnings = new();
        Dictionary<int, PlayerMapRoomSnapshot> enabledByIndex = new();
        HashSet<string> endpointClaims = new(StringComparer.Ordinal);

        int enabledRooms = 0;
        int readyRooms = 0;
        int pendingRooms = 0;
        int missingRooms = 0;
        int failedRooms = 0;
        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;

        PlayerMapRoomSnapshot[] rooms = player.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room == null || room.Disabled) continue;
            enabledRooms++;
            enabledByIndex[room.RoomIndex] = room;

            if (!Finite(room.EffectivePosition.x) || !Finite(room.EffectivePosition.y))
            {
                errors.Add((room.Name ?? room.RoomIndex.ToString()) + ": Canon Position contains NaN/Infinity.");
                continue;
            }

            RoomMapBakeSnapshot bake = room.Bake ?? RoomMapBakeSnapshot.Empty;
            switch (bake.Status)
            {
                case RoomMapBakeStatus.Ready:
                    readyRooms++;
                    if (bake.Width <= 0 || bake.Height <= 0)
                    {
                        errors.Add(room.Name + ": ready room bake has an invalid size.");
                        break;
                    }
                    float halfW = bake.Width * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
                    float halfH = bake.Height * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
                    minX = Mathf.Min(minX, room.EffectivePosition.x - halfW);
                    minY = Mathf.Min(minY, room.EffectivePosition.y - halfH);
                    maxX = Mathf.Max(maxX, room.EffectivePosition.x + halfW);
                    maxY = Mathf.Max(maxY, room.EffectivePosition.y + halfH);
                    break;
                case RoomMapBakeStatus.Pending:
                    pendingRooms++;
                    break;
                case RoomMapBakeStatus.Missing:
                    missingRooms++;
                    errors.Add(room.Name + ": room source file is missing" +
                               (string.IsNullOrWhiteSpace(bake.Error) ? "." : " (" + bake.Error + ")"));
                    break;
                case RoomMapBakeStatus.Failed:
                    failedRooms++;
                    errors.Add(room.Name + ": room bake failed" +
                               (string.IsNullOrWhiteSpace(bake.Error) ? "." : " (" + bake.Error + ")"));
                    break;
            }
        }

        if (enabledRooms == 0)
            errors.Add("No enabled rooms are available for Player Map rendering.");

        int width = 0;
        int layerHeight = 0;
        int height = 0;
        bool outputSafe = false;
        if (readyRooms > 0 && minX != float.MaxValue)
        {
            width = (int)((maxX - minX) / PlayerMapCoordinateSystem.CanonPixelsPerTile) +
                    PlayerMapCoordinateSystem.OutputPadding * 2;
            layerHeight = (int)((maxY - minY) / PlayerMapCoordinateSystem.CanonPixelsPerTile) +
                          PlayerMapCoordinateSystem.OutputPadding * 2;
            long fullHeight = (long)layerHeight * PlayerMapCoordinateSystem.LayerCount;
            height = fullHeight > int.MaxValue ? int.MaxValue : (int)fullHeight;
            outputSafe = width > 0 && layerHeight > 0 && height > 0 &&
                         width <= MaxDimension && height <= MaxDimension &&
                         (long)width * height <= MaxPixels;
            if (!outputSafe)
                errors.Add("Output dimensions are unsafe: " + width + "x" + height + ".");
        }

        int exactConnections = 0;
        int ambiguousConnections = 0;
        int invalidEndpoints = 0;
        int duplicateClaims = 0;
        EditorMapConnectionSnapshot[] connections = world.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection == null ||
                !enabledByIndex.TryGetValue(connection.FromRoomIndex, out PlayerMapRoomSnapshot from) ||
                !enabledByIndex.TryGetValue(connection.ToRoomIndex, out PlayerMapRoomSnapshot to))
                continue;

            if (connection.Ambiguous || connection.FromNodeIndex < 0 || connection.ToNodeIndex < 0)
            {
                ambiguousConnections++;
                warnings.Add("Unresolved connection " + from.Name + ":" + connection.FromNodeIndex + " -> " +
                             to.Name + ":" + connection.ToNodeIndex + ".");
                continue;
            }

            exactConnections++;
            if (!ValidRoomExit(from, connection.FromNodeIndex))
            {
                invalidEndpoints++;
                errors.Add(from.Name + ":" + connection.FromNodeIndex + " is not a valid baked RoomExit endpoint.");
            }
            if (!ValidRoomExit(to, connection.ToNodeIndex))
            {
                invalidEndpoints++;
                errors.Add(to.Name + ":" + connection.ToNodeIndex + " is not a valid baked RoomExit endpoint.");
            }

            if (!endpointClaims.Add(connection.FromRoomIndex + ":" + connection.FromNodeIndex))
            {
                duplicateClaims++;
                warnings.Add("Multiple connections claim endpoint " + from.Name + ":" + connection.FromNodeIndex + ".");
            }
            if (!endpointClaims.Add(connection.ToRoomIndex + ":" + connection.ToNodeIndex))
            {
                duplicateClaims++;
                warnings.Add("Multiple connections claim endpoint " + to.Name + ":" + connection.ToNodeIndex + ".");
            }
        }

        int overlapPairs = 0;
        if (readyRooms > 1 && minX != float.MaxValue)
        {
            List<RectRecord> rects = new(readyRooms);
            for (int i = 0; i < rooms.Length; i++)
            {
                PlayerMapRoomSnapshot room = rooms[i];
                if (room == null || room.Disabled || room.Bake?.Status != RoomMapBakeStatus.Ready) continue;
                float left = room.EffectivePosition.x - room.Bake.Width * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
                float bottom = room.EffectivePosition.y - room.Bake.Height * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
                int x = (int)((left - minX) / PlayerMapCoordinateSystem.CanonPixelsPerTile) + PlayerMapCoordinateSystem.OutputPadding;
                int y = (int)((bottom - minY) / PlayerMapCoordinateSystem.CanonPixelsPerTile) + PlayerMapCoordinateSystem.OutputPadding;
                rects.Add(new RectRecord(room, x, y, room.Bake.Width, room.Bake.Height));
            }

            for (int i = 0; i < rects.Count; i++)
            {
                RectRecord a = rects[i];
                int ax2 = a.X + a.Width;
                int ay2 = a.Y + a.Height;
                for (int j = i + 1; j < rects.Count; j++)
                {
                    RectRecord b = rects[j];
                    if (a.Room.Layer != b.Room.Layer) continue;
                    int bx2 = b.X + b.Width;
                    int by2 = b.Y + b.Height;
                    if (a.X >= bx2 || b.X >= ax2 || a.Y >= by2 || b.Y >= ay2) continue;
                    overlapPairs++;
                    warnings.Add("Overlap on L" + a.Room.Layer + ": " + a.Room.Name + " / " + b.Room.Name +
                                 ". Deterministic RoomIndex order will be used.");
                }
            }
        }

        if (pendingRooms > 0)
            warnings.Add(pendingRooms + " room/terrain bake(s) are still pending; a Render request will wait for them.");

        bool canRender = errors.Count == 0 && missingRooms == 0 && failedRooms == 0 && pendingRooms == 0 &&
                         enabledRooms > 0 && readyRooms == enabledRooms && outputSafe;
        return new PlayerMapPreflightSnapshot
        {
            Available = true,
            RegionName = player.RegionName ?? string.Empty,
            EnabledRooms = enabledRooms,
            ReadyRooms = readyRooms,
            PendingRooms = pendingRooms,
            MissingRooms = missingRooms,
            FailedRooms = failedRooms,
            ExactConnections = exactConnections,
            AmbiguousConnections = ambiguousConnections,
            InvalidEndpoints = invalidEndpoints,
            DuplicateEndpointClaims = duplicateClaims,
            OverlapPairs = overlapPairs,
            Width = width,
            LayerHeight = layerHeight,
            Height = height,
            OutputSafe = outputSafe,
            CanRender = canRender,
            Errors = errors.ToArray(),
            Warnings = warnings.ToArray()
        };
    }

    private static bool ValidRoomExit(PlayerMapRoomSnapshot room, int nodeIndex)
    {
        RoomMapBakeSnapshot bake = room?.Bake;
        return bake?.Status == RoomMapBakeStatus.Ready &&
               bake.TryGetNodeAnchor(nodeIndex, out RoomMapNodeAnchorSnapshot anchor) &&
               anchor.Kind == RoomMapPixelKind.RoomExit;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private readonly struct RectRecord
    {
        internal RectRecord(PlayerMapRoomSnapshot room, int x, int y, int width, int height)
        {
            Room = room;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        internal PlayerMapRoomSnapshot Room { get; }
        internal int X { get; }
        internal int Y { get; }
        internal int Width { get; }
        internal int Height { get; }
    }
}
