using System;
using System.Globalization;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

internal readonly struct HeatColumnEmitterSample
{
    internal readonly Vector2 Start;
    internal readonly Vector2 End;
    internal readonly float Radius;
    internal readonly float Strength;
    internal readonly float Turbulence;

    internal HeatColumnEmitterSample(
        Vector2 start,
        Vector2 end,
        float radius,
        float strength,
        float turbulence)
    {
        Start = start;
        End = end;
        Radius = Mathf.Max(8f, radius);
        Strength = Mathf.Max(0f, strength);
        Turbulence = Mathf.Max(0f, turbulence);
    }
}

/// <summary>
/// Mapper-authored local thermal emitter. It never renders a sprite and never decides
/// whether weather is active; HeatWave samples it only while the weather is present.
/// FlowVector is a soft preferred path, not a rigid particle trajectory.
/// </summary>
internal sealed class HeatColumnData : PlacedObject.Data
{
    private const string VersionTag = "V1";
    private const int FieldCount = 6;

    public Vector2 FlowVector = new(24f, 220f);
    public float Radius = 72f;
    public float Strength = 1f;
    public float Turbulence = 0.85f;

    internal HeatColumnData(PlacedObject owner)
        : base(owner)
    {
    }

    public override void FromString(string s)
    {
        try
        {
            string[] parts = (s ?? string.Empty).Split('~');
            if (parts.Length < FieldCount || parts[0] != VersionTag)
            {
                return;
            }

            FlowVector = new Vector2(
                ParseFloat(parts[1], FlowVector.x),
                ParseFloat(parts[2], FlowVector.y));
            Radius = Mathf.Max(8f, ParseFloat(parts[3], Radius));
            Strength = Mathf.Max(0f, ParseFloat(parts[4], Strength));
            Turbulence = Mathf.Max(0f, ParseFloat(parts[5], Turbulence));
            unrecognizedAttributes = SaveUtils.PopulateUnrecognizedStringAttrs(parts, FieldCount);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed to parse HeatColumn data: {ex.Message}");
        }
    }

    public override string ToString()
    {
        string result = string.Join("~", new[]
        {
            VersionTag,
            FlowVector.x.ToString("0.###", CultureInfo.InvariantCulture),
            FlowVector.y.ToString("0.###", CultureInfo.InvariantCulture),
            Radius.ToString("0.###", CultureInfo.InvariantCulture),
            Strength.ToString("0.###", CultureInfo.InvariantCulture),
            Turbulence.ToString("0.###", CultureInfo.InvariantCulture)
        });

        result = SaveState.SetCustomData(this, result);
        return SaveUtils.AppendUnrecognizedStringAttrs(result, "~", unrecognizedAttributes);
    }

    private static float ParseFloat(string value, float fallback)
    {
        return float.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float parsed)
            ? parsed
            : fallback;
    }
}
