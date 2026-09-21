using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Merges authored continuous/custom terrain into the new static Player Map room bake. The base bake
/// remains sourced directly from room text; this overlay consumes only the rebuilt semantic terrain
/// runs from MapRoomGeometryPresentationHub and never asks vanilla MiniMap/RoomRepresentation to
/// generate a texture.
///
/// A base room bake is not considered officially ready while authored-terrain discovery is still
/// pending. This readiness barrier prevents timing-dependent Render Map output where an early render
/// could omit curves/custom terrain that had not finished entering the semantic cache yet.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapRuntimePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapTerrainBakeBridgePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.TerrainBakeBridge";
    public const string PluginName = "DryCycle Player Map Terrain Bake Bridge";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            () => PlayerMapTerrainBakeBridge.Enable(Logger),
            PlayerMapTerrainBakeBridge.Disable);
    private void OnDisable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Disable(
            PluginName + ".OnDisable",
            PlayerMapTerrainBakeBridge.Disable);
}

internal static class PlayerMapTerrainBakeBridge
{
    private sealed class OverlayEntry
    {
        internal RoomMapBake BaseBake;
        internal int TerrainRevision;
        internal RoomMapBake Enhanced;
    }

    private static readonly Dictionary<int, OverlayEntry> Cache = new();

    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map authored-terrain bake bridge enabled through direct cache calls; no self-detours attached.");
    }

    internal static void Disable()
    {
        Cache.Clear();
        enabled = false;
        log = null;
    }

    internal static bool TryEnhanceReady(int roomIndex, RoomMapBake baseBake, out RoomMapBake bake)
    {
        if (baseBake == null)
        {
            bake = null;
            Cache.Remove(roomIndex);
            return false;
        }

        if (!enabled)
        {
            bake = baseBake;
            return true;
        }

        if (!MapRoomGeometryPresentationHub.TryGetPlayerMapTerrainFillRuns(
                roomIndex,
                out EditorMapRectSnapshot[] terrainRuns,
                out int terrainRevision))
        {
            bake = null;
            Cache.Remove(roomIndex);
            return false;
        }

        if (terrainRuns == null || terrainRuns.Length == 0)
        {
            bake = baseBake;
            Cache.Remove(roomIndex);
            return true;
        }

        if (Cache.TryGetValue(roomIndex, out OverlayEntry cached) &&
            ReferenceEquals(cached.BaseBake, baseBake) &&
            cached.TerrainRevision == terrainRevision &&
            cached.Enhanced != null)
        {
            bake = cached.Enhanced;
            return true;
        }

        RoomMapBake enhanced = CloneBake(baseBake);
        ApplyTerrain(enhanced, terrainRuns);
        enhanced.Runs = BuildRuns(enhanced);
        Cache[roomIndex] = new OverlayEntry
        {
            BaseBake = baseBake,
            TerrainRevision = terrainRevision,
            Enhanced = enhanced
        };
        bake = enhanced;
        return true;
    }

    internal static RoomMapBakeSnapshot ProjectSnapshot(
        int roomIndex,
        RoomMapBakeSnapshot source,
        RoomMapBake baseBake)
    {
        if (!enabled || source == null || source.Status != RoomMapBakeStatus.Ready)
            return source;

        if (!MapRoomGeometryPresentationHub.TryGetPlayerMapTerrainFillRuns(
                roomIndex,
                out EditorMapRectSnapshot[] terrainRuns,
                out _))
        {
            return new RoomMapBakeSnapshot
            {
                Status = RoomMapBakeStatus.Pending,
                Width = source.Width,
                Height = source.Height,
                Error = "Authored terrain semantic scan is still pending.",
                Runs = source.Runs ?? Array.Empty<RoomMapPreviewRun>(),
                NodeAnchors = source.NodeAnchors ?? Array.Empty<RoomMapNodeAnchorSnapshot>()
            };
        }

        if (terrainRuns == null || terrainRuns.Length == 0)
            return source;

        if (!TryEnhanceReady(roomIndex, baseBake, out RoomMapBake enhanced) || enhanced == null)
        {
            return new RoomMapBakeSnapshot
            {
                Status = RoomMapBakeStatus.Pending,
                Width = source.Width,
                Height = source.Height,
                Error = "Authored terrain bake is still being merged.",
                Runs = source.Runs ?? Array.Empty<RoomMapPreviewRun>(),
                NodeAnchors = source.NodeAnchors ?? Array.Empty<RoomMapNodeAnchorSnapshot>()
            };
        }

        return new RoomMapBakeSnapshot
        {
            Status = RoomMapBakeStatus.Ready,
            Width = enhanced.Width,
            Height = enhanced.Height,
            Error = source.Error,
            Runs = enhanced.Runs ?? Array.Empty<RoomMapPreviewRun>(),
            NodeAnchors = enhanced.NodeAnchors ?? Array.Empty<RoomMapNodeAnchorSnapshot>()
        };
    }

    private static RoomMapBake CloneBake(RoomMapBake source)
    {
        RoomMapBake clone = new()
        {
            RoomIndex = source.RoomIndex,
            RoomName = source.RoomName,
            Width = source.Width,
            Height = source.Height,
            Pixels = source.Pixels == null ? Array.Empty<RoomMapPixel>() : (RoomMapPixel[])source.Pixels.Clone(),
            Runs = source.Runs ?? Array.Empty<RoomMapPreviewRun>(),
            NodeAnchors = source.NodeAnchors ?? Array.Empty<RoomMapNodeAnchorSnapshot>(),
            SourcePath = source.SourcePath,
            SourceLength = source.SourceLength,
            SourceWriteTimeUtc = source.SourceWriteTimeUtc
        };
        RoomMapNodeAnchorSnapshot[] anchors = clone.NodeAnchors;
        for (int i = 0; i < anchors.Length; i++)
            clone.NodeAnchorByIndex[anchors[i].NodeIndex] = anchors[i];
        return clone;
    }

    private static void ApplyTerrain(RoomMapBake bake, EditorMapRectSnapshot[] terrainRuns)
    {
        if (bake?.Pixels == null || bake.Width <= 0 || bake.Height <= 0 || terrainRuns == null) return;

        for (int i = 0; i < terrainRuns.Length; i++)
        {
            EditorMapRectSnapshot run = terrainRuns[i];
            if (!TryMapKind(run.Kind, out RoomMapPixelKind targetKind)) continue;

            float left = Math.Min(run.X, run.X + run.Width);
            float right = Math.Max(run.X, run.X + run.Width);
            float bottom = Math.Min(run.Y, run.Y + run.Height);
            float top = Math.Max(run.Y, run.Y + run.Height);
            int minX = Math.Max(0, (int)Math.Floor(left));
            int maxX = Math.Min(bake.Width - 1, (int)Math.Ceiling(right) - 1);
            int minY = Math.Max(0, (int)Math.Floor(bottom));
            int maxY = Math.Min(bake.Height - 1, (int)Math.Ceiling(top) - 1);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    // Use the tile centre as the semantic sample point. WorldMap terrain runs are
                    // conservative envelopes of the authored curves; centre sampling avoids turning
                    // every touched neighbouring tile into full material while retaining continuous
                    // slopes and custom bands.
                    float cx = x + 0.5f;
                    float cy = y + 0.5f;
                    if (cx < left || cx > right || cy < bottom || cy > top) continue;

                    int index = y * bake.Width + x;
                    RoomMapPixel current = bake.Pixels[index];
                    if (IsShortcut(current.Kind)) continue;

                    if (targetKind == RoomMapPixelKind.Solid || current.Kind == RoomMapPixelKind.Air ||
                        current.Kind == RoomMapPixelKind.BackWall)
                        bake.Pixels[index] = new RoomMapPixel(targetKind, current.Water);
                }
            }
        }
    }

    private static bool TryMapKind(EditorMapGeometryKind kind, out RoomMapPixelKind target)
    {
        switch (kind)
        {
            case EditorMapGeometryKind.Solid:
            case EditorMapGeometryKind.CurvedSlope:
                target = RoomMapPixelKind.Solid;
                return true;
            case EditorMapGeometryKind.Structure:
            case EditorMapGeometryKind.LocalTerrain:
            case EditorMapGeometryKind.QuicksandBody:
            case EditorMapGeometryKind.QuicksandMaterial:
                target = RoomMapPixelKind.Structure;
                return true;
            default:
                target = default;
                return false;
        }
    }

    private static bool IsShortcut(RoomMapPixelKind kind) =>
        kind == RoomMapPixelKind.RoomExit ||
        kind == RoomMapPixelKind.CreatureHole ||
        kind == RoomMapPixelKind.NormalShortcut ||
        kind == RoomMapPixelKind.NpcTransport ||
        kind == RoomMapPixelKind.RegionTransport;

    private static RoomMapPreviewRun[] BuildRuns(RoomMapBake bake)
    {
        List<RoomMapPreviewRun> runs = new();
        for (int y = 0; y < bake.Height; y++)
        {
            int x = 0;
            while (x < bake.Width)
            {
                RoomMapPixel pixel = bake.Pixels[y * bake.Width + x];
                int end = x + 1;
                while (end < bake.Width)
                {
                    RoomMapPixel next = bake.Pixels[y * bake.Width + end];
                    if (next.Kind != pixel.Kind || next.Water != pixel.Water) break;
                    end++;
                }
                runs.Add(new RoomMapPreviewRun(x, y, end - x, pixel.Kind, pixel.Water));
                x = end;
            }
        }
        return runs.ToArray();
    }

}
