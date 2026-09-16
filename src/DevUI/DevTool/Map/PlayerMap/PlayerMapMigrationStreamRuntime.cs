using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using RWCustom;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

public readonly struct PlayerMapMigrationBezierSnapshot
{
    public PlayerMapMigrationBezierSnapshot(Vector2 a, Vector2 handleA, Vector2 b, Vector2 handleB)
    {
        A = a;
        HandleA = handleA;
        B = b;
        HandleB = handleB;
    }

    public Vector2 A { get; }
    public Vector2 HandleA { get; }
    public Vector2 B { get; }
    public Vector2 HandleB { get; }
}

public readonly struct PlayerMapMigrationMidpointSnapshot
{
    public PlayerMapMigrationMidpointSnapshot(Vector2 position, float perpendicularAngle, float length1, float length2)
    {
        Position = position;
        PerpendicularAngle = perpendicularAngle;
        Length1 = length1;
        Length2 = length2;
    }

    public Vector2 Position { get; }
    public float PerpendicularAngle { get; }
    public float Length1 { get; }
    public float Length2 { get; }
}

public sealed class PlayerMapMigrationStreamSnapshot
{
    public string Name { get; init; } = string.Empty;
    public Vector2 Start { get; init; }
    public Vector2 StartHandle { get; init; }
    public Vector2 End { get; init; }
    public Vector2 EndHandle { get; init; }
    public PlayerMapMigrationMidpointSnapshot[] Midpoints { get; init; } = Array.Empty<PlayerMapMigrationMidpointSnapshot>();
    public PlayerMapMigrationBezierSnapshot[] Segments { get; init; } = Array.Empty<PlayerMapMigrationBezierSnapshot>();
    public float Width { get; init; }
    public int Rate { get; init; }
    public bool[] Layers { get; init; } = new[] { true, true, true };
    public string NextStreamName { get; init; } = string.Empty;
    public string DestinationRoom { get; init; } = string.Empty;
}

public sealed class PlayerMapMigrationPresentationSnapshot
{
    public static readonly PlayerMapMigrationPresentationSnapshot Empty = new();

    public bool Available { get; init; }
    public string RegionName { get; init; } = string.Empty;
    public long Revision { get; init; }
    public PlayerMapMigrationStreamSnapshot[] Streams { get; init; } = Array.Empty<PlayerMapMigrationStreamSnapshot>();
}

public enum PlayerMapMigrationCommandKind
{
    Create,
    SetStart,
    SetStartHandle,
    SetEnd,
    SetEndHandle,
    SetWidth,
    SetRate,
    SetLayerEnabled,
    SetNextStream,
    SetDestinationRoom
}

public readonly struct PlayerMapMigrationCommand
{
    public PlayerMapMigrationCommand(
        PlayerMapMigrationCommandKind kind,
        string streamName = null,
        Vector2 vector = default,
        float number = 0f,
        int integer = 0,
        bool flag = false,
        string text = null)
    {
        Kind = kind;
        StreamName = streamName ?? string.Empty;
        Vector = vector;
        Number = number;
        Integer = integer;
        Flag = flag;
        Text = text ?? string.Empty;
    }

    public PlayerMapMigrationCommandKind Kind { get; }
    public string StreamName { get; }
    public Vector2 Vector { get; }
    public float Number { get; }
    public int Integer { get; }
    public bool Flag { get; }
    public string Text { get; }
}

public static class PlayerMapMigrationCommandQueue
{
    private static readonly ConcurrentQueue<PlayerMapMigrationCommand> Queue = new();

    public static void Enqueue(PlayerMapMigrationCommand command) => Queue.Enqueue(command);
    internal static bool TryDequeue(out PlayerMapMigrationCommand command) => Queue.TryDequeue(out command);

    internal static void Clear()
    {
        while (Queue.TryDequeue(out _)) { }
    }
}

/// <summary>
/// Rebuilt migration-stream authoring backend. It edits WorldSpawnMigrationStream data directly and
/// never constructs MapSpawnMigrationStreamControl/BezierSplineControl or advances their DevUI
/// lifecycle. PlayerMapConfigBuildPipeline already serializes these live stream objects during the
/// same atomic map-config save transaction.
/// </summary>
internal static class PlayerMapMigrationStreamRuntime
{
    private sealed class StreamState
    {
        internal string Name = string.Empty;
        internal Vector2 Start;
        internal Vector2 StartHandle;
        internal Vector2 End;
        internal Vector2 EndHandle;
        internal BezierSpline.Midpoint[] Midpoints = Array.Empty<BezierSpline.Midpoint>();
        internal float Width;
        internal int Rate;
        internal bool[] Layers = new[] { true, true, true };
        internal string NextStreamName = string.Empty;
        internal string DestinationRoom = string.Empty;
    }

