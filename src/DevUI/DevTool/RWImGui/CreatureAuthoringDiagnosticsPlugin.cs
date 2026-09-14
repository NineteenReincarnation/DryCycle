using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Temporary, low-noise diagnostics for the World Workspace creature-authoring path.
/// It intentionally changes no editor behaviour. The trace is written only when the sampled
/// state changes, making BepInEx/LogOutput.log useful instead of flooding it every render frame.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class CreatureAuthoringDiagnosticsPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.CreatureAuthoringTrace";
    public const string PluginName = "DryCycle DevTool Creature Authoring Trace";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private const string TraceMarker = "CREATURE_TRACE_V1";
    private const int SampleIntervalFrames = 30;

    private int nextSampleFrame;
    private string lastSignature = string.Empty;
    private bool mismatchReported;

    private void OnEnable()
    {
        string assemblyLocation;
        try { assemblyLocation = typeof(BridgePlugin).Assembly.Location ?? "<unknown>"; }
        catch { assemblyLocation = "<unavailable>"; }

        Logger.LogWarning(
            "[CreatureTrace] ENABLED marker=" + TraceMarker +
            " bridgeVersion=" + BridgePlugin.PluginVersion +
            " assembly=" + assemblyLocation);

        LogPluginDiscovery();
        LogMethodPresence();
        nextSampleFrame = 0;
        lastSignature = string.Empty;
        mismatchReported = false;
    }

    private void Update()
    {
        if (Time.frameCount < nextSampleFrame) return;
        nextSampleFrame = Time.frameCount + SampleIntervalFrames;

        try
        {
            SampleState();
        }
        catch (Exception error)
        {
            Logger.LogError("[CreatureTrace] sampler failed: " + error);
        }
    }

    private void LogPluginDiscovery()
    {
        Logger.LogInfo(
            "[CreatureTrace] plugins " +
            "bridge=" + PluginLoaded(BridgePlugin.PluginId) +
            " spawnHelper=" + PluginLoaded(WorldCreatureSpawnInspectorPlugin.PluginId) +
            " catalogRuntime=" + PluginLoaded(WorldCreatureCatalogRuntimePlugin.PluginId) +
            " lineageHelper=" + PluginLoaded(WorldLineageInspectorPlugin.PluginId) +
            " trace=" + PluginLoaded(PluginId));
    }

    private void LogMethodPresence()
    {
        MethodInfo drawInspector = typeof(WorldWorkspaceView).GetMethod(
            "DrawInspector",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo drawRoomInspector = typeof(WorldWorkspaceView).GetMethod(
            "DrawRoomInspector",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo drawCreature = typeof(WorldCreatureSpawnInspector).GetMethod(
            "DrawIntegrated",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        MethodInfo drawSelector = typeof(WorldCreatureCatalogPicker).GetMethod(
            "DrawSelector",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Logger.LogInfo(
            "[CreatureTrace] methods " +
            "workspace.DrawInspector=" + MethodDescription(drawInspector) +
            " workspace.DrawRoomInspector=" + MethodDescription(drawRoomInspector) +
            " spawn.DrawIntegrated=" + MethodDescription(drawCreature) +
            " catalog.DrawSelector=" + MethodDescription(drawSelector));
    }

    private void SampleState()
    {
        EditorMapPresentationSnapshot snapshot = MapEditorPresentationHub.Current;
        bool mapAvailable = snapshot?.Available == true;
        int selectedRoom = mapAvailable ? snapshot.SelectedRoomIndex : -1;
        string selectedRoomName = ResolveRoomName(snapshot, selectedRoom);

        object selectionKind = ReadStaticField(typeof(WorldWorkspaceView), "selectionKind");
        int workspaceInspectorRoom = ReadIntField(typeof(WorldWorkspaceView), "inspectorRoom", -9999);

        bool spawnEnabled = ReadBoolField(typeof(WorldCreatureSpawnInspector), "enabled");
        int spawnStateRoom = ReadIntField(typeof(WorldCreatureSpawnInspector), "stateRoom", -9999);
        int selectedDen = ReadIntField(typeof(WorldCreatureSpawnInspector), "selectedDen", -9999);
        string creatureId = ReadStringField(typeof(WorldCreatureSpawnInspector), "creatureId");

        bool catalogInitialized = ReadBoolField(typeof(WorldCreatureCatalogPicker), "initialized");
        bool catalogRequested = ReadBoolField(typeof(WorldCreatureCatalogPicker), "catalogRequested");
        bool catalogValidated = ReadBoolField(typeof(WorldCreatureCatalogPicker), "currentCatalogValidated");
        int observedCreatureCount = ReadIntField(typeof(WorldCreatureCatalogPicker), "observedCreatureCount", -9999);
        int catalogGroups = ReadCatalogGroupCount();
        int registeredCreatureCount = SafeRegisteredCreatureCount();

        string signature =
            "map=" + mapAvailable +
            " selected=" + selectedRoom + ":" + selectedRoomName +
            " selectionKind=" + (selectionKind ?? "<null>") +
            " inspectorRoom=" + workspaceInspectorRoom +
            " spawnEnabled=" + spawnEnabled +
            " spawnStateRoom=" + spawnStateRoom +
            " den=" + selectedDen +
            " creature='" + creatureId + "'" +
            " catalogInit=" + catalogInitialized +
            " catalogRequested=" + catalogRequested +
            " catalogValidated=" + catalogValidated +
            " catalogGroups=" + catalogGroups +
            " observedCreatures=" + observedCreatureCount +
            " registeredCreatures=" + registeredCreatureCount;

        if (!string.Equals(signature, lastSignature, StringComparison.Ordinal))
        {
            lastSignature = signature;
            Logger.LogInfo("[CreatureTrace] state " + signature);
        }

        bool roomInspectorReached = mapAvailable && selectedRoom >= 0 && workspaceInspectorRoom == selectedRoom;
        bool creatureInspectorReached = mapAvailable && selectedRoom >= 0 && spawnStateRoom == selectedRoom;

        if (roomInspectorReached && !creatureInspectorReached)
        {
            if (!mismatchReported)
            {
                mismatchReported = true;
                Logger.LogWarning(
                    "[CreatureTrace] MISMATCH: WorldWorkspace room inspector reached room " +
                    selectedRoom + " ('" + selectedRoomName +
                    "'), but WorldCreatureSpawnInspector.DrawIntegrated has not observed that room. " +
                    "The failure is between DrawRoomInspector and DrawIntegrated, or the running DLL is not the expected build.");
            }
        }
        else
        {
            mismatchReported = false;
        }

        if (creatureInspectorReached && selectedDen < 0)
        {
            Logger.LogWarning(
                "[CreatureTrace] creature inspector reached room " + selectedRoomName +
                " but selectedDen=" + selectedDen +
                ". Check WorldCreaturePipeCatalog.Get / room Den data.");
        }

        if (creatureInspectorReached && !catalogInitialized)
        {
            Logger.LogWarning(
                "[CreatureTrace] creature inspector is active but catalogInitialized=false. " +
                "Check WorldCreatureCatalogRuntimePlugin / Bridge fallback lifecycle.");
        }

        if (creatureInspectorReached && catalogRequested && catalogValidated && catalogGroups == 0)
        {
            Logger.LogWarning(
                "[CreatureTrace] creature catalog was requested and validated but contains 0 groups. " +
                "Check ExtEnum CreatureTemplate registrations/source collection.");
        }
    }

    private int ReadCatalogGroupCount()
    {
        object catalog = ReadStaticField(typeof(WorldCreatureCatalogPicker), "catalog");
        if (catalog == null) return -1;
        try
        {
            FieldInfo groupsField = catalog.GetType().GetField(
                "Groups",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object groups = groupsField?.GetValue(catalog);
            if (groups is Array array) return array.Length;
            if (groups is ICollection collection) return collection.Count;
        }
        catch (Exception error)
        {
            Logger.LogWarning("[CreatureTrace] could not read catalog group count: " + error.Message);
        }
        return -1;
    }

    private static bool PluginLoaded(string pluginId)
    {
        try
        {
            return Chainloader.PluginInfos.TryGetValue(pluginId, out var info) && info?.Instance != null;
        }
        catch
        {
            return false;
        }
    }

    private static string MethodDescription(MethodInfo method)
    {
        if (method == null) return "missing";
        try
        {
            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            return "present(token=" + method.MetadataToken + ",il=" + (il?.Length ?? 0) + ")";
        }
        catch
        {
            return "present";
        }
    }

    private static object ReadStaticField(Type type, string name)
    {
        try
        {
            FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static int ReadIntField(Type type, string name, int fallback)
    {
        object value = ReadStaticField(type, name);
        return value is int number ? number : fallback;
    }

    private static bool ReadBoolField(Type type, string name)
    {
        object value = ReadStaticField(type, name);
        return value is bool flag && flag;
    }

    private static string ReadStringField(Type type, string name)
    {
        return ReadStaticField(type, name) as string ?? string.Empty;
    }

    private static int SafeRegisteredCreatureCount()
    {
        try { return ExtEnum<CreatureTemplate.Type>.values.entries.Count; }
        catch { return -1; }
    }

    private static string ResolveRoomName(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (rooms[i]?.RoomIndex == roomIndex) return rooms[i].Name ?? string.Empty;
        return string.Empty;
    }
}
