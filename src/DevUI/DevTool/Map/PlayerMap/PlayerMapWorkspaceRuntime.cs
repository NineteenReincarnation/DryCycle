using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

public enum PlayerMapPlacementMode
{
    Derived = 0,
    Absolute = 1
}

public readonly struct PlayerMapDefMaterialSnapshot
{
    public PlayerMapDefMaterialSnapshot(int id, Vector2 a, Vector2 b, bool air)
    {
        Id = id;
        A = a;
        B = b;
        Air = air;
    }

    public int Id { get; }
    public Vector2 A { get; }
    public Vector2 B { get; }
    public bool Air { get; }
    public float Left => Mathf.Min(A.x, B.x);
    public float Right => Mathf.Max(A.x, B.x);
    public float Bottom => Mathf.Min(A.y, B.y);
    public float Top => Mathf.Max(A.y, B.y);
}

public sealed class PlayerMapRoomSnapshot
{
    public int RoomIndex { get; init; }
    public string Name { get; init; } = string.Empty;
    public PlayerMapPlacementMode Mode { get; init; }
    public Vector2 WorldPosition { get; init; }
    public Vector2 DerivedBasePosition { get; init; }
    public Vector2 Offset { get; init; }
    public Vector2 AbsolutePosition { get; init; }
    public Vector2 EffectivePosition { get; init; }
    public int Layer { get; init; }
    public bool Disabled { get; init; }
    public bool Selected { get; init; }
    public RoomMapBakeSnapshot Bake { get; init; } = RoomMapBakeSnapshot.Empty;
}

public sealed class PlayerMapPresentationSnapshot
{
    public static readonly PlayerMapPresentationSnapshot Empty = new();

    public bool Available { get; init; }
    public string RegionName { get; init; } = string.Empty;
    public bool Dirty { get; init; }
    public long Revision { get; init; }
    public int SelectedRoomIndex { get; init; } = -1;
    public PlayerMapRoomSnapshot[] Rooms { get; init; } = Array.Empty<PlayerMapRoomSnapshot>();
    public PlayerMapDefMaterialSnapshot[] DefaultMaterials { get; init; } = Array.Empty<PlayerMapDefMaterialSnapshot>();
    public PlayerMapRenderReport RenderReport { get; init; } = PlayerMapRenderReport.Empty;
    public PlayerMapRenderPreview Preview { get; init; } = PlayerMapRenderPreview.Empty;
}

public static class PlayerMapCoordinateSystem
{
    public const float WorldLayoutPixelsPerTile = 2f;
    public const float CanonPixelsPerTile = 3f;
    public const float WorldToCanonScale = CanonPixelsPerTile / WorldLayoutPixelsPerTile;
    public const int OutputPadding = 10;
    public const int LayerCount = 3;

    public static Vector2 WorldLayoutToCanon(Vector2 worldLayout) => worldLayout * WorldToCanonScale;
    public static Vector2 CanonToWorldLayout(Vector2 canon) => canon / WorldToCanonScale;

    internal static int CanonDeltaToOutputPixel(float value) => (int)(value / CanonPixelsPerTile);
}

/// <summary>
/// Player Map renders physical room geometry only. Off-screen dens are topology/creature-storage
/// rooms with no physical room source and therefore must never enter the room bake/render pipeline.
/// AbstractRoom.offScreenDen is authoritative; the name check is a compatibility fallback for
/// partially constructed/third-party world data.
/// </summary>
internal static class PlayerMapRoomEligibility
{
    internal static bool IsRenderable(AbstractRoom room)
    {
        if (room == null || room.offScreenDen) return false;
        return !IsOffscreenDenName(room.name);
    }

    internal static bool IsRenderableName(string roomName) => !IsOffscreenDenName(roomName);

