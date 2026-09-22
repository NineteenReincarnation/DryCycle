using System;
using DryCycle.DevUI.DevTool.World;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Retained world-space route for one map connection. Points never depend on canvas pan or zoom.
/// </summary>
internal sealed class ConnectionRouteResource
{
    internal string ConnectionId = string.Empty;
    internal int FromRoomIndex;
    internal int ToRoomIndex;
    internal WorldConnectionDirection Direction;
    internal bool Ambiguous;
    internal WorldMapOrthogonalRouter.RouteKind Kind;

    // BasePoints is the immutable single-route result from the orthogonal router. Points is the
    // retained presentation path after global corridor-lane derivation. Keeping both prevents lane
    // offsets from accumulating across incremental rebuilds or persistent-cache restores.
    internal Num.Vector2[] BasePoints = Array.Empty<Num.Vector2>();
    internal Num.Vector2[] Points = Array.Empty<Num.Vector2>();

    internal Num.Vector2 StartDirection;
    internal Num.Vector2 EndDirection;

    // Derived presentation density only. 0 = normal, 1 = dense, 2 = extreme.
    // BaseDensityTier comes from endpoint crowding; DensityTier is the final max after corridor
    // analysis. Neither value is authoring/persistent data.
    internal byte BaseDensityTier;
    internal byte DensityTier;

    internal long Revision;
}