    private static long revision = 1;
    private static PlayerMapMigrationPresentationSnapshot current = PlayerMapMigrationPresentationSnapshot.Empty;
    private static object observedWorld;
    private static long observedRevision = long.MinValue;
    private static int observedStreamCount = -1;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        Reset();
        logger?.LogInfo("Player Map migration-stream authoring backend enabled.");
    }

    internal static void Disable()
    {
        enabled = false;
        PlayerMapMigrationCommandQueue.Clear();
        Reset();
    }

    internal static void Reset()
    {
        revision = 1;
        current = PlayerMapMigrationPresentationSnapshot.Empty;
        observedWorld = null;
        observedRevision = long.MinValue;
        observedStreamCount = -1;
    }

    internal static PlayerMapMigrationPresentationSnapshot GetPresentation(EditorSession session)
    {
        if (!enabled || session?.Owner?.activePage is not DevInterface.MapPage page || page.world == null)
            return PlayerMapMigrationPresentationSnapshot.Empty;

        List<WorldSpawnMigrationStream> streams = page.world.voidSpawnWorldAI?.worldMigrationStreams;
        int count = streams?.Count ?? 0;
        if (ReferenceEquals(observedWorld, page.world) && observedRevision == revision && observedStreamCount == count && current.Available)
            return current;

        PlayerMapMigrationStreamSnapshot[] snapshots = new PlayerMapMigrationStreamSnapshot[count];
        for (int i = 0; i < count; i++) snapshots[i] = BuildSnapshot(streams[i]);
        Array.Sort(snapshots, (a, b) => string.Compare(a?.Name, b?.Name, StringComparison.OrdinalIgnoreCase));

        current = new PlayerMapMigrationPresentationSnapshot
        {
            Available = true,
            RegionName = page.world.name ?? string.Empty,
            Revision = revision,
            Streams = snapshots
        };
        observedWorld = page.world;
        observedRevision = revision;
        observedStreamCount = count;
        return current;
    }

    internal static void Process(EditorSession session)
    {
        if (!enabled || session?.Owner?.activePage is not DevInterface.MapPage page || page.world == null)
        {
            if (session == null) PlayerMapMigrationCommandQueue.Clear();
            return;
        }

        bool changed = false;
        while (PlayerMapMigrationCommandQueue.TryDequeue(out PlayerMapMigrationCommand command))
            changed |= Execute(session, page, command);

        if (!changed) return;
        Touch(session);
        GetPresentation(session);
    }

    private static bool Execute(EditorSession session, DevInterface.MapPage page, PlayerMapMigrationCommand command)
    {
        if (command.Kind == PlayerMapMigrationCommandKind.Create)
            return Create(session, page, command.Vector);

        WorldSpawnMigrationStream stream = Find(page.world, command.StreamName);
        if (stream?.spline == null) return false;
        StreamState before = Capture(stream);
        bool changed = command.Kind switch
        {
            PlayerMapMigrationCommandKind.SetStart => SetStart(stream, command.Vector),
            PlayerMapMigrationCommandKind.SetStartHandle => SetStartHandle(stream, command.Vector),
            PlayerMapMigrationCommandKind.SetEnd => SetEnd(stream, command.Vector),
            PlayerMapMigrationCommandKind.SetEndHandle => SetEndHandle(stream, command.Vector),
            PlayerMapMigrationCommandKind.SetWidth => SetWidth(stream, command.Number),
            PlayerMapMigrationCommandKind.SetRate => SetRate(stream, command.Integer),
            PlayerMapMigrationCommandKind.SetLayerEnabled => SetLayer(stream, command.Integer, command.Flag),
            PlayerMapMigrationCommandKind.SetNextStream => SetNextStream(page.world, stream, command.Text),
            PlayerMapMigrationCommandKind.SetDestinationRoom => SetDestination(stream, command.Text),
            _ => false
        };
        if (!changed) return false;

        InvalidateIntersectionCache(stream);
        StreamState after = Capture(stream);
        session.History?.Push(new DelegateHistoryEntry(
            Label(command.Kind),
            s => Restore(s, before),
            s => Restore(s, after)));
        return true;
    }

    private static bool Create(EditorSession session, DevInterface.MapPage page, Vector2 center)
    {
        if (page.world.voidSpawnWorldAI == null)
            page.world.voidSpawnWorldAI = new VoidSpawnWorldAI(page.world);
        List<WorldSpawnMigrationStream> streams = page.world.voidSpawnWorldAI.worldMigrationStreams;
        if (streams == null) return false;

        Vector2 start = center;
        Vector2 startHandle = center + Vector2.up * 100f;
        Vector2 end = center + (Vector2.up + Vector2.right) * 100f;
        Vector2 endHandle = center + Vector2.right * 100f;
        WorldSpawnMigrationStream created = new(
            new BezierSpline(start, startHandle, end, endHandle),
            WorldSpawnMigrationStream.NewName(page.world));
        streams.Add(created);
        StreamState state = Capture(created);
        session.History?.Push(new DelegateHistoryEntry(
            "Create player-map migration stream",
            s => RemoveByName(s, state.Name),
            s => AddFromState(s, state)));
        return true;
    }

    private static bool SetStart(WorldSpawnMigrationStream stream, Vector2 value)
    {
        if ((stream.spline.posA - value).sqrMagnitude <= 0.000001f) return false;
        Vector2 delta = value - stream.spline.posA;
        stream.spline.posA = value;
        stream.spline.handleA += delta;
        InvalidateSplineLengths(stream.spline);
        return true;
    }

    private static bool SetStartHandle(WorldSpawnMigrationStream stream, Vector2 value)
    {
        if ((stream.spline.handleA - value).sqrMagnitude <= 0.000001f) return false;
        stream.spline.handleA = value;
        InvalidateSplineLengths(stream.spline);
        return true;
    }

    private static bool SetEnd(WorldSpawnMigrationStream stream, Vector2 value)
    {
        if ((stream.spline.posB - value).sqrMagnitude <= 0.000001f) return false;
        Vector2 delta = value - stream.spline.posB;
        stream.spline.posB = value;
        stream.spline.handleB += delta;
        InvalidateSplineLengths(stream.spline);
        return true;
    }

    private static bool SetEndHandle(WorldSpawnMigrationStream stream, Vector2 value)
    {
        if ((stream.spline.handleB - value).sqrMagnitude <= 0.000001f) return false;
        stream.spline.handleB = value;
        InvalidateSplineLengths(stream.spline);
        return true;
    }

    private static bool SetWidth(WorldSpawnMigrationStream stream, float value)
    {
        value = Mathf.Clamp(value, 1f, 200f);
        if (Mathf.Abs(stream.width - value) <= 0.0001f) return false;
        stream.width = value;
        return true;
    }

    private static bool SetRate(WorldSpawnMigrationStream stream, int value)
    {
        value = Mathf.Clamp(value, 5, 200);
        if (stream.rate == value) return false;
        stream.rate = value;
        return true;
    }

    private static bool SetLayer(WorldSpawnMigrationStream stream, int layer, bool value)
    {
        layer = Mathf.Clamp(layer, 0, PlayerMapCoordinateSystem.LayerCount - 1);
        stream.layers ??= new[] { true, true, true };
        if (stream.layers.Length < PlayerMapCoordinateSystem.LayerCount)
        {
            bool[] expanded = new[] { true, true, true };
            Array.Copy(stream.layers, expanded, Math.Min(stream.layers.Length, expanded.Length));
            stream.layers = expanded;
        }
        if (stream.layers[layer] == value) return false;
        stream.layers[layer] = value;
        return true;
    }

    private static bool SetNextStream(global::World world, WorldSpawnMigrationStream stream, string value)
    {
        string next = NormalizeOptional(value);
        if (string.Equals(stream.nextStreamName ?? string.Empty, next, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(next) && string.Equals(next, stream.name, StringComparison.OrdinalIgnoreCase)) return false;
        WorldSpawnMigrationStream resolved = string.IsNullOrEmpty(next) ? null : Find(world, next);
        if (!string.IsNullOrEmpty(next) && resolved == null) return false;
        stream.nextStreamName = string.IsNullOrEmpty(next) ? null : resolved.name;
        stream.nextStream = resolved;
        return true;
    }

    private static bool SetDestination(WorldSpawnMigrationStream stream, string value)
    {
        string next = NormalizeOptional(value);
        if (string.Equals(stream.destRoom ?? string.Empty, next, StringComparison.OrdinalIgnoreCase)) return false;
        stream.destRoom = string.IsNullOrEmpty(next) ? null : next;
        return true;
    }

    private static StreamState Capture(WorldSpawnMigrationStream stream)
    {
        BezierSpline spline = stream.spline;
        BezierSpline.Midpoint[] mids = new BezierSpline.Midpoint[spline?.midpoints?.Count ?? 0];
        for (int i = 0; i < mids.Length; i++) mids[i] = spline.midpoints[i];
        return new StreamState
        {
            Name = stream.name ?? string.Empty,
            Start = spline?.posA ?? Vector2.zero,
            StartHandle = spline?.handleA ?? Vector2.zero,
            End = spline?.posB ?? Vector2.zero,
            EndHandle = spline?.handleB ?? Vector2.zero,
            Midpoints = mids,
            Width = stream.width,
            Rate = stream.rate,
            Layers = stream.layers == null ? new[] { true, true, true } : (bool[])stream.layers.Clone(),
            NextStreamName = stream.nextStreamName ?? string.Empty,
            DestinationRoom = stream.destRoom ?? string.Empty
        };
    }

    private static bool Restore(EditorSession session, StreamState state)
    {
        if (session?.Owner?.activePage is not DevInterface.MapPage page || page.world == null || state == null) return false;
        WorldSpawnMigrationStream stream = Find(page.world, state.Name);
        if (stream == null) return AddFromState(session, state);
        Apply(page.world, stream, state);
        Touch(session);
        return true;
    }

    private static bool AddFromState(EditorSession session, StreamState state)
    {
        if (session?.Owner?.activePage is not DevInterface.MapPage page || page.world == null || state == null) return false;
        if (Find(page.world, state.Name) != null) return false;
        if (page.world.voidSpawnWorldAI == null)
            page.world.voidSpawnWorldAI = new VoidSpawnWorldAI(page.world);
        WorldSpawnMigrationStream stream = CreateFromState(state);
        page.world.voidSpawnWorldAI.worldMigrationStreams.Add(stream);
        ResolveNextStream(page.world, stream);
        Touch(session);
        return true;
    }

    private static bool RemoveByName(EditorSession session, string name)
    {
        if (session?.Owner?.activePage is not DevInterface.MapPage page || page.world?.voidSpawnWorldAI?.worldMigrationStreams == null)
            return false;
        List<WorldSpawnMigrationStream> streams = page.world.voidSpawnWorldAI.worldMigrationStreams;
        for (int i = 0; i < streams.Count; i++)
        {
            if (!string.Equals(streams[i]?.name, name, StringComparison.OrdinalIgnoreCase)) continue;
            streams.RemoveAt(i);
            Touch(session);
            return true;
        }
        return false;
    }

    private static void Apply(global::World world, WorldSpawnMigrationStream stream, StreamState state)
    {
        stream.spline = CloneSpline(state);
        stream.width = state.Width;
        stream.rate = state.Rate;
        stream.layers = state.Layers == null ? new[] { true, true, true } : (bool[])state.Layers.Clone();
        stream.nextStreamName = string.IsNullOrWhiteSpace(state.NextStreamName) ? null : state.NextStreamName;
        stream.destRoom = string.IsNullOrWhiteSpace(state.DestinationRoom) ? null : state.DestinationRoom;
        ResolveNextStream(world, stream);
        InvalidateIntersectionCache(stream);
    }

    private static WorldSpawnMigrationStream CreateFromState(StreamState state)
    {
        WorldSpawnMigrationStream stream = new(CloneSpline(state), state.Name)
        {
            width = state.Width,
            rate = state.Rate,
            layers = state.Layers == null ? new[] { true, true, true } : (bool[])state.Layers.Clone(),
            nextStreamName = string.IsNullOrWhiteSpace(state.NextStreamName) ? null : state.NextStreamName,
            destRoom = string.IsNullOrWhiteSpace(state.DestinationRoom) ? null : state.DestinationRoom
        };
        InvalidateIntersectionCache(stream);
        return stream;
    }

    private static BezierSpline CloneSpline(StreamState state)
    {
        BezierSpline.Midpoint[] mids = state.Midpoints == null
            ? Array.Empty<BezierSpline.Midpoint>()
            : (BezierSpline.Midpoint[])state.Midpoints.Clone();
        return new BezierSpline(state.Start, state.StartHandle, state.End, state.EndHandle, mids);
    }

    private static PlayerMapMigrationStreamSnapshot BuildSnapshot(WorldSpawnMigrationStream stream)
    {
        if (stream?.spline == null) return new PlayerMapMigrationStreamSnapshot { Name = stream?.name ?? string.Empty };
        BezierSpline spline = stream.spline;
        PlayerMapMigrationMidpointSnapshot[] midpoints = new PlayerMapMigrationMidpointSnapshot[spline.midpoints.Count];
        for (int i = 0; i < midpoints.Length; i++)
        {
            BezierSpline.Midpoint midpoint = spline.midpoints[i];
            midpoints[i] = new PlayerMapMigrationMidpointSnapshot(
                midpoint.pos,
                midpoint.PerpendicularAngle,
                midpoint.length1,
                midpoint.length2);
        }

        PlayerMapMigrationBezierSnapshot[] segments = new PlayerMapMigrationBezierSnapshot[spline.Segments];
        for (int i = 0; i < segments.Length; i++)
        {
            BezierCurve bezier = spline.GetBezier(i);
            segments[i] = new PlayerMapMigrationBezierSnapshot(bezier.posA, bezier.handleA, bezier.posB, bezier.handleB);
        }

        bool[] layers = stream.layers == null ? new[] { true, true, true } : (bool[])stream.layers.Clone();
        if (layers.Length < PlayerMapCoordinateSystem.LayerCount)
        {
            bool[] expanded = new[] { true, true, true };
            Array.Copy(layers, expanded, Math.Min(layers.Length, expanded.Length));
            layers = expanded;
        }

        return new PlayerMapMigrationStreamSnapshot
        {
            Name = stream.name ?? string.Empty,
            Start = spline.posA,
            StartHandle = spline.handleA,
            End = spline.posB,
            EndHandle = spline.handleB,
            Midpoints = midpoints,
            Segments = segments,
            Width = stream.width,
            Rate = stream.rate,
            Layers = layers,
            NextStreamName = stream.nextStreamName ?? string.Empty,
            DestinationRoom = stream.destRoom ?? string.Empty
        };
    }

    private static WorldSpawnMigrationStream Find(global::World world, string name)
    {
        List<WorldSpawnMigrationStream> streams = world?.voidSpawnWorldAI?.worldMigrationStreams;
        if (streams == null || string.IsNullOrWhiteSpace(name)) return null;
        for (int i = 0; i < streams.Count; i++)
            if (string.Equals(streams[i]?.name, name, StringComparison.OrdinalIgnoreCase)) return streams[i];
        return null;
    }

    private static void ResolveNextStream(global::World world, WorldSpawnMigrationStream stream)
    {
        stream.nextStream = string.IsNullOrWhiteSpace(stream.nextStreamName)
            ? null
            : Find(world, stream.nextStreamName);
    }

    private static void InvalidateIntersectionCache(WorldSpawnMigrationStream stream)
    {
        stream.foundIntersectingRooms = false;
        stream.rooms?.Clear();
    }

    private static void InvalidateSplineLengths(BezierSpline spline)
    {
        if (spline == null) return;
        spline._cachedLengths = new float?[spline.Segments];
    }

    private static string NormalizeOptional(string value)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Equals("None", StringComparison.OrdinalIgnoreCase) ? string.Empty : text;
    }

    private static string Label(PlayerMapMigrationCommandKind kind) => kind switch
    {
        PlayerMapMigrationCommandKind.SetStart => "Move migration-stream start",
        PlayerMapMigrationCommandKind.SetStartHandle => "Move migration-stream start handle",
        PlayerMapMigrationCommandKind.SetEnd => "Move migration-stream end",
        PlayerMapMigrationCommandKind.SetEndHandle => "Move migration-stream end handle",
        PlayerMapMigrationCommandKind.SetWidth => "Change migration-stream width",
        PlayerMapMigrationCommandKind.SetRate => "Change migration-stream rate",
        PlayerMapMigrationCommandKind.SetLayerEnabled => "Change migration-stream layers",
        PlayerMapMigrationCommandKind.SetNextStream => "Change migration-stream next stream",
        PlayerMapMigrationCommandKind.SetDestinationRoom => "Change migration-stream warp destination",
        _ => "Edit migration stream"
    };

    private static void Touch(EditorSession session)
    {
        revision = revision >= long.MaxValue ? 1L : revision + 1L;
        observedRevision = long.MinValue;
        EditorRevisionHub.Mark(session, EditorRevisionKind.Map);
    }
}