    private static bool IsOffscreenDenName(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return false;
        string value = roomName.Trim();
        return value.Equals("OffscreenDen", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("OffscreenDen_", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class PlayerMapRoomState
{
    internal int RoomIndex;
    internal string Name = string.Empty;
    internal PlayerMapPlacementMode Mode;
    internal Vector2 Offset;
    internal Vector2 AbsolutePosition;
    internal Vector2 LastMirroredCanonical;
    internal bool HasMirror;
}

internal sealed class PlayerMapDefMaterialState
{
    internal int Id;
    internal Vector2 A;
    internal Vector2 B;
    internal Vector2 PanelPosition;
    internal bool Air;

    internal PlayerMapDefMaterialState Clone() => new()
    {
        Id = Id,
        A = A,
        B = B,
        PanelPosition = PanelPosition,
        Air = Air
    };
}

internal sealed class PlayerMapSessionState
{
    internal MapPage Page;
    internal string Region = string.Empty;
    internal readonly Dictionary<int, PlayerMapRoomState> Rooms = new();
    internal readonly List<PlayerMapDefMaterialState> DefaultMaterials = new();
    internal int NextDefMaterialId = 1;
    internal long Revision = 1;
    internal bool Dirty;
    internal int ObservedBakeRevision = -1;
    internal int ObservedSelectedRoom = int.MinValue;
    internal bool Initialized;
    internal PlayerMapPresentationSnapshot Presentation = PlayerMapPresentationSnapshot.Empty;
    internal PlayerMapRenderReport RenderReport = PlayerMapRenderReport.Empty;
    internal PlayerMapRenderPreview Preview = PlayerMapRenderPreview.Empty;
}

public enum PlayerMapCommandKind
{
    SetEffectivePosition,
    SetOffset,
    SetPlacementMode,
    ResetOffset,
    SetLayer,
    CreateDefaultMaterial,
    SetDefaultMaterialRect,
    SetDefaultMaterialAir,
    DeleteDefaultMaterial,
    BuildPreview,
    RenderAndExport
}

public readonly struct PlayerMapCommand
{
    public PlayerMapCommand(
        PlayerMapCommandKind kind,
        int roomIndex = -1,
        Vector2 value = default,
        Vector2 valueB = default,
        int integer = 0,
        bool flag = false)
    {
        Kind = kind;
        RoomIndex = roomIndex;
        Value = value;
        ValueB = valueB;
        Integer = integer;
        Flag = flag;
    }

    public PlayerMapCommandKind Kind { get; }
    public int RoomIndex { get; }
    public Vector2 Value { get; }
    public Vector2 ValueB { get; }
    public int Integer { get; }
    public bool Flag { get; }
}

public static class PlayerMapCommandQueue
{
    private static readonly ConcurrentQueue<PlayerMapCommand> Queue = new();

    public static void Enqueue(PlayerMapCommand command) => Queue.Enqueue(command);

    public static void Process(EditorSession session)
    {
        if (session == null)
        {
            Clear();
            return;
        }

        PlayerMapWorkspaceRuntime.Synchronize(session);
        while (Queue.TryDequeue(out PlayerMapCommand command))
        {
            try
            {
                PlayerMapWorkspaceRuntime.Execute(session, command);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("Player Map command failed: " + error);
            }
        }
        PlayerMapWorkspaceRuntime.Synchronize(session);
    }

    public static void Clear()
    {
        while (Queue.TryDequeue(out _)) { }
    }
}

/// <summary>
/// Authoritative backend for the rebuilt Player Map / Canon View.
/// It reads MapPage/RoomPanel only as compatibility data surfaces; it never drives their Update,
/// MiniMap, RoomRepresentation texture generation, or MapRenderOutput lifecycle.
/// </summary>
internal static class PlayerMapWorkspaceRuntime
{
    private static ConditionalWeakTable<EditorSession, PlayerMapSessionState> states = new();
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;
        On.DevInterface.MapPage.SaveMapConfig += MapPage_SaveMapConfig;
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        On.DevInterface.MapPage.SaveMapConfig -= MapPage_SaveMapConfig;
        states = new ConditionalWeakTable<EditorSession, PlayerMapSessionState>();
        PlayerMapCommandQueue.Clear();
        RoomMapBakeCache.Clear();
        enabled = false;
    }

    internal static PlayerMapPresentationSnapshot GetPresentation(EditorSession session)
    {
        if (session == null) return PlayerMapPresentationSnapshot.Empty;
        PlayerMapSessionState state = states.GetValue(session, _ => new PlayerMapSessionState());
        PlayerMapPresentationSnapshot snapshot = state.Presentation;
        snapshot = PlayerMapDerivedLayoutBridge.Project(session, snapshot);
        snapshot = PlayerMapIncrementalRenderHooks.ProjectPresentation(session, snapshot);
        return snapshot;
    }

    internal static bool IsDirty(EditorSession session)
    {
        if (session == null || !states.TryGetValue(session, out PlayerMapSessionState state)) return false;
        return state.Dirty;
    }

    internal static void Synchronize(EditorSession session)
    {
        if (session?.Owner?.activePage is not MapPage page || page.world == null)
            return;

        PlayerMapSessionState state = states.GetValue(session, _ => new PlayerMapSessionState());
        bool identityChanged = !ReferenceEquals(state.Page, page) ||
                               !string.Equals(state.Region, page.world.name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        if (identityChanged)
            InitializeState(page, state);

        RoomMapBakeCache.ProcessPending(4);

        bool changed = SynchronizeRooms(page, state);
        int selectedRoom = MapEditorPresentationHub.Current?.SelectedRoomIndex ?? -1;
        int bakeRevision = RoomMapBakeCache.Revision;
        if (changed || state.ObservedBakeRevision != bakeRevision || state.ObservedSelectedRoom != selectedRoom ||
            !state.Presentation.Available)
        {
            state.ObservedBakeRevision = bakeRevision;
            state.ObservedSelectedRoom = selectedRoom;
            Publish(page, state, selectedRoom);
        }

        PlayerMapIncrementalRenderHooks.AfterSynchronize(session);
    }

    internal static void Execute(EditorSession session, PlayerMapCommand command)
    {
        if (session?.Owner?.activePage is not MapPage page || page.world == null) return;
        if (PlayerMapLayerMutationFilter.ShouldSkip(session, command)) return;
        if (PlayerMapIncrementalRenderHooks.TryHandleExecute(session, command)) return;

        PlayerMapSessionState state = states.GetValue(session, _ => new PlayerMapSessionState());
        if (!ReferenceEquals(state.Page, page)) InitializeState(page, state);

        switch (command.Kind)
        {
            case PlayerMapCommandKind.SetEffectivePosition:
                SetEffectivePosition(session, page, state, command.RoomIndex, command.Value, pushHistory: true);
                break;
            case PlayerMapCommandKind.SetOffset:
                SetOffset(session, page, state, command.RoomIndex, command.Value, pushHistory: true);
                break;
            case PlayerMapCommandKind.SetPlacementMode:
                SetPlacementMode(session, page, state, command.RoomIndex,
                    command.Integer == 0 ? PlayerMapPlacementMode.Derived : PlayerMapPlacementMode.Absolute,
                    pushHistory: true);
                break;
            case PlayerMapCommandKind.ResetOffset:
                SetOffset(session, page, state, command.RoomIndex, Vector2.zero, pushHistory: true);
                break;
            case PlayerMapCommandKind.SetLayer:
                MapEditorActions.SetRoomLayer(session, command.RoomIndex, command.Integer);
                Touch(state, dirty: true);
                break;
            case PlayerMapCommandKind.CreateDefaultMaterial:
                CreateDefaultMaterial(session, page, state, command.Value, command.ValueB, command.Flag);
                break;
            case PlayerMapCommandKind.SetDefaultMaterialRect:
                SetDefaultMaterialRect(session, state, command.Integer, command.Value, command.ValueB);
                break;
            case PlayerMapCommandKind.SetDefaultMaterialAir:
                SetDefaultMaterialAir(session, state, command.Integer, command.Flag);
                break;
            case PlayerMapCommandKind.DeleteDefaultMaterial:
                DeleteDefaultMaterial(session, state, command.Integer);
                break;
            case PlayerMapCommandKind.BuildPreview:
                Render(page, state, export: false);
                break;
            case PlayerMapCommandKind.RenderAndExport:
                Render(page, state, export: true);
                break;
        }
    }

    private static void MapPage_SaveMapConfig(On.DevInterface.MapPage.orig_SaveMapConfig orig, MapPage self)
    {
        EditorSession session = DevToolSessionHub.Current;
        if (self == null || session == null || !ReferenceEquals(session.Owner?.activePage, self) || EditorUiModeState.UseVanilla)
        {
            orig(self);
            return;
        }

        PlayerMapSessionState state = states.GetValue(session, _ => new PlayerMapSessionState());
        if (!ReferenceEquals(state.Page, self)) InitializeState(self, state);
        if (!PlayerMapConfigSerializer.Save(self, state, out string error))
        {
            state.RenderReport = PlayerMapRenderReport.Failure("Map config save failed", error);
            Touch(state, dirty: true);
            Plugin.Logger?.LogWarning("Player Map config save failed: " + error);
            return;
        }

        state.Dirty = false;
        Touch(state, dirty: false);
    }

    private static void InitializeState(MapPage page, PlayerMapSessionState state)
    {
        state.Page = page;
        state.Region = page.world?.name ?? string.Empty;
        state.Rooms.Clear();
        state.DefaultMaterials.Clear();
        state.NextDefMaterialId = 1;
        state.Revision = state.Revision >= long.MaxValue ? 1L : state.Revision + 1L;
        state.Dirty = false;
        state.RenderReport = PlayerMapRenderReport.Empty;
        state.Preview = PlayerMapRenderPreview.Empty;
        state.ObservedBakeRevision = -1;
        state.ObservedSelectedRoom = int.MinValue;
        state.Initialized = true;

        LoadDefaultMaterials(page, state);
        SynchronizeRooms(page, state);
        Publish(page, state, MapEditorPresentationHub.Current?.SelectedRoomIndex ?? -1);
        PlayerMapPlacementBootstrap.AfterInitialize(page, state);
    }

    private static bool SynchronizeRooms(MapPage page, PlayerMapSessionState state)
    {
        bool changed = false;
        HashSet<int> alive = new();
        if (page.subNodes == null) return false;

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
            AbstractRoom room = panel.roomRep.room;
            if (!PlayerMapRoomEligibility.IsRenderable(room)) continue;

            alive.Add(room.index);
            Vector2 derivedBase = PlayerMapCoordinateSystem.WorldLayoutToCanon(panel.devPos);

            if (!state.Rooms.TryGetValue(room.index, out PlayerMapRoomState roomState))
            {
                roomState = new PlayerMapRoomState
                {
                    RoomIndex = room.index,
                    Name = room.name ?? string.Empty,
                    Mode = PlayerMapPlacementMode.Derived,
                    Offset = panel.pos - derivedBase,
                    AbsolutePosition = panel.pos,
                    LastMirroredCanonical = panel.pos,
                    HasMirror = true
                };
                state.Rooms.Add(room.index, roomState);
                changed = true;
            }
            else
            {
                roomState.Name = room.name ?? roomState.Name;
                if (roomState.HasMirror && (panel.pos - roomState.LastMirroredCanonical).sqrMagnitude > 0.0001f)
                {
                    // A third-party/vanilla writer changed Canon Position directly. Adopt the value
                    // without destroying the derived relation selected by the new editor.
                    if (roomState.Mode == PlayerMapPlacementMode.Derived)
                        roomState.Offset = panel.pos - derivedBase;
                    else
                        roomState.AbsolutePosition = panel.pos;
                    changed = true;
                }
            }

            Vector2 effective = Effective(roomState, panel.devPos);
            if ((panel.pos - effective).sqrMagnitude > 0.000001f)
                panel.pos = effective;
            roomState.LastMirroredCanonical = effective;
            roomState.HasMirror = true;
            RoomMapBakeCache.Request(room.index, room.name ?? string.Empty);
        }

        if (state.Rooms.Count != alive.Count)
        {
            List<int> stale = new();
            foreach (int key in state.Rooms.Keys)
                if (!alive.Contains(key)) stale.Add(key);
            for (int i = 0; i < stale.Count; i++) state.Rooms.Remove(stale[i]);
            changed = stale.Count > 0;
        }

        if (changed) Touch(state, dirty: false);
        return changed;
    }

    private static void Publish(MapPage page, PlayerMapSessionState state, int selectedRoom)
    {
        HashSet<string> disabled = new(StringComparer.OrdinalIgnoreCase);
        if (page.world?.DisabledMapRooms != null)
        {
            for (int i = 0; i < page.world.DisabledMapRooms.Count; i++)
                if (!string.IsNullOrWhiteSpace(page.world.DisabledMapRooms[i]))
                    disabled.Add(page.world.DisabledMapRooms[i]);
        }

        List<PlayerMapRoomSnapshot> rooms = new(state.Rooms.Count);
        bool selectedRenderable = false;
        if (page.subNodes != null)
        {
            for (int i = 0; i < page.subNodes.Count; i++)
            {
                if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
                AbstractRoom room = panel.roomRep.room;
                if (!PlayerMapRoomEligibility.IsRenderable(room)) continue;
                if (!state.Rooms.TryGetValue(room.index, out PlayerMapRoomState roomState)) continue;
                Vector2 derivedBase = PlayerMapCoordinateSystem.WorldLayoutToCanon(panel.devPos);
                bool selected = room.index == selectedRoom;
                if (selected) selectedRenderable = true;
                rooms.Add(new PlayerMapRoomSnapshot
                {
                    RoomIndex = room.index,
                    Name = room.name ?? string.Empty,
                    Mode = roomState.Mode,
                    WorldPosition = panel.devPos,
                    DerivedBasePosition = derivedBase,
                    Offset = roomState.Offset,
                    AbsolutePosition = roomState.AbsolutePosition,
                    EffectivePosition = Effective(roomState, panel.devPos),
                    Layer = Mathf.Clamp(panel.layer, 0, 2),
                    Disabled = disabled.Contains(room.name ?? string.Empty),
                    Selected = selected,
                    Bake = RoomMapBakeCache.GetSnapshot(room.index)
                });
            }
        }
        rooms.Sort((a, b) => a.RoomIndex.CompareTo(b.RoomIndex));

        PlayerMapDefMaterialSnapshot[] defs = new PlayerMapDefMaterialSnapshot[state.DefaultMaterials.Count];
        for (int i = 0; i < state.DefaultMaterials.Count; i++)
        {
            PlayerMapDefMaterialState item = state.DefaultMaterials[i];
            defs[i] = new PlayerMapDefMaterialSnapshot(item.Id, item.A, item.B, item.Air);
        }

        state.Presentation = new PlayerMapPresentationSnapshot
        {
            Available = true,
            RegionName = state.Region,
            Dirty = state.Dirty,
            Revision = state.Revision,
            SelectedRoomIndex = selectedRenderable ? selectedRoom : -1,
            Rooms = rooms.ToArray(),
            DefaultMaterials = defs,
            RenderReport = state.RenderReport,
            Preview = state.Preview
        };
    }

    private readonly struct PlacementValue
    {
        internal PlacementValue(PlayerMapPlacementMode mode, Vector2 offset, Vector2 absolute)
        {
            Mode = mode;
            Offset = offset;
            Absolute = absolute;
        }

        internal PlayerMapPlacementMode Mode { get; }
        internal Vector2 Offset { get; }
        internal Vector2 Absolute { get; }
    }

    private static bool SetEffectivePosition(
        EditorSession session,
        MapPage page,
        PlayerMapSessionState state,
        int roomIndex,
        Vector2 effective,
        bool pushHistory)
    {
        RoomPanel panel = FindRoomPanel(page, roomIndex);
        if (panel == null || !state.Rooms.TryGetValue(roomIndex, out PlayerMapRoomState roomState)) return false;
        PlacementValue before = Capture(roomState);
        if (roomState.Mode == PlayerMapPlacementMode.Derived)
            roomState.Offset = effective - PlayerMapCoordinateSystem.WorldLayoutToCanon(panel.devPos);
        else
            roomState.AbsolutePosition = effective;
        return FinishPlacementMutation(session, panel, state, roomState, before, "Move player-map room", pushHistory);
    }

    private static bool SetOffset(
        EditorSession session,
        MapPage page,
        PlayerMapSessionState state,
        int roomIndex,
        Vector2 offset,
        bool pushHistory)
    {
        RoomPanel panel = FindRoomPanel(page, roomIndex);
        if (panel == null || !state.Rooms.TryGetValue(roomIndex, out PlayerMapRoomState roomState)) return false;
        PlacementValue before = Capture(roomState);
        Vector2 current = Effective(roomState, panel.devPos);
        roomState.Mode = PlayerMapPlacementMode.Derived;
        roomState.Offset = offset;
        roomState.AbsolutePosition = current;
        return FinishPlacementMutation(session, panel, state, roomState, before, "Adjust player-map offset", pushHistory);
    }

    private static bool SetPlacementMode(
        EditorSession session,
        MapPage page,
        PlayerMapSessionState state,
        int roomIndex,
        PlayerMapPlacementMode mode,
        bool pushHistory)
    {
        RoomPanel panel = FindRoomPanel(page, roomIndex);
        if (panel == null || !state.Rooms.TryGetValue(roomIndex, out PlayerMapRoomState roomState) || roomState.Mode == mode)
            return false;
        PlacementValue before = Capture(roomState);
        Vector2 current = Effective(roomState, panel.devPos);
        if (mode == PlayerMapPlacementMode.Absolute)
            roomState.AbsolutePosition = current;
        else
            roomState.Offset = current - PlayerMapCoordinateSystem.WorldLayoutToCanon(panel.devPos);
        roomState.Mode = mode;
        return FinishPlacementMutation(session, panel, state, roomState, before, "Change player-map placement mode", pushHistory);
    }

    private static bool FinishPlacementMutation(
        EditorSession session,
        RoomPanel panel,
        PlayerMapSessionState state,
        PlayerMapRoomState roomState,
        PlacementValue before,
        string label,
        bool pushHistory)
    {
        PlacementValue after = Capture(roomState);
        if (Same(before, after)) return false;
        Mirror(panel, roomState);
        Touch(state, dirty: true);
        if (pushHistory && session?.History != null)
        {
            int roomIndex = roomState.RoomIndex;
            session.History.Push(new DelegateHistoryEntry(
                label,
                s => RestorePlacement(s, roomIndex, before),
                s => RestorePlacement(s, roomIndex, after)));
        }
        return true;
    }

    private static bool RestorePlacement(EditorSession session, int roomIndex, PlacementValue value)
    {
        if (session?.Owner?.activePage is not MapPage page) return false;
        PlayerMapSessionState state = states.GetValue(session, _ => new PlayerMapSessionState());
        if (!ReferenceEquals(state.Page, page)) InitializeState(page, state);
        RoomPanel panel = FindRoomPanel(page, roomIndex);
        if (panel == null || !state.Rooms.TryGetValue(roomIndex, out PlayerMapRoomState roomState)) return false;
        roomState.Mode = value.Mode;
        roomState.Offset = value.Offset;
        roomState.AbsolutePosition = value.Absolute;
        Mirror(panel, roomState);
        Touch(state, dirty: true);
        return true;
    }

    private static PlacementValue Capture(PlayerMapRoomState state) =>
        new(state.Mode, state.Offset, state.AbsolutePosition);

    private static bool Same(PlacementValue a, PlacementValue b) =>
        a.Mode == b.Mode &&
        (a.Offset - b.Offset).sqrMagnitude <= 0.000001f &&
        (a.Absolute - b.Absolute).sqrMagnitude <= 0.000001f;

    private static void Mirror(RoomPanel panel, PlayerMapRoomState roomState)
    {
        Vector2 effective = Effective(roomState, panel.devPos);
        panel.pos = effective;
        roomState.LastMirroredCanonical = effective;
        roomState.HasMirror = true;
    }

    private static Vector2 Effective(PlayerMapRoomState state, Vector2 worldPosition) =>
        state.Mode == PlayerMapPlacementMode.Absolute
            ? state.AbsolutePosition
            : PlayerMapCoordinateSystem.WorldLayoutToCanon(worldPosition) + state.Offset;

    private static void CreateDefaultMaterial(
        EditorSession session,
        MapPage page,
        PlayerMapSessionState state,
        Vector2 a,
        Vector2 b,
        bool air)
    {
        if ((a - b).sqrMagnitude < 0.001f)
        {
            a -= new Vector2(45f, 30f);
            b += new Vector2(45f, 30f);
        }
        PlayerMapDefMaterialState created = new()
        {
            Id = state.NextDefMaterialId++,
            A = a,
            B = b,
            PanelPosition = a + new Vector2(-10f, -60f),
            Air = air
        };
        state.DefaultMaterials.Add(created);
        Touch(state, dirty: true);
        int id = created.Id;
        session?.History.Push(new DelegateHistoryEntry(
            "Create default material region",
            s => RemoveDefNoHistory(s, id),
            s => AddDefNoHistory(s, created.Clone())));
    }

    private static void SetDefaultMaterialRect(
        EditorSession session,
        PlayerMapSessionState state,
        int id,
        Vector2 a,
        Vector2 b)
    {
        PlayerMapDefMaterialState item = FindDef(state, id);
        if (item == null || ((item.A - a).sqrMagnitude <= 0.000001f && (item.B - b).sqrMagnitude <= 0.000001f)) return;
        Vector2 beforeA = item.A;
        Vector2 beforeB = item.B;
        item.A = a;
        item.B = b;
        Touch(state, dirty: true);
        session?.History.Push(new DelegateHistoryEntry(
            "Resize default material region",
            s => RestoreDefRect(s, id, beforeA, beforeB),
            s => RestoreDefRect(s, id, a, b)));
    }

    private static void SetDefaultMaterialAir(EditorSession session, PlayerMapSessionState state, int id, bool air)
    {
        PlayerMapDefMaterialState item = FindDef(state, id);
        if (item == null || item.Air == air) return;
        bool before = item.Air;
        item.Air = air;
        Touch(state, dirty: true);
        session?.History.Push(new DelegateHistoryEntry(
            "Change default material region",
            s => RestoreDefAir(s, id, before),
            s => RestoreDefAir(s, id, air)));
    }

    private static void DeleteDefaultMaterial(EditorSession session, PlayerMapSessionState state, int id)
    {
        int index = state.DefaultMaterials.FindIndex(x => x.Id == id);
        if (index < 0) return;
        PlayerMapDefMaterialState removed = state.DefaultMaterials[index].Clone();
        state.DefaultMaterials.RemoveAt(index);
        Touch(state, dirty: true);
        session?.History.Push(new DelegateHistoryEntry(
            "Delete default material region",
            s => AddDefNoHistory(s, removed.Clone()),
            s => RemoveDefNoHistory(s, id)));
    }

    private static bool RestoreDefRect(EditorSession session, int id, Vector2 a, Vector2 b)
    {
        PlayerMapSessionState state = GetLiveState(session);
        PlayerMapDefMaterialState item = FindDef(state, id);
        if (item == null) return false;
        item.A = a;
        item.B = b;
        Touch(state, dirty: true);
        return true;
    }

    private static bool RestoreDefAir(EditorSession session, int id, bool air)
    {
        PlayerMapSessionState state = GetLiveState(session);
        PlayerMapDefMaterialState item = FindDef(state, id);
        if (item == null) return false;
        item.Air = air;
        Touch(state, dirty: true);
        return true;
    }

    private static bool AddDefNoHistory(EditorSession session, PlayerMapDefMaterialState item)
    {
        PlayerMapSessionState state = GetLiveState(session);
        if (state == null || item == null || FindDef(state, item.Id) != null) return false;
        state.DefaultMaterials.Add(item);
        state.NextDefMaterialId = Math.Max(state.NextDefMaterialId, item.Id + 1);
        Touch(state, dirty: true);
        return true;
    }

    private static bool RemoveDefNoHistory(EditorSession session, int id)
    {
        PlayerMapSessionState state = GetLiveState(session);
        if (state == null) return false;
        int index = state.DefaultMaterials.FindIndex(x => x.Id == id);
        if (index < 0) return false;
        state.DefaultMaterials.RemoveAt(index);
        Touch(state, dirty: true);
        return true;
    }

    private static PlayerMapSessionState GetLiveState(EditorSession session)
    {
        if (session?.Owner?.activePage is not MapPage page) return null;
        PlayerMapSessionState state = states.GetValue(session, _ => new PlayerMapSessionState());
        if (!ReferenceEquals(state.Page, page)) InitializeState(page, state);
        return state;
    }

    private static PlayerMapDefMaterialState FindDef(PlayerMapSessionState state, int id) =>
        state?.DefaultMaterials.Find(x => x.Id == id);

    private static void Render(MapPage page, PlayerMapSessionState state, bool export)
    {
        PlayerMapRenderOutcome outcome = PlayerMapRenderPipeline.Build(page, state, export);
        state.RenderReport = outcome.Report;
        state.Preview = outcome.Preview;
        Touch(state, dirty: false);
    }

    private static RoomPanel FindRoomPanel(MapPage page, int roomIndex)
    {
        if (page?.subNodes == null) return null;
        for (int i = 0; i < page.subNodes.Count; i++)
            if (page.subNodes[i] is RoomPanel panel && panel.roomRep?.room?.index == roomIndex)
                return panel;
        return null;
    }

    internal static bool TryGetRoomState(MapPage page, PlayerMapSessionState state, int roomIndex,
        out PlayerMapRoomState roomState, out RoomPanel panel)
    {
        roomState = null;
        panel = FindRoomPanel(page, roomIndex);
        return panel != null && state.Rooms.TryGetValue(roomIndex, out roomState);
    }

    internal static Vector2 Effective(PlayerMapRoomState state, RoomPanel panel) =>
        Effective(state, panel.devPos);

    private static void Touch(PlayerMapSessionState state, bool dirty)
    {
        if (state == null) return;
        state.Revision = state.Revision >= long.MaxValue ? 1L : state.Revision + 1L;
        if (dirty) state.Dirty = true;
        state.ObservedBakeRevision = -1;
    }

    private static void LoadDefaultMaterials(MapPage page, PlayerMapSessionState state)
    {
        string path = page?.filePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return; }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i]?.Trim();
            if (string.IsNullOrEmpty(line) || !line.StartsWith("Def_Mat:", StringComparison.OrdinalIgnoreCase)) continue;
            string payload = line.Substring(line.IndexOf(':') + 1).Trim();
            string[] parts = payload.Split(',');
            if (parts.Length < 7) continue;
            if (!TryFloat(parts[0], out float ax) || !TryFloat(parts[1], out float ay) ||
                !TryFloat(parts[2], out float bx) || !TryFloat(parts[3], out float by) ||
                !TryFloat(parts[4], out float px) || !TryFloat(parts[5], out float py) ||
                !int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int air))
                continue;
            state.DefaultMaterials.Add(new PlayerMapDefMaterialState
            {
                Id = state.NextDefMaterialId++,
                A = new Vector2(ax, ay),
                B = new Vector2(bx, by),
                PanelPosition = new Vector2(px, py),
                Air = air == 1
            });
        }
    }

    private static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

