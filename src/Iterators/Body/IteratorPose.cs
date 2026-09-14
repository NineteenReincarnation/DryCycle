using System;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>与 Graphics 无关的不可变姿势输入。手脚偏移使用身体局部坐标：x 向右，y 向头部。</summary>
public sealed class IteratorPose
{
    public static IteratorPose Idle { get; } = new("Idle");
    public static IteratorPose Talk { get; } = new("Talk", rightHandOffset: new Vector2(12f, 6f));
    public static IteratorPose Look { get; } = new("Look");
    public static IteratorPose Think { get; } = new("Think", rightHandOffset: new Vector2(4f, 10f));
    public static IteratorPose Angry { get; } = new("Angry", leftHandOffset: new Vector2(-12f, 3f), rightHandOffset: new Vector2(12f, 3f));
    public static IteratorPose Inspect { get; } = new("Inspect", leftHandOffset: new Vector2(-5f, 8f), rightHandOffset: new Vector2(5f, 8f));
    public static IteratorPose Reach { get; } = new("Reach", rightHandOffset: new Vector2(18f, 10f));
    public static IteratorPose Point { get; } = new("Point", rightHandOffset: new Vector2(22f, 3f));

    public IteratorPose(string name, Vector2? bodyDirection = null,
        Vector2? leftHandOffset = null, Vector2? rightHandOffset = null,
        Vector2? leftFootOffset = null, Vector2? rightFootOffset = null)
    {
        IteratorValidation.RequireText(name, nameof(name));
        Name = name;
        Vector2 direction = BodyValidation.Vector(bodyDirection ?? Vector2.up, nameof(bodyDirection), 1000f);
        if (direction.sqrMagnitude < 0.000001f)
            throw new ArgumentException("IteratorFramework: pose body direction cannot be zero.", nameof(bodyDirection));
        BodyDirection = direction.normalized;
        LeftHandOffset = BodyValidation.Vector(leftHandOffset ?? new Vector2(-9f, -2f), nameof(leftHandOffset), 1000f);
        RightHandOffset = BodyValidation.Vector(rightHandOffset ?? new Vector2(9f, -2f), nameof(rightHandOffset), 1000f);
        LeftFootOffset = BodyValidation.Vector(leftFootOffset ?? new Vector2(-4f, -17f), nameof(leftFootOffset), 1000f);
        RightFootOffset = BodyValidation.Vector(rightFootOffset ?? new Vector2(4f, -17f), nameof(rightFootOffset), 1000f);
    }

    public string Name { get; }
    public Vector2 BodyDirection { get; }
    public Vector2 LeftHandOffset { get; }
    public Vector2 RightHandOffset { get; }
    public Vector2 LeftFootOffset { get; }
    public Vector2 RightFootOffset { get; }
    public override string ToString() => Name;
}
