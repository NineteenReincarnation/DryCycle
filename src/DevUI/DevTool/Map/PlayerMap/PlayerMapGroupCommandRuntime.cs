using System;
using System.Collections.Concurrent;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

public readonly struct PlayerMapGroupMoveCommand
{
    public PlayerMapGroupMoveCommand(int[] roomIndices, Vector2[] effectivePositions, string label)
    {
        RoomIndices = roomIndices == null ? Array.Empty<int>() : (int[])roomIndices.Clone();
        EffectivePositions = effectivePositions == null ? Array.Empty<Vector2>() : (Vector2[])effectivePositions.Clone();
        Label = string.IsNullOrWhiteSpace(label) ? "Move player-map rooms" : label;
    }

    public int[] RoomIndices { get; }
    public Vector2[] EffectivePositions { get; }
    public string Label { get; }
}

public readonly struct PlayerMapGroupLayerCommand
{
    public PlayerMapGroupLayerCommand(int[] roomIndices, int layer, string label)
    {
        RoomIndices = roomIndices == null ? Array.Empty<int>() : (int[])roomIndices.Clone();
        Layer = Math.Max(0, Math.Min(PlayerMapCoordinateSystem.LayerCount - 1, layer));
        Label = string.IsNullOrWhiteSpace(label) ? "Change player-map room layers" : label;
    }

    public int[] RoomIndices { get; }
    public int Layer { get; }
    public string Label { get; }
}

public enum PlayerMapGroupPlacementOperation
{
    SetDerived = 0,
    SetAbsolute = 1,
    ResetOffset = 2
}

public readonly struct PlayerMapGroupPlacementCommand
{
    public PlayerMapGroupPlacementCommand(int[] roomIndices, PlayerMapGroupPlacementOperation operation, string label)
    {
        RoomIndices = roomIndices == null ? Array.Empty<int>() : (int[])roomIndices.Clone();
        Operation = operation;
        Label = string.IsNullOrWhiteSpace(label) ? "Change player-map room placement" : label;
    }

    public int[] RoomIndices { get; }
    public PlayerMapGroupPlacementOperation Operation { get; }
    public string Label { get; }
}

public static class PlayerMapGroupCommandQueue
{
    private static readonly ConcurrentQueue<PlayerMapGroupMoveCommand> MoveQueue = new();
    private static readonly ConcurrentQueue<PlayerMapGroupLayerCommand> LayerQueue = new();
    private static readonly ConcurrentQueue<PlayerMapGroupPlacementCommand> PlacementQueue = new();
    private static Func<PlayerMapGroupMoveCommand, PlayerMapGroupMoveCommand> moveTransformer;

    public static void Enqueue(PlayerMapGroupMoveCommand command)
    {
        if (!Valid(command)) return;
        if (moveTransformer != null)
            command = moveTransformer(command);
        if (!Valid(command)) return;
        MoveQueue.Enqueue(command);
    }

    /// <summary>
    /// Queues independent absolute targets without passing through interactive shared-delta snapping.
    /// Use this for deterministic layout tools such as Align/Distribute. Dragging and keyboard nudges
    /// must keep using Enqueue(PlayerMapGroupMoveCommand) so PlayerMapLayoutAssist can quantize their
    /// shared translation while preserving legacy Canon coordinate phase.
    /// </summary>
    public static void EnqueueExact(PlayerMapGroupMoveCommand command)
    {
        if (!Valid(command)) return;
        MoveQueue.Enqueue(command);
    }

    public static void Enqueue(PlayerMapGroupLayerCommand command)
    {
        if (command.RoomIndices == null || command.RoomIndices.Length == 0)
            return;
        LayerQueue.Enqueue(command);
    }

    public static void Enqueue(PlayerMapGroupPlacementCommand command)
    {
        if (command.RoomIndices == null || command.RoomIndices.Length == 0)
            return;
        PlacementQueue.Enqueue(command);
    }

    internal static bool TryDequeue(out PlayerMapGroupMoveCommand command) => MoveQueue.TryDequeue(out command);
    internal static bool TryDequeue(out PlayerMapGroupLayerCommand command) => LayerQueue.TryDequeue(out command);
    internal static bool TryDequeue(out PlayerMapGroupPlacementCommand command) => PlacementQueue.TryDequeue(out command);

    internal static void RegisterMoveTransformer(
        Func<PlayerMapGroupMoveCommand, PlayerMapGroupMoveCommand> transformer) =>
        moveTransformer = transformer;

