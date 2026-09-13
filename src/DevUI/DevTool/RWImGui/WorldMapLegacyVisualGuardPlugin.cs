using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Keeps the vanilla MapPage alive as an authoring/data source without allowing its Futile visuals
/// to leak through the rebuilt RWImGui world map.
///
/// The retained renderer still needs RoomPanel/MapTex and the vanilla page must continue updating so
/// room textures, node positions and map authoring state stay authoritative. Destroying or clearing
/// those nodes would break that contract. Instead this guard runs in LateUpdate, after vanilla DevUI
/// has refreshed for the frame, and only suppresses presentation objects. Switching back to Vanilla
/// presentation restores the objects and asks MapPage to refresh its own visibility rules.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRendererPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapLegacyVisualGuardPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.LegacyVisualGuard";
    public const string PluginName = "DryCycle DevTool World Map Legacy Visual Guard";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapLegacyVisualGuard.Enable(Logger);

    // LateUpdate is deliberate. MapPage/MiniMap.Refresh can set isVisible=true during the normal
    // Rain World update; suppressing before that would allow the old map to reappear for rendering.
    private void LateUpdate() => WorldMapLegacyVisualGuard.LateUpdate();

    private void OnDisable() => WorldMapLegacyVisualGuard.Disable();
}

internal static class WorldMapLegacyVisualGuard
{
    private static readonly HashSet<FSprite> SuppressedSprites = new();
    private static readonly HashSet<FLabel> SuppressedLabels = new();

    private static ManualLogSource log;
    private static MapPage suppressedPage;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        enabled = true;
        log?.LogInfo("World Map legacy visual guard enabled.");
    }

    internal static void Disable()
    {
        Restore();
        enabled = false;
        log = null;
    }

    internal static void LateUpdate()
    {
        if (!enabled) return;

        EditorSession session = DevToolRuntime.ActiveSession;
        bool rebuiltMapOwnsPresentation =
            !EditorUiModeState.UseVanilla &&
            session?.ToolMode == EditorToolMode.Map &&
            session.Owner?.activePage is MapPage;

        if (!rebuiltMapOwnsPresentation)
        {
            Restore();
            return;
        }

        MapPage page = session.Owner.activePage as MapPage;
        if (!ReferenceEquals(page, suppressedPage))
        {
            Restore();
            suppressedPage = page;
        }

        SuppressNode(page);
        SuppressCreatureVisuals(page);
    }

    private static void SuppressNode(DevUINode node)
    {
        if (node == null) return;

        List<FSprite> sprites = node.fSprites;
        if (sprites != null)
        {
            for (int i = 0; i < sprites.Count; i++) Suppress(sprites[i]);
        }

        List<FLabel> labels = node.fLabels;
        if (labels != null)
        {
            for (int i = 0; i < labels.Count; i++) Suppress(labels[i]);
        }

        List<DevUINode> children = node.subNodes;
        if (children == null) return;
        for (int i = 0; i < children.Count; i++) SuppressNode(children[i]);
    }

    private static void SuppressCreatureVisuals(MapPage page)
    {
        List<MapPage.CreatureVis> creatures = page?.creatureVisualizations;
        if (creatures == null) return;

        for (int i = 0; i < creatures.Count; i++)
        {
            MapPage.CreatureVis creature = creatures[i];
            if (creature == null) continue;
            Suppress(creature.sprite);
            Suppress(creature.sprite2);
            Suppress(creature.label);
            Suppress(creature.label2);
        }
    }

    private static void Suppress(FSprite sprite)
    {
        if (sprite == null) return;

        // Only remember objects that vanilla actually wanted visible. Objects which are logically
        // hidden by layer/mode stay hidden when we later restore the page.
        if (sprite.isVisible) SuppressedSprites.Add(sprite);
        if (SuppressedSprites.Contains(sprite)) sprite.isVisible = false;
    }

    private static void Suppress(FLabel label)
    {
        if (label == null) return;
        if (label.isVisible) SuppressedLabels.Add(label);
        if (SuppressedLabels.Contains(label)) label.isVisible = false;
    }

    private static void Restore()
    {
        if (suppressedPage == null && SuppressedSprites.Count == 0 && SuppressedLabels.Count == 0)
            return;

        foreach (FSprite sprite in SuppressedSprites)
        {
            try
            {
                if (sprite != null) sprite.isVisible = true;
            }
            catch
            {
                // A page can be destroyed while DevTools closes. Dead Futile nodes need no restore.
            }
        }

        foreach (FLabel label in SuppressedLabels)
        {
            try
            {
                if (label != null) label.isVisible = true;
            }
            catch
            {
            }
        }

        MapPage page = suppressedPage;
        suppressedPage = null;
        SuppressedSprites.Clear();
        SuppressedLabels.Clear();

        // Re-evaluate vanilla layer/canon/ripple visibility after our temporary suppression rather
        // than assuming every object should remain visible when the author switches back to Vanilla.
        try { page?.Refresh(); }
        catch (Exception error)
        {
            log?.LogDebug("World Map legacy visual restore skipped refresh: " + error.Message);
        }
    }
}
