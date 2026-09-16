using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Restores Rain World's centered map-file convention without restoring its live-state side effects.
/// Vanilla subtracts the Canon/Dev means directly from RoomPanel, Def_Mat handles and migration
/// splines before writing. This serializer performs the same coordinate transform only on a frozen
/// serialization copy, leaving the active new-UI workspace, Undo history and render caches untouched.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapConnectionMetadataSerializerPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapNormalizedConfigSerializerPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.NormalizedConfig";
    public const string PluginName = "DryCycle Player Map Normalized Config";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() => PlayerMapNormalizedConfigSerializer.Enable(Logger);
    private void OnDisable() => PlayerMapNormalizedConfigSerializer.Disable();
}

internal static class PlayerMapNormalizedConfigSerializer
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
            MethodInfo save = typeof(PlayerMapConfigSerializer).GetMethod(
                "Save",
                flags,
                null,
                new[]
                {
                    typeof(MapPage),
                    typeof(PlayerMapSessionState),
                    typeof(string).MakeByRefType()
                },
                null);
            if (save == null)
                throw new MissingMethodException("PlayerMapConfigSerializer.Save was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            saveHook = constructor.Invoke(new object[] { save, SaveHookDelegate }) as IDisposable;
            if (saveHook == null)
                throw new InvalidOperationException("Player Map normalized-config hook was not created.");

            enabled = true;
            log?.LogInfo("Player Map non-destructive coordinate normalization enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map normalized config serializer could not attach: " + Unwrap(error).Message);
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
        if (!enabled)
            return orig(page, state, out error);

        string path = page?.filePath ?? string.Empty;
        bool existed = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        string[] before = Array.Empty<string>();
        if (existed)
        {
            try { before = File.ReadAllLines(path); }
            catch (Exception readError)
            {
                error = "Could not snapshot map config before normalized save: " + readError.Message;
                return false;
            }
        }

        if (!orig(page, state, out error))
            return false;

        if (TryNormalizeSavedDocument(page, state, out string normalizeError))
        {
            error = null;
            return true;
        }

        try
        {
            if (existed)
                AtomicWrite(path, before);
            else if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception rollbackError)
        {
            error = normalizeError + " Rollback also failed: " + rollbackError.Message;
            log?.LogError(error);
            return false;
        }

        error = normalizeError;
        return false;
    }

    private static bool TryNormalizeSavedDocument(MapPage page, PlayerMapSessionState state, out string error)
    {
        error = null;
        if (page?.world == null || state == null || page.subNodes == null ||
            string.IsNullOrWhiteSpace(page.filePath) || !File.Exists(page.filePath))
        {
            error = "Map config state is unavailable for coordinate normalization.";
            return false;
        }

        Vector2 canonMean = Vector2.zero;
        Vector2 devMean = Vector2.zero;
        int roomCount = 0;
        Dictionary<string, string> normalizedRooms = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> disabled = new(StringComparer.OrdinalIgnoreCase);
        if (page.world.DisabledMapRooms != null)
        {
            for (int i = 0; i < page.world.DisabledMapRooms.Count; i++)
            {
                string name = page.world.DisabledMapRooms[i];
                if (!string.IsNullOrWhiteSpace(name)) disabled.Add(name);
            }
        }

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
            AbstractRoom room = panel.roomRep.room;
            if (!state.Rooms.TryGetValue(room.index, out PlayerMapRoomState roomState))
            {
                error = (room.name ?? room.index.ToString()) + ": Player Map placement state is unavailable.";
                return false;
            }

            canonMean += PlayerMapWorkspaceRuntime.Effective(roomState, panel);
            devMean += panel.devPos;
            roomCount++;
        }

        if (roomCount <= 0)
        {
            error = "No rooms are available for map coordinate normalization.";
            return false;
        }
        canonMean /= roomCount;
        devMean /= roomCount;

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
            AbstractRoom room = panel.roomRep.room;
            if (disabled.Contains(room.name ?? string.Empty)) continue;
            if (!state.Rooms.TryGetValue(room.index, out PlayerMapRoomState roomState)) continue;

            Vector2 canon = PlayerMapWorkspaceRuntime.Effective(roomState, panel) - canonMean;
            Vector2 dev = panel.devPos - devMean;
            normalizedRooms[room.name ?? string.Empty] =
                (room.name ?? string.Empty) + ": " +
                F(canon.x) + "><" + F(canon.y) + "><" +
                F(dev.x) + "><" + F(dev.y) + "><" +
                Mathf.Clamp(panel.layer, 0, 2).ToString(CultureInfo.InvariantCulture) + "><" +
                (room.subregionName ?? string.Empty) + "><" +
                room.size.x.ToString(CultureInfo.InvariantCulture) + "><" +
                room.size.y.ToString(CultureInfo.InvariantCulture);
        }

        string[] current = File.ReadAllLines(page.filePath);
        List<string> output = new(current.Length + state.DefaultMaterials.Count + 16);
        int lastRoom = -1;
        int lastConnection = -1;

        for (int i = 0; i < current.Length; i++)
        {
            string line = current[i] ?? string.Empty;
            if (line.TrimStart().StartsWith("Def_Mat:", StringComparison.OrdinalIgnoreCase) ||
                line.TrimStart().StartsWith("SpawnMigrationStream:", StringComparison.OrdinalIgnoreCase) ||
                line.TrimStart().StartsWith("SpawnMigrationStreamMidpoint:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryRoomRecordName(line, out string roomName) && normalizedRooms.TryGetValue(roomName, out string replacement))
            {
                output.Add(replacement);
                lastRoom = output.Count - 1;
                continue;
            }

            output.Add(line);
            if (line.TrimStart().StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
                lastConnection = output.Count - 1;
        }

        int defInsertion = lastRoom >= 0 ? lastRoom + 1 : 0;
        if (state.DefaultMaterials.Count > 0)
        {
            List<string> defs = new(state.DefaultMaterials.Count);
            for (int i = 0; i < state.DefaultMaterials.Count; i++)
            {
                PlayerMapDefMaterialState item = state.DefaultMaterials[i];
                Vector2 a = item.A - canonMean;
                Vector2 b = item.B - canonMean;
                // PanelPosition is local to handle A in vanilla and must not be translated.
                defs.Add("Def_Mat: " + F(a.x) + "," + F(a.y) + "," +
                         F(b.x) + "," + F(b.y) + "," +
                         F(item.PanelPosition.x) + "," + F(item.PanelPosition.y) + "," +
                         (item.Air ? "1" : "0"));
            }
            output.InsertRange(defInsertion, defs);
        }

        // Connection records were regenerated by PlayerMapConnectionMetadataSerializer and contain
        // room-local shortcut coordinates, so they are intentionally not translated by canonMean.
        lastConnection = -1;
        for (int i = 0; i < output.Count; i++)
            if ((output[i] ?? string.Empty).TrimStart().StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
                lastConnection = i;

        List<string> streams = BuildNormalizedMigrationStreams(page.world, canonMean);
        if (streams.Count > 0)
        {
            int streamInsertion = lastConnection >= 0
                ? lastConnection + 1
                : Math.Min(output.Count, defInsertion + state.DefaultMaterials.Count);
            output.InsertRange(streamInsertion, streams);
        }

        AtomicWrite(page.filePath, output.ToArray());
        return true;
    }

    private static List<string> BuildNormalizedMigrationStreams(global::World world, Vector2 canonMean)
    {
        List<string> lines = new();
        var ai = world?.voidSpawnWorldAI;
        if (ai?.worldMigrationStreams == null) return lines;

        for (int i = 0; i < ai.worldMigrationStreams.Count; i++)
        {
            WorldSpawnMigrationStream source = ai.worldMigrationStreams[i];
            if (source?.spline == null) continue;

            BezierSpline.Midpoint[] midpoints = new BezierSpline.Midpoint[source.spline.midpoints.Count];
            for (int m = 0; m < midpoints.Length; m++)
            {
                BezierSpline.Midpoint midpoint = source.spline.midpoints[m];
                midpoint.pos -= canonMean;
                midpoints[m] = midpoint;
            }

            BezierSpline shifted = new(
                source.spline.posA - canonMean,
                source.spline.handleA - canonMean,
                source.spline.posB - canonMean,
                source.spline.handleB - canonMean,
                midpoints);
            WorldSpawnMigrationStream copy = new(shifted, source.name)
            {
                nextStreamName = source.nextStreamName,
                layers = source.layers == null ? new bool[3] : (bool[])source.layers.Clone(),
                width = source.width,
                rate = source.rate,
                destRoom = source.destRoom
            };

            if (lines.Count > 0) lines.Add(string.Empty);
            lines.Add("SpawnMigrationStream: " + copy.Serialize());
            List<string> mids = copy.SerializeMidpoints();
            for (int m = 0; m < mids.Count; m++)
                lines.Add("SpawnMigrationStreamMidpoint: " + mids[m]);
        }
        return lines;
    }

    private static bool TryRoomRecordName(string line, out string roomName)
    {
        roomName = null;
        if (string.IsNullOrWhiteSpace(line) || line.IndexOf("><", StringComparison.Ordinal) < 0) return false;
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

    private static void AtomicWrite(string target, string[] lines)
    {
        string directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temp = target + ".normalize.drycycle.tmp";
        File.WriteAllLines(temp, lines ?? Array.Empty<string>());
        PlayerMapConfigSerializer.AtomicReplaceSingle(temp, target);
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