    internal static void UnregisterMoveTransformer(
        Func<PlayerMapGroupMoveCommand, PlayerMapGroupMoveCommand> transformer)
    {
        if (Delegate.Equals(moveTransformer, transformer))
            moveTransformer = null;
    }

    internal static void Clear()
    {
        while (MoveQueue.TryDequeue(out _)) { }
        while (LayerQueue.TryDequeue(out _)) { }
        while (PlacementQueue.TryDequeue(out _)) { }
    }

    private static bool Valid(PlayerMapGroupMoveCommand command) =>
        command.RoomIndices != null && command.EffectivePositions != null &&
        command.RoomIndices.Length > 0 && command.RoomIndices.Length == command.EffectivePositions.Length;
}

/// <summary>
/// Executes Player Map group mutations on the same main-thread command boundary as existing map
/// edits. Child commands retain their proven per-room undo semantics; EditorHistoryService.BeginBatch
/// folds an entire group move/layer/placement change into one atomic user-visible history entry.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapRuntimePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapGroupCommandPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.GroupCommands";
    public const string PluginName = "DryCycle Player Map Group Commands";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            () => PlayerMapGroupCommandRuntime.Enable(Logger),
            PlayerMapGroupCommandRuntime.Disable);
    private void OnDisable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Disable(
            PluginName + ".OnDisable",
            PlayerMapGroupCommandRuntime.Disable);
}

internal static class PlayerMapGroupCommandRuntime
{
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map grouped placement/layer command runtime enabled through direct queue processing; no self-detour attached.");
    }

    internal static void Disable()
    {
        PlayerMapGroupCommandQueue.Clear();
        enabled = false;
        log = null;
    }

    internal static bool Process(EditorSession session)
    {
        if (!enabled || session == null)
        {
            if (session == null) PlayerMapGroupCommandQueue.Clear();
            return false;
        }

        bool processed = false;
        while (PlayerMapGroupCommandQueue.TryDequeue(out PlayerMapGroupMoveCommand group))
        {
            int count = Math.Min(group.RoomIndices?.Length ?? 0, group.EffectivePositions?.Length ?? 0);
            if (count <= 0) continue;

            using (session.History?.BeginBatch(group.Label))
            {
                for (int i = 0; i < count; i++)
                {
                    PlayerMapWorkspaceRuntime.Execute(session, new PlayerMapCommand(
                        PlayerMapCommandKind.SetEffectivePosition,
                        roomIndex: group.RoomIndices[i],
                        value: group.EffectivePositions[i]));
                }
            }
            processed = true;
        }

        while (PlayerMapGroupCommandQueue.TryDequeue(out PlayerMapGroupLayerCommand group))
        {
            int count = group.RoomIndices?.Length ?? 0;
            if (count <= 0) continue;

            using (session.History?.BeginBatch(group.Label))
            {
                for (int i = 0; i < count; i++)
                {
                    PlayerMapWorkspaceRuntime.Execute(session, new PlayerMapCommand(
                        PlayerMapCommandKind.SetLayer,
                        roomIndex: group.RoomIndices[i],
                        integer: group.Layer));
                }
            }
            processed = true;
        }

        while (PlayerMapGroupCommandQueue.TryDequeue(out PlayerMapGroupPlacementCommand group))
        {
            int count = group.RoomIndices?.Length ?? 0;
            if (count <= 0) continue;

            using (session.History?.BeginBatch(group.Label))
            {
                for (int i = 0; i < count; i++)
                {
                    int roomIndex = group.RoomIndices[i];
                    switch (group.Operation)
                    {
                        case PlayerMapGroupPlacementOperation.SetDerived:
                            PlayerMapWorkspaceRuntime.Execute(session, new PlayerMapCommand(
                                PlayerMapCommandKind.SetPlacementMode,
                                roomIndex: roomIndex,
                                integer: (int)PlayerMapPlacementMode.Derived));
                            break;
                        case PlayerMapGroupPlacementOperation.SetAbsolute:
                            PlayerMapWorkspaceRuntime.Execute(session, new PlayerMapCommand(
                                PlayerMapCommandKind.SetPlacementMode,
                                roomIndex: roomIndex,
                                integer: (int)PlayerMapPlacementMode.Absolute));
                            break;
                        case PlayerMapGroupPlacementOperation.ResetOffset:
                            PlayerMapWorkspaceRuntime.Execute(session, new PlayerMapCommand(
                                PlayerMapCommandKind.ResetOffset,
                                roomIndex: roomIndex));
                            break;
                    }
                }
            }
            processed = true;
        }

        return processed;
    }
}
