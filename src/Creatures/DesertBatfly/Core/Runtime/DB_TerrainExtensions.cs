using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Narrow compatibility helper for the older DesertBatflyAI steering probe. Rain World's
/// TerrainManager exposes tile obstruction, not a Vector2 Contains API; convert through the
/// same Room tile mapping used by the game and delegate to the native terrain manager.
/// Keep this species-local rather than changing TerrainManager globally.
/// </summary>
internal static class DB_TerrainExtensions
{
    internal static bool Contains(this TerrainManager terrain, Vector2 worldPosition)
    {
        return terrain != null && terrain.ObstructsTile(Room.StaticGetTilePosition(worldPosition));
    }
}
