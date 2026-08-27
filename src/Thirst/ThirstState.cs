using UnityEngine;

namespace DryCycle.Thirst;

internal sealed class ThirstState
{
    // Water is stored in pip units. The upper bound is player-specific and is
    // enforced by ThirstStore from the player's maximum food-pip count.
    public float Water;
    public float LastWater;
    public bool IsDrinking;

    public float WaterValue => Water * ThirstConstants.WaterValuePerPip;
    public float LastWaterValue => LastWater * ThirstConstants.WaterValuePerPip;

    public void Set(float amount)
    {
        LastWater = Water;
        Water = Mathf.Max(0f, amount);
    }

    public void SetWaterValue(float amount)
    {
        Set(Mathf.Max(0f, amount) / ThirstConstants.WaterValuePerPip);
    }
}