internal static class PlayerMapConfigSerializer
{
    internal static bool Save(MapPage page, PlayerMapSessionState state, out string error)
    {
        error = null;
        if (page?.world == null || state == null)
        {
            error = "MapPage or Player Map state is unavailable.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(page.filePath))
        {
            error = "MapPage.filePath is empty.";
            return false;
        }

        try
        {
            Dictionary<string, string> roomLines = BuildRoomLines(page, state);
            List<string> source = File.Exists(page.filePath)
                ? new List<string>(File.ReadAllLines(page.filePath))
                : new List<string>();
            List<string> output = new(source.Count + roomLines.Count + state.DefaultMaterials.Count);
            HashSet<string> writtenRooms = new(StringComparer.OrdinalIgnoreCase);
            int lastRoomOutputIndex = -1;

            for (int i = 0; i < source.Count; i++)
            {
                string line = source[i] ?? string.Empty;
                if (line.TrimStart().StartsWith("Def_Mat:", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (TryRoomRecordName(line, out string roomName) && roomLines.TryGetValue(roomName, out string replacement))
                {
                    output.Add(replacement);
                    writtenRooms.Add(roomName);
                    lastRoomOutputIndex = output.Count - 1;
                    continue;
                }

                output.Add(line);
            }

            List<string> missing = new();
            foreach (KeyValuePair<string, string> pair in roomLines)
                if (!writtenRooms.Contains(pair.Key)) missing.Add(pair.Value);
            missing.Sort(StringComparer.Ordinal);
            int insertion = lastRoomOutputIndex >= 0 ? lastRoomOutputIndex + 1 : 0;
            if (missing.Count > 0)
            {
                output.InsertRange(insertion, missing);
                insertion += missing.Count;
            }

            if (state.DefaultMaterials.Count > 0)
            {
                List<string> defs = new(state.DefaultMaterials.Count);
                for (int i = 0; i < state.DefaultMaterials.Count; i++)
                {
                    PlayerMapDefMaterialState item = state.DefaultMaterials[i];
                    defs.Add("Def_Mat: " + F(item.A.x) + "," + F(item.A.y) + "," +
                             F(item.B.x) + "," + F(item.B.y) + "," +
                             F(item.PanelPosition.x) + "," + F(item.PanelPosition.y) + "," +
                             (item.Air ? "1" : "0"));
                }
                output.InsertRange(insertion, defs);
            }

            AtomicWriteAllLines(page.filePath, output);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static Dictionary<string, string> BuildRoomLines(MapPage page, PlayerMapSessionState state)
    {
        Dictionary<string, string> lines = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> disabled = new(StringComparer.OrdinalIgnoreCase);
        if (page.world.DisabledMapRooms != null)
            for (int i = 0; i < page.world.DisabledMapRooms.Count; i++)
                if (!string.IsNullOrWhiteSpace(page.world.DisabledMapRooms[i])) disabled.Add(page.world.DisabledMapRooms[i]);

        if (page.subNodes == null) return lines;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
            AbstractRoom room = panel.roomRep.room;
            if (!PlayerMapRoomEligibility.IsRenderable(room)) continue;
            if (disabled.Contains(room.name ?? string.Empty)) continue;
            if (!state.Rooms.TryGetValue(room.index, out PlayerMapRoomState roomState)) continue;
            Vector2 canonical = PlayerMapWorkspaceRuntime.Effective(roomState, panel);
            string subregion = room.subregionName ?? string.Empty;
            lines[room.name] = room.name + ": " +
                               F(canonical.x) + "><" + F(canonical.y) + "><" +
                               F(panel.devPos.x) + "><" + F(panel.devPos.y) + "><" +
                               Mathf.Clamp(panel.layer, 0, 2).ToString(CultureInfo.InvariantCulture) + "><" +
                               subregion + "><" + room.size.x.ToString(CultureInfo.InvariantCulture) + "><" +
                               room.size.y.ToString(CultureInfo.InvariantCulture);
        }
        return lines;
    }

    private static bool TryRoomRecordName(string line, out string roomName)
    {
        roomName = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        int colon = line.IndexOf(':');
        if (colon <= 0) return false;
        string key = line.Substring(0, colon).Trim();
        if (key.Equals("Def_Mat", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("SpawnMigrationStream", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("SpawnMigrationStreamMidpoint", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Room_Attr", StringComparison.OrdinalIgnoreCase))
            return false;
        roomName = key;
        return true;
    }

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static void AtomicWriteAllLines(string target, IReadOnlyList<string> lines)
    {
        string directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = target + ".drycycle.tmp";
        File.WriteAllLines(temp, ToArray(lines));
        AtomicReplaceSingle(temp, target);
    }

    internal static void AtomicReplaceSingle(string temp, string target)
    {
        string backup = target + ".drycycle.bak";
        if (File.Exists(backup)) File.Delete(backup);
        if (!File.Exists(target))
        {
            File.Move(temp, target);
            return;
        }

        File.Move(target, backup);
        try
        {
            File.Move(temp, target);
            File.Delete(backup);
        }
        catch
        {
            if (File.Exists(target)) File.Delete(target);
            if (File.Exists(backup)) File.Move(backup, target);
            throw;
        }
    }

    private static string[] ToArray(IReadOnlyList<string> lines)
    {
        string[] result = new string[lines.Count];
        for (int i = 0; i < lines.Count; i++) result[i] = lines[i] ?? string.Empty;
        return result;
    }
}
