using System;

namespace DryCycle.Iterators;

/// <summary>可共享的感知配置。距离为房间像素，采样间隔为游戏更新帧。</summary>
public sealed class IteratorSensorProfile
{
    public static IteratorSensorProfile Default { get; } = new();

    public IteratorSensorProfile(int sampleInterval = 5, float observeRange = 600f, float retainRange = 650f,
        float nearRange = 120f, float nearExitRange = 150f, float targetSwitchMargin = 40f, bool requireVisibility = true)
    {
        if (sampleInterval < 1 || sampleInterval > 600) throw new ArgumentOutOfRangeException(nameof(sampleInterval));
        Range(observeRange, nameof(observeRange)); Range(retainRange, nameof(retainRange));
        Range(nearRange, nameof(nearRange)); Range(nearExitRange, nameof(nearExitRange)); Range(targetSwitchMargin, nameof(targetSwitchMargin));
        if (retainRange < observeRange || nearExitRange < nearRange) throw new ArgumentException("Exit ranges must be at least their entry ranges.");
        SampleInterval = sampleInterval; ObserveRange = observeRange; RetainRange = retainRange;
        NearRange = nearRange; NearExitRange = nearExitRange; TargetSwitchMargin = targetSwitchMargin; RequireVisibility = requireVisibility;
    }
    public int SampleInterval { get; }
    public float ObserveRange { get; }
    public float RetainRange { get; }
    public float NearRange { get; }
    public float NearExitRange { get; }
    public float TargetSwitchMargin { get; }
    public bool RequireVisibility { get; }
    private static void Range(float value, string name)
    {
        if (float.IsNaN(value) || value < 0f || value > 20000f) throw new ArgumentOutOfRangeException(name);
    }
}
