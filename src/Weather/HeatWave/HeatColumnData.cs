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
    internal readonly float FlowSpeed;
    internal readonly float Expansion;
    internal readonly float Pulse;

    internal HeatColumnEmitterSample(
        Vector2 start,
        Vector2 end,
        float radius,
        float strength,
        float turbulence,
        float flowSpeed,
        float expansion,
        float pulse)
    {
        Start = start;
        End = end;
        Radius = Mathf.Clamp(radius, 16f, 360f);
        Strength = Mathf.Clamp(strength, 0f, 2.5f);
        Turbulence = Mathf.Clamp(turbulence, 0f, 2.5f);
        FlowSpeed = Mathf.Clamp(flowSpeed, 0.15f, 3f);
        Expansion = Mathf.Clamp(expansion, 0.35f, 2.6f);
        Pulse = Mathf.Clamp01(pulse);
    }
}

/// <summary>
/// Mapper-authored local thermal emitter. It never renders a sprite and never decides
/// whether weather is active; HeatWave samples it only while the weather is present.
/// FlowVector is a soft preferred path, not a rigid particle trajectory.
///
/// V2 separates visual reach from flow speed and exposes plume expansion/pulsation so
/// a tall slow column, compact violent vent or broad wavering thermal sheet can share
/// the same simulation contract. V1 rooms remain readable.
/// </summary>
internal sealed class HeatColumnData : PlacedObject.Data
{
    private const string VersionTagV2 = "V2";
    private const string VersionTagV1 = "V1";
    private const int V2FieldCount = 11;
    private const int V1FieldCount = 6;

    public Vector2 FlowVector = new(24f, 220f);
    public float Radius = 72f;
    public float Strength = 1f;
    public float Turbulence = 0.85f;
    public float FlowSpeed = 1f;
    public float Expansion = 1f;
    public float Pulse = 0.55f;
    public Vector2 PanelPos = new(36f, 80f);

    internal HeatColumnData(PlacedObject owner)
        : base(owner)
    {
    }

    public override void FromString(string s)
    {
        try
        {
            string[] parts = (s ?? string.Empty).Split('~');
            if (parts.Length >= V2FieldCount && parts[0] == VersionTagV2)
            {
                ReadV2(parts);
                return;
            }

            if (parts.Length >= V1FieldCount && parts[0] == VersionTagV1)
            {
                ReadV1(parts);
            }
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
            VersionTagV2,
            FlowVector.x.ToString("0.###", CultureInfo.InvariantCulture),
            FlowVector.y.ToString("0.###", CultureInfo.InvariantCulture),
            Radius.ToString("0.###", CultureInfo.InvariantCulture),
            Strength.ToString("0.###", CultureInfo.InvariantCulture),
            Turbulence.ToString("0.###", CultureInfo.InvariantCulture),
            FlowSpeed.ToString("0.###", CultureInfo.InvariantCulture),
            Expansion.ToString("0.###", CultureInfo.InvariantCulture),
            Pulse.ToString("0.###", CultureInfo.InvariantCulture),
            PanelPos.x.ToString("0.###", CultureInfo.InvariantCulture),
            PanelPos.y.ToString("0.###", CultureInfo.InvariantCulture)
        });

        result = SaveState.SetCustomData(this, result);
        return SaveUtils.AppendUnrecognizedStringAttrs(result, "~", unrecognizedAttributes);
    }

    private void ReadV2(string[] parts)
    {
        FlowVector = new Vector2(
            ParseFloat(parts[1], FlowVector.x),
            ParseFloat(parts[2], FlowVector.y));
        Radius = Mathf.Clamp(ParseFloat(parts[3], Radius), 16f, 360f);
        Strength = Mathf.Clamp(ParseFloat(parts[4], Strength), 0f, 2.5f);
        Turbulence = Mathf.Clamp(ParseFloat(parts[5], Turbulence), 0f, 2.5f);
        FlowSpeed = Mathf.Clamp(ParseFloat(parts[6], FlowSpeed), 0.15f, 3f);
        Expansion = Mathf.Clamp(ParseFloat(parts[7], Expansion), 0.35f, 2.6f);
        Pulse = Mathf.Clamp01(ParseFloat(parts[8], Pulse));
        PanelPos = new Vector2(
            ParseFloat(parts[9], PanelPos.x),
            ParseFloat(parts[10], PanelPos.y));
        unrecognizedAttributes = SaveUtils.PopulateUnrecognizedStringAttrs(parts, V2FieldCount);
    }

    private void ReadV1(string[] parts)
    {
        FlowVector = new Vector2(
            ParseFloat(parts[1], FlowVector.x),
            ParseFloat(parts[2], FlowVector.y));
        Radius = Mathf.Clamp(ParseFloat(parts[3], Radius), 16f, 360f);
        Strength = Mathf.Clamp(ParseFloat(parts[4], Strength), 0f, 2.5f);
        Turbulence = Mathf.Clamp(ParseFloat(parts[5], Turbulence), 0f, 2.5f);
        unrecognizedAttributes = SaveUtils.PopulateUnrecognizedStringAttrs(parts, V1FieldCount);
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
