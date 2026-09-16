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

public static class PlayerMapGroupCommandQueue
{
    private static readonly ConcurrentQueue<PlayerMapGroupMoveCommand> MoveQueue = new();
    private static readonly ConcurrentQueue<PlayerMapGroupLayerCommand> LayerQueue = new();

    public static void Enqueue(PlayerMapGroupMoveCommand command)
    {
        if (command.RoomIndices == null || command.EffectivePositions == null ||
            command.RoomIndices.Length == 0 || command.RoomIndices.Length != command.EffectivePositions.Length)
            return;
        MoveQueue.Enqueue(command);
    }

    public static void Enqueue(PlayerMapGroupLayerCommand command)
    {
        if (command.RoomIndices == null || command.RoomIndices.Length == 0)
            return;
        LayerQueue.Enqueue(command);
    }

    internal static bool TryDequeue(out PlayerMapGroupMoveCommand command) => MoveQueue.TryDequeue(out command);
    internal static bool TryDequeue(out PlayerMapGroupLayerCommand command) => LayerQueue.TryDequeue(out command);

    internal static void Clear()
    {
        while (MoveQueue.TryDequeue(out _)) { }
        while (LayerQueue.TryDequeue(out _)) { }
    }
}

/// <summary>
/// Executes Player Map group mutations on the same main-thread command boundary as existing map
/// edits. Child commands retain their proven per-room undo semantics; EditorHistoryService.BeginBatch
/// folds an entire group move/layer change into one atomic user-visible history entry.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapRuntimePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapGroupCommandPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.GroupCommands";
    public const string PluginName = "DryCycle Player Map Group Commands";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() => PlayerMapGroupCommandRuntime.Enable(Logger);
    private void OnDisable() => PlayerMapGroupCommandRuntime.Disable();
}

internal static class PlayerMapGroupCommandRuntime
{
    private delegate void OrigProcess(EditorSession session);
    private delegate void HookProcess(OrigProcess orig, EditorSession session);

    private static readonly HookProcess ProcessHookDelegate = ProcessHook;
    private static IDisposable processHook;
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public;
            System.Reflection.MethodInfo process = typeof(PlayerMapCommandQueue).GetMethod(
                "Process", flags, null, new[] { typeof(EditorSession) }, null);
            if (process == null)
                throw new MissingMethodException("PlayerMapCommandQueue.Process was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            System.Reflection.ConstructorInfo constructor =
                hookType?.GetConstructor(new[] { typeof(System.Reflection.MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            processHook = constructor.Invoke(new object[] { process, ProcessHookDelegate }) as IDisposable;
            if (processHook == null)
                throw new InvalidOperationException("Player Map group command hook was not created.");

            enabled = true;
            log?.LogInfo("Player Map grouped placement/layer command runtime enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map group command runtime could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { processHook?.Dispose(); }
        catch { }
        processHook = null;
        PlayerMapGroupCommandQueue.Clear();
        enabled = false;
        log = null;
    }

    private static void ProcessHook(OrigProcess orig, EditorSession session)
    {
        orig(session);
        if (!enabled || session == null)
        {
            if (session == null) PlayerMapGroupCommandQueue.Clear();
            return;
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

        if (processed)
            PlayerMapWorkspaceRuntime.Synchronize(session);
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is System.Reflection.TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
