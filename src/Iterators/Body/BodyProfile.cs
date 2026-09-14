using System;

namespace DryCycle.Iterators;

/// <summary>不可变的默认身体配置。距离为房间像素，速度/加速度按游戏物理帧计；Gravity 乘以房间重力。</summary>
public sealed class BodyProfile
{
    public static BodyProfile Default { get; } = new();

    public BodyProfile(float radius = 6f, float chunkMass = 0.5f, float chunkDistance = 9f,
        float maxSpeed = 4f, float acceleration = 0.35f, float arrivalRadius = 1f,
        float gravity = 0f, float airFriction = 0.99f, float waterFriction = 0.92f,
        float buoyancy = 0.95f, float bounce = 0.1f, float surfaceFriction = 0.17f)
    {
        Radius = BodyValidation.Range(radius, 1f, 100f, nameof(radius));
        ChunkMass = BodyValidation.Range(chunkMass, 0.01f, 100f, nameof(chunkMass));
        ChunkDistance = BodyValidation.Range(chunkDistance, 1f, 200f, nameof(chunkDistance));
        MaxSpeed = BodyValidation.Range(maxSpeed, 0.01f, 20f, nameof(maxSpeed));
        Acceleration = BodyValidation.Range(acceleration, 0.001f, 20f, nameof(acceleration));
        ArrivalRadius = BodyValidation.Range(arrivalRadius, 0.01f, 100f, nameof(arrivalRadius));
        Gravity = BodyValidation.Range(gravity, 0f, 5f, nameof(gravity));
        AirFriction = BodyValidation.Range(airFriction, 0f, 1f, nameof(airFriction));
        WaterFriction = BodyValidation.Range(waterFriction, 0f, 1f, nameof(waterFriction));
        Buoyancy = BodyValidation.Range(buoyancy, 0f, 5f, nameof(buoyancy));
        Bounce = BodyValidation.Range(bounce, 0f, 1f, nameof(bounce));
        SurfaceFriction = BodyValidation.Range(surfaceFriction, 0f, 1f, nameof(surfaceFriction));
    }

    public float Radius { get; }
    public float ChunkMass { get; }
    public float ChunkDistance { get; }
    public float MaxSpeed { get; }
    public float Acceleration { get; }
    public float ArrivalRadius { get; }
    public float Gravity { get; }
    public float AirFriction { get; }
    public float WaterFriction { get; }
    public float Buoyancy { get; }
    public float Bounce { get; }
    public float SurfaceFriction { get; }
}
