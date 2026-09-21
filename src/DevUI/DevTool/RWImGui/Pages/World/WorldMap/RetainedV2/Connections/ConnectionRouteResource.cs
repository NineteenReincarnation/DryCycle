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
    internal Num.Vector2[] Points = Array.Empty<Num.Vector2>();
    internal Num.Vector2 StartDirection;
    internal Num.Vector2 EndDirection;
    internal long Revision;
}
