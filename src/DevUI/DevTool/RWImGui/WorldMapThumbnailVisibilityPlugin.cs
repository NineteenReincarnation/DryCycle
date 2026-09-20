using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Keeps World Map room thumbnails readable independently of zoom and hover state.
///
/// WorldMapView calls this presentation helper directly. The plugin ID remains for dependency
/// ordering, but no DryCycle-owned draw method is RuntimeDetoured.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapImGuiPresentationFallbackPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapThumbnailVisibilityPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMap.ThumbnailVisibility";
    public const string PluginName = "DryCycle DevTool World Map Thumbnail Visibility";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapThumbnailVisibility.Enable(Logger);
    private void OnDisable() => WorldMapThumbnailVisibility.Disable();
}

internal static class WorldMapThumbnailVisibility
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        logger?.LogInfo("World Map thumbnails use direct zoom-independent contrast styling; no self-detour attached.");
    }

    internal static void Disable() => enabled = false;

    internal static int PushRoomStyle()
    {
        if (!enabled) return 0;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Num.Vector4(0.35f, 0.36f, 0.38f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Num.Vector4(0.62f, 0.64f, 0.66f, 0.95f));
        return 2;
    }

    internal static void PopRoomStyle(int count)
    {
        if (count > 0) ImGui.PopStyleColor(count);
    }

    internal static uint ResolveGeometryColor(EditorMapGeometryKind kind, uint fallback)
    {
        if (!enabled) return fallback;

        return kind switch
        {
            EditorMapGeometryKind.Air =>
                ImGui.GetColorU32(new Num.Vector4(0.68f, 0.69f, 0.70f, 1.00f)),
            EditorMapGeometryKind.BackWall =>
                ImGui.GetColorU32(new Num.Vector4(0.56f, 0.57f, 0.58f, 1.00f)),
            EditorMapGeometryKind.Solid =>
                ImGui.GetColorU32(new Num.Vector4(0.41f, 0.42f, 0.43f, 1.00f)),
            EditorMapGeometryKind.Structure =>
                ImGui.GetColorU32(new Num.Vector4(0.66f, 0.35f, 0.35f, 1.00f)),
            _ => fallback
        };
    }
}
