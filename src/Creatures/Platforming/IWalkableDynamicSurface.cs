using UnityEngine;

namespace DryCycle.Creatures.Platforming;

/// <summary>
/// Immutable sample of one continuous walkable point on a moving surface. Coordinate is an
/// opaque provider-owned 1D parameter; the rider runtime only stores it between frames.
/// </summary>
public readonly struct WalkableSurfaceSample
{
    public readonly IWalkableDynamicSurface Surface;
    public readonly float Coordinate;
    public readonly Vector2 Point;
    public readonly Vector2 PreviousPoint;
    public readonly Vector2 Normal;
    public readonly Vector2 Tangent;

    public WalkableSurfaceSample(
        IWalkableDynamicSurface surface,
        float coordinate,
        Vector2 point,
        Vector2 previousPoint,
        Vector2 normal,
        Vector2 tangent)
    {
        Surface = surface;
        Coordinate = coordinate;
        Point = point;
        PreviousPoint = previousPoint;
        Normal = normal;
        Tangent = tangent;
    }

    public Vector2 Velocity => Point - PreviousPoint;
}

/// <summary>
/// Reusable contract for creature/object surfaces that should behave like moving ground for
/// players. Implementers own their geometry; the shared rider runtime owns Player integration.
/// </summary>
public interface IWalkableDynamicSurface
{
    Room SurfaceRoom { get; }
    bool SurfaceEnabled { get; }

    /// <summary>Sample the surface nearest a world-space rider position.</summary>
    bool TrySample(Vector2 worldPosition, out WalkableSurfaceSample sample);

    /// <summary>Re-sample a previously acquired provider coordinate after the surface moves.</summary>
    bool TrySample(float coordinate, out WalkableSurfaceSample sample);
}

/// <summary>
/// Optional owner contract for adapter-backed surfaces. Creature providers that directly inherit
/// PhysicalObject need not implement it; the runtime uses the provider itself as the owner.
/// </summary>
public interface IWalkableDynamicSurfaceOwner
{
    PhysicalObject SurfaceOwner { get; }
}

/// <summary>
/// Optional companion contract for providers whose collision representation needs one final
/// projection after Room finishes PhysicalObject-to-PhysicalObject collision resolution.
/// </summary>
public interface IPostRoomPhysicsWalkableSurface
{
    void FinalizeSurfacePhysics();
}
