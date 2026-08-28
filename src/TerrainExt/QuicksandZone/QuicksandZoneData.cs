using System;
using System.Collections.Generic;
using System.Globalization;
using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal sealed class QuicksandZoneData : PlacedObject.Data
{
    private const int SerializedFieldCount = 14;

    public BezierSpline SurfaceSpline;
    public BezierSpline BottomSpline;

    // Signed flow speed. Positive moves from SurfaceSpline A -> B, negative reverses it.
    public float FlowSpeed = 0.75f;
    public float FlowStrength = 0.55f;
    public float HorizontalDrag = 0.62f;
    public float SinkStrength = 0.18f;

    public QuicksandZoneData(PlacedObject owner)
        : base(owner)
    {
        SurfaceSpline = new BezierSpline(
            Vector2.zero,
            new Vector2(80f, 0f),
            new Vector2(240f, 0f),
            new Vector2(160f, 0f));

        BottomSpline = new BezierSpline(
            new Vector2(0f, -75f),
            new Vector2(80f, -75f),
            new Vector2(240f, -75f),
            new Vector2(160f, -75f));
    }

    public override void FromString(string s)
    {
        try
        {
            string[] parts = (s ?? string.Empty).Split('~');
            if (parts.Length < SerializedFieldCount)
            {
                return;
            }

            SurfaceSpline = ReadSpline(parts, 0);
            BottomSpline = ReadSpline(parts, 5);
            FlowSpeed = ParseFloat(parts[10], FlowSpeed);
            FlowStrength = Mathf.Max(0f, ParseFloat(parts[11], FlowStrength));
            HorizontalDrag = Mathf.Clamp01(ParseFloat(parts[12], HorizontalDrag));
            SinkStrength = Mathf.Max(0f, ParseFloat(parts[13], SinkStrength));
            unrecognizedAttributes = SaveUtils.PopulateUnrecognizedStringAttrs(parts, SerializedFieldCount);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed to parse QuicksandZone data: {ex.Message}");
        }
    }

    public override string ToString()
    {
        List<string> fields = new(SerializedFieldCount);
        WriteSpline(fields, SurfaceSpline);
        WriteSpline(fields, BottomSpline);
        fields.Add(FlowSpeed.ToString("0.###", CultureInfo.InvariantCulture));
        fields.Add(FlowStrength.ToString("0.###", CultureInfo.InvariantCulture));
        fields.Add(HorizontalDrag.ToString("0.###", CultureInfo.InvariantCulture));
        fields.Add(SinkStrength.ToString("0.###", CultureInfo.InvariantCulture));

        string result = string.Join("~", fields);
        result = SaveState.SetCustomData(this, result);
        return SaveUtils.AppendUnrecognizedStringAttrs(result, "~", unrecognizedAttributes);
    }

    private static void WriteSpline(List<string> fields, BezierSpline spline)
    {
        spline ??= new BezierSpline();
        fields.Add(SerializeVector(spline.posA));
        fields.Add(SerializeVector(spline.posB));
        fields.Add(SerializeVector(spline.handleA));
        fields.Add(SerializeVector(spline.handleB));
        fields.Add(string.Join("|", spline.midpoints));
    }

    private static BezierSpline ReadSpline(string[] parts, int start)
    {
        Vector2 posA = ParseVector(parts[start]);
        Vector2 posB = ParseVector(parts[start + 1]);
        Vector2 handleA = ParseVector(parts[start + 2]);
        Vector2 handleB = ParseVector(parts[start + 3]);
        List<BezierSpline.Midpoint> midpoints = new();

        string midpointText = parts[start + 4];
        if (!string.IsNullOrWhiteSpace(midpointText))
        {
            string[] midpointParts = midpointText.Split('|');
            for (int i = 0; i < midpointParts.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(midpointParts[i]))
                {
                    midpoints.Add(BezierSpline.Midpoint.FromString(midpointParts[i]));
                }
            }
        }

        return new BezierSpline(posA, handleA, posB, handleB, midpoints.ToArray());
    }

    private static string SerializeVector(Vector2 value)
    {
        return value.x.ToString("0.###", CultureInfo.InvariantCulture) + "^" +
               value.y.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static Vector2 ParseVector(string value)
    {
        string[] parts = (value ?? string.Empty).Split('^');
        if (parts.Length != 2)
        {
            return Vector2.zero;
        }

        return new Vector2(
            float.Parse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture),
            float.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    private static float ParseFloat(string value, float fallback)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : fallback;
    }
}
