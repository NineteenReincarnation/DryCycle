using System;

namespace DryCycle.DevUI.DevTool.Map;

/// <summary>
/// Narrow semantic bridge from the rebuilt World Map geometry cache to Player Map baking. This does
/// not expose vanilla MapTex pixels; it only exposes authored terrain fill runs already rebuilt from
/// RoomSettings (TerrainHandle, LocalTerrain, CurvedSlope, SuperSlope and DryCycle terrain).
/// </summary>
internal static partial class MapRoomGeometryPresentationHub
{
    internal static bool TryGetPlayerMapTerrainFillRuns(
        int roomIndex,
        out EditorMapRectSnapshot[] runs,
        out int semanticRevision)
    {
        runs = Array.Empty<EditorMapRectSnapshot>();
        semanticRevision = 0;
        if (!cache.TryGetValue(roomIndex, out CacheEntry entry) || !entry.CurvesInitialized)
            return false;

        runs = entry.TerrainFillRuns ?? Array.Empty<EditorMapRectSnapshot>();
        unchecked
        {
            // SettingsFingerprint changes when authored terrain changes. Include the fill count so a
            // provider that legitimately produces zero runs still invalidates an older overlay.
            semanticRevision = entry.SettingsFingerprint * 397 ^ runs.Length;
        }
        return true;
    }
}
