using System;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>固定房间锚点与最大伸长约束；不加载原版房间专用 Arm、音效或图形。</summary>
public sealed class FixedArm : IteratorArm
{
    public FixedArm(IteratorContext context, Vector2 anchor, float maxLength) : base(context)
    {
        Anchor = BodyValidation.Vector(anchor, nameof(anchor));
        MaxLength = BodyValidation.Range(maxLength, 1f, 100000f, nameof(maxLength));
    }

    public Vector2 Anchor { get; }
    public float MaxLength { get; }

    protected override void OnInitialize()
    {
        if (Vector2.Distance(Context.Body.Position, Anchor) > MaxLength + 0.01f)
            throw new InvalidOperationException("IteratorFramework: FixedArm cannot reach the body's initial position; change its anchor or length.");
    }

    protected override Vector2 OnConstrainTarget(Vector2 target) => Anchor + Vector2.ClampMagnitude(target - Anchor, MaxLength);

    protected override void OnUpdate()
    {
        IteratorBody body = Context.Body;
        Vector2 predicted = body.Position + body.Velocity;
        Vector2 correction = OnConstrainTarget(predicted) - predicted;
        for (int i = 0; i < body.Chunks.Count; i++) body.Chunks[i].vel += correction;
    }

    protected override void OnAfterPhysics()
    {
        IteratorBody body = Context.Body;
        Vector2 offset = body.Position - Anchor;
        if (offset.sqrMagnitude <= MaxLength * MaxLength) return;
        body.CorrectPosition(Vector2.ClampMagnitude(offset, MaxLength) - offset);
        Vector2 outward = offset.normalized;
        for (int i = 0; i < body.Chunks.Count; i++)
        {
            BodyChunk chunk = body.Chunks[i];
            float speed = Vector2.Dot(chunk.vel, outward);
            if (speed > 0f) chunk.vel -= outward * speed;
        }
    }
}
