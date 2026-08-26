using UnityEngine;

namespace DryCycle.Thirst;

internal sealed class ThirstState
{
    public float Water = ThirstConstants.MaxWater;
    public float LastWater = ThirstConstants.MaxWater;
    public bool IsDrinking;

    public void Add(float amount)
    {
        if (amount <= 0f)
        {
            return;
        }

        LastWater = Water;
        Water = Mathf.Clamp(Water + amount, 0f, ThirstConstants.MaxWater);
    }

    public void Set(float amount)
    {
        LastWater = Water;
        Water = Mathf.Clamp(amount, 0f, ThirstConstants.MaxWater);
    }
}
