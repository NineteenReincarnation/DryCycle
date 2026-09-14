using System;
using UnityEngine;

namespace DryCycle.Iterators;

internal static class BodyValidation
{
    internal static float Range(float value, float min, float max, string name)
    {
        if (float.IsNaN(value) || value < min || value > max)
            throw new ArgumentOutOfRangeException(name, value, $"IteratorFramework: {name} must be finite and in [{min}, {max}].");
        return value;
    }

    internal static Vector2 Vector(Vector2 value, string name, float limit = 1000000f)
    {
        if (float.IsNaN(value.x) || float.IsNaN(value.y) || value.x < -limit || value.x > limit || value.y < -limit || value.y > limit)
            throw new ArgumentOutOfRangeException(name, value, $"IteratorFramework: {name} needs finite components within +/-{limit}.");
        return value;
    }
}
