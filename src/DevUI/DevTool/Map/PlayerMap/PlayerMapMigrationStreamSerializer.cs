using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Completes map-config normalization for Ripple/WorldSpawnMigrationStream data without reproducing
/// vanilla's live-state mutation. A translated spline copy is serialized after the deterministic
/// room/Def_Mat/Connection writer succeeds; the live WorldSpawnMigrationStream is never changed.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapConfigSerializerV2Plugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapMigrationStreamSerializerPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.MigrationStreamSerializer";
    public const string PluginName = "DryCycle Player Map Migration Stream Serializer";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() => PlayerMapMigrationStreamSerializer.Enable(Logger);
    private void OnDisable() => PlayerMapMigrationStreamSerializer.Disable();
}

internal static class PlayerMapMigrationStreamSerializer
{
    private delegate bool OrigSave(MapPage page, PlayerMapSessionState state, out string error);
    private delegate bool HookSave(OrigSave orig, MapPage page, PlayerMapSessionState state, out string error);

    private static readonly HookSave SaveHookDelegate = SaveHook;
    private static IDisposable saveHook;
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo target = typeof(PlayerMapConfigSerializerV2).GetMethod(
                "Save",
                flags,
                null,
                new[] { typeof(MapPage), typeof(PlayerMapSessionState), typeof(string).MakeByRefType() },
                null);
            if (target == null)
                throw new MissingMethodException("PlayerMapConfigSerializerV2.Save was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            saveHook = constructor.Invoke(new object[] { target, SaveHookDelegate }) as IDisposable;
            if (saveHook == null)
                throw new InvalidOperationException("Player Map migration-stream serializer hook was not created.");

            enabled = true;
            log?.LogInfo("Player Map non-mutating migration-stream serializer enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map migration-stream serializer could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { saveHook?.Dispose(); }
        catch { }
        saveHook = null;
        enabled = false;
        log = null;
    }

    private static bool SaveHook(OrigSave orig, MapPage page, PlayerMapSessionState state, out string error)
    {
        if (!orig(page, state, out error)) return false;
        try
        {
            if (page?.world?.voidSpawnWorldAI?.worldMigrationStreams == null ||
                page.world.voidSpawnWorldAI.worldMigrationStreams.Count == 0)
                return true;

            Vector2 canonAverage = CanonAverage(page, state);
            List<string> streamLines = BuildStreamLines(page.world.voidSpawnWorldAI.worldMigrationStreams, canonAverage);
            RewriteStreamBlock(page.filePath, streamLines);
            return true;
        }
        catch (Exception exception)
        {
            error = "Map config was saved, but migration-stream snapshot serialization failed: " + exception.Message;
            log?.LogWarning(error);
            return false;
        }
    }

    private static Vector2 CanonAverage(MapPage page, PlayerMapSessionState state)
    {
        Vector2 total = Vector2.zero;
        int count = 0;
        if (page?.subNodes == null) return total;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
            if (!state.Rooms.TryGetValue(panel.roomRep.room.index, out PlayerMapRoomState roomState)) continue;
            total += PlayerMapWorkspaceRuntime.Effective(roomState, panel);
            count++;
        }
        return count > 0 ? total / count : Vector2.zero;
    }

    private static List<string> BuildStreamLines(
        List<WorldSpawnMigrationStream> streams,
        Vector2 canonAverage)
    {
        List<string> result = new();
        for (int i = 0; i < streams.Count; i++)
        {
            WorldSpawnMigrationStream source = streams[i];
            if (source?.spline == null) continue;

            BezierSpline.Midpoint[] midpoints = new BezierSpline.Midpoint[source.spline.midpoints.Count];
            for (int m = 0; m < midpoints.Length; m++)
            {
                BezierSpline.Midpoint midpoint = source.spline.midpoints[m];
                midpoints[m] = new BezierSpline.Midpoint(
                    midpoint.pos - canonAverage,
                    midpoint.PerpendicularAngle,
                    midpoint.length1,
                    midpoint.length2);
            }

            BezierSpline spline = new(
                source.spline.posA - canonAverage,
                source.spline.handleA - canonAverage,
                source.spline.posB - canonAverage,
                source.spline.handleB - canonAverage,
                midpoints);
            WorldSpawnMigrationStream snapshot = new(spline, source.name)
            {
                nextStreamName = source.nextStreamName,
                layers = source.layers == null ? new[] { true, true, true } : (bool[])source.layers.Clone(),
                width = source.width,
                rate = source.rate,
                destRoom = source.destRoom
            };

            if (result.Count > 0) result.Add(string.Empty);
            result.Add("SpawnMigrationStream: " + snapshot.Serialize());
            List<string> mids = snapshot.SerializeMidpoints();
            for (int m = 0; m < mids.Count; m++)
                result.Add("SpawnMigrationStreamMidpoint: " + mids[m]);
        }
        return result;
    }

    private static void RewriteStreamBlock(string path, List<string> streamLines)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Map config path is unavailable.");

        List<string> source = File.Exists(path)
            ? new List<string>(File.ReadAllLines(path))
            : new List<string>();
        List<string> output = new(source.Count + streamLines.Count + 1);
        int insertion = -1;

        for (int i = 0; i < source.Count; i++)
        {
            string line = source[i] ?? string.Empty;
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("SpawnMigrationStream:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("SpawnMigrationStreamMidpoint:", StringComparison.OrdinalIgnoreCase))
            {
                if (insertion < 0) insertion = output.Count;
                continue;
            }

            output.Add(line);
            if (trimmed.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
                insertion = output.Count;
        }

        if (insertion < 0) insertion = output.Count;
        if (streamLines.Count > 0)
        {
            List<string> block = new(streamLines.Count + 1) { string.Empty };
            block.AddRange(streamLines);
            output.InsertRange(insertion, block);
        }

        string temp = path + ".drycycle.tmp";
        File.WriteAllLines(temp, output.ToArray());
        PlayerMapConfigSerializer.AtomicReplaceSingle(temp, path);
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
