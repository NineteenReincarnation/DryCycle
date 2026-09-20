using BepInEx;
using BepInEx.Logging;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Presents room exits and creature holes as two independent World Map layers.
///
/// WorldMapView reads these flags and draws the controls directly; no self-hooks are used.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapPipeLayerPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapPipeLayers";
    public const string PluginName = "DryCycle DevTool World Map Pipe Layers";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapPipeLayers.Enable(Logger);
    private void OnDisable() => WorldMapPipeLayers.Disable();
}

internal static class WorldMapPipeLayers
{
    private static bool enabled;
    private static bool roomPipesVisible = true;
    private static bool creaturePipesVisible = true;

    internal static bool RoomPipesVisible => !enabled || roomPipesVisible;
    internal static bool CreaturePipesVisible => !enabled || creaturePipesVisible;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        roomPipesVisible = true;
        creaturePipesVisible = true;
        logger?.LogInfo("World Map room/creature pipe layers use direct view controls; no self-detour attached.");
    }

    internal static void Disable()
    {
        enabled = false;
        roomPipesVisible = true;
        creaturePipesVisible = true;
    }

    internal static void DrawToolbarControls(ref bool portLabels, ref int linkingRoom, ref int linkingNode)
    {
        if (!enabled) return;

        bool roomVisible = roomPipesVisible;
        if (ImGui.Checkbox(
                DevToolUiSettings.T("房间管道", "Room pipes") + "##WorldMapRoomPipes",
                ref roomVisible))
        {
            bool wasVisible = roomPipesVisible;
            roomPipesVisible = roomVisible;
            if (wasVisible && !roomPipesVisible)
            {
                linkingRoom = -1;
                linkingNode = -1;
            }
        }

        portLabels = roomPipesVisible;

        ImGui.SameLine();
        bool creatureVisible = creaturePipesVisible;
        if (ImGui.Checkbox(
                DevToolUiSettings.T("生物管道", "Creature pipes") + "##WorldMapCreaturePipes",
                ref creatureVisible))
            creaturePipesVisible = creatureVisible;
    }
}
