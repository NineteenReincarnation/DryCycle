using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>双 BodyChunk 默认身体；以有限加速度接近目标，使用游戏碰撞，姿势控制身体朝向。</summary>
public class StandardIteratorBody : IteratorBody
{
    public StandardIteratorBody(IteratorContext context, BodyProfile profile = null) : base(context, profile) { }

    protected override void OnInitialize()
    {
        Oracle oracle = Context.Oracle;
        Vector2 center = (oracle.bodyChunks[0].pos + oracle.bodyChunks[1].pos) * 0.5f;
        Vector2 half = Pose.BodyDirection * (Profile.ChunkDistance * 0.5f);
        var chunks = new[]
        {
            new BodyChunk(oracle, 0, center + half, Profile.Radius, Profile.ChunkMass),
            new BodyChunk(oracle, 1, center - half, Profile.Radius, Profile.ChunkMass)
        };
        ConfigurePhysics(chunks, new[]
        {
            new PhysicalObject.BodyChunkConnection(chunks[0], chunks[1], Profile.ChunkDistance,
                PhysicalObject.BodyChunkConnection.Type.Normal, 1f, 0.5f)
        });
        MoveTo(center);
    }

    protected override void OnUpdate()
    {
        Vector2 acceleration = Vector2.zero;
        if (MovementTarget.HasValue)
        {
            Vector2 target = Context.Arm.ConstrainTarget(MovementTarget.Value);
            if (IsDestroyed) return;
            Vector2 delta = target - Position;
            float distance = delta.magnitude;
            float speed = Mathf.Min(Profile.MaxSpeed, Mathf.Sqrt(2f * Profile.Acceleration * Mathf.Max(0f, distance - Profile.ArrivalRadius)));
            Vector2 desired = distance > 0.0001f ? delta / distance * speed : Vector2.zero;
            acceleration = Vector2.ClampMagnitude(desired - Velocity, Profile.Acceleration);
        }
        Vector2 difference = Chunks[0].pos - Chunks[1].pos;
        Vector2 orientation = (Pose.BodyDirection * Profile.ChunkDistance - difference) * 0.08f
            - (Chunks[0].vel - Chunks[1].vel) * 0.2f;
        Chunks[0].vel = Vector2.ClampMagnitude(Chunks[0].vel + acceleration + orientation, Profile.MaxSpeed);
        Chunks[1].vel = Vector2.ClampMagnitude(Chunks[1].vel + acceleration - orientation, Profile.MaxSpeed);
    }
}
