using UnityEngine;

namespace DryCycle.Thirst;

internal sealed class ThirstState
{
    public ThirstState()
    {
    }

    // Water remains stored in pip units for save compatibility and HUD logic.
    // WaterValue exposes the same state in DryCycle's internal WV scale.
    public float Water = ThirstConstants.MaxWater;
    public float LastWater = ThirstConstants.MaxWater;
    public bool IsDrinking;

    public float WaterValue => Water * ThirstConstants.WaterValuePerPip;
    public float LastWaterValue => LastWater * ThirstConstants.WaterValuePerPip;

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

    public void AddWaterValue(float amount)
    {
        if (amount <= 0f)
        {
            return;
        }

        Add(amount / ThirstConstants.WaterValuePerPip);
    }

    public void SetWaterValue(float amount)
    {
        Set(Mathf.Clamp(amount, 0f, ThirstConstants.MaxWaterValue) /
            ThirstConstants.WaterValuePerPip);
    }
}
