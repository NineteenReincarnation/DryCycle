using System;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Explicit ImGui draw-channel ownership for the World Map.
///
/// WorldMapView calls this service directly. No DryCycle-owned draw method is RuntimeDetoured.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapPresentationCorrectnessPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapRenderOrderPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMap.RenderOrder";
    public const string PluginName = "DryCycle DevTool World Map Render Order";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapRenderOrder.Enable(Logger);
    private void OnDisable() => WorldMapRenderOrder.Disable();
}

internal static class WorldMapRenderOrder
{
    private const int BaseChannel = 0;
    private const int ConnectionChannel = 1;
    private const int OverlayChannel = 2;
    private const int ChannelCount = 3;

    private static ManualLogSource log;
    private static bool enabled;
    private static bool channelsActive;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("World Map render order uses direct draw-channel calls: rooms < connections < overlays; no self-detour attached.");
    }

    internal static void Disable()
    {
        channelsActive = false;
        enabled = false;
        log = null;
    }

    internal static bool BeginCanvas(ImDrawListPtr draw, EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || snapshot?.Available != true || channelsActive)
            return false;

        try
        {
            draw.ChannelsSplit(ChannelCount);
            channelsActive = true;
            draw.ChannelsSetCurrent(BaseChannel);
            return true;
        }
        catch (Exception error)
        {
            channelsActive = false;
            log?.LogDebug("World Map channel split failed: " + error.Message);
            return false;
        }
    }

    internal static void EndCanvas(ImDrawListPtr draw, bool split)
    {
        if (!split) return;
        channelsActive = false;
        try
        {
            draw.ChannelsSetCurrent(BaseChannel);
            draw.ChannelsMerge();
        }
        catch (Exception error)
        {
            log?.LogDebug("World Map channel merge failed: " + error.Message);
        }
    }

    internal static void UseBase(ImDrawListPtr draw)
    {
        if (channelsActive) draw.ChannelsSetCurrent(BaseChannel);
    }

    internal static void UseConnections(ImDrawListPtr draw)
    {
        if (channelsActive) draw.ChannelsSetCurrent(ConnectionChannel);
    }

    internal static void UseOverlay(ImDrawListPtr draw)
    {
        if (channelsActive) draw.ChannelsSetCurrent(OverlayChannel);
    }
}
