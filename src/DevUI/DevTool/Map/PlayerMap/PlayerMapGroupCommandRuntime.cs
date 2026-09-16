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

public static class PlayerMapGroupCommandQueue
{
    private static readonly ConcurrentQueue<PlayerMapGroupMoveCommand> Queue = new();

    public static void Enqueue(PlayerMapGroupMoveCommand command)
    {
        if (command.RoomIndices == null || command.EffectivePositions == null ||
            command.RoomIndices.Length == 0 || command.RoomIndices.Length != command.EffectivePositions.Length)
            return;
        Queue.Enqueue(command);
    }

    internal static bool TryDequeue(out PlayerMapGroupMoveCommand command) => Queue.TryDequeue(out command);

    internal static void Clear()
    {
        while (Queue.TryDequeue(out _)) { }
    }
}

/// <summary>
/// Executes Player Map group movement on the same main-thread command boundary as existing map edits.
/// Child commands keep their proven per-room undo logic; EditorHistoryService.BeginBatch folds them
/// into one atomic user-visible history entry.
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
            log?.LogInfo("Player Map grouped placement command runtime enabled.");
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

        bool changed = false;
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
            changed = true;
        }

        if (changed)
            PlayerMapWorkspaceRuntime.Synchronize(session);
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is System.Reflection.TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
