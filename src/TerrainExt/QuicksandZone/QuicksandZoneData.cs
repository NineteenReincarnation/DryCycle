using System;
using System.Collections.Generic;
using System.Globalization;
using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal sealed class QuicksandZoneData : PlacedObject.Data
{
    private const string VersionTag = "V2";
    private const int V2FieldCount = 12;
    private const int LegacyFieldCount = 14;

    private readonly List<float> _materialBoundaries = new();

    public BezierSpline SurfaceSpline;
    public float BottomDepth = 100f;

    // Visual flow tuning is independent from the TerrainCurve render pipeline.
    public float FlowSpeed = 0.75f;
    public float FlowStrength = 0.55f;
    public float HorizontalDrag = 0.62f;
    public float SinkStrength = 0.18f;

    internal IReadOnlyList<float> MaterialBoundaries => _materialBoundaries;

    public QuicksandZoneData(PlacedObject owner)
        : base(owner)
    {
        SurfaceSpline = new BezierSpline(
            Vector2.zero,
            new Vector2(0f, 100f),
            new Vector2(180f, 0f),
            new Vector2(180f, -100f));

        // New objects start with one obvious quicksand section in the middle.
        _materialBoundaries.Add(0.30f);
        _materialBoundaries.Add(0.70f);
    }

    internal bool IsQuicksand(float u)
    {
        u = Mathf.Clamp01(u);
        bool quicksand = false;
        for (int i = 0; i < _materialBoundaries.Count; i++)
        {
            if (u + 0.00001f < _materialBoundaries[i])
            {
                break;
            }

            quicksand = !quicksand;
        }

        return quicksand;
    }

    internal bool TryGetQuicksandInterval(float u, out float startU, out float endU)
    {
        u = Mathf.Clamp01(u);
        bool quicksand = false;
        float sectionStart = 0f;

        for (int i = 0; i < _materialBoundaries.Count; i++)
        {
            float boundary = _materialBoundaries[i];
            if (u < boundary - 0.00001f)
            {
                if (quicksand)
                {
                    startU = sectionStart;
                    endU = boundary;
                    return true;
                }

                break;
            }

            quicksand = !quicksand;
            sectionStart = boundary;
        }

        if (quicksand)
        {
            startU = sectionStart;
            endU = 1f;
            return true;
        }

        startU = 0f;
        endU = 0f;
        return false;
    }

    internal void FillQuicksandIntervals(List<Vector2> intervals)
    {
        intervals.Clear();
        bool quicksand = false;
        float sectionStart = 0f;

        for (int i = 0; i < _materialBoundaries.Count; i++)
        {
            float boundary = _materialBoundaries[i];
            if (quicksand && boundary > sectionStart + 0.0001f)
            {
                intervals.Add(new Vector2(sectionStart, boundary));
            }

            quicksand = !quicksand;
            sectionStart = boundary;
        }

        if (quicksand && sectionStart < 0.9999f)
        {
            intervals.Add(new Vector2(sectionStart, 1f));
        }
    }

    internal void SetMaterialBoundaries(IEnumerable<float> values)
    {
        _materialBoundaries.Clear();
        if (values != null)
        {
            foreach (float raw in values)
            {
                _materialBoundaries.Add(Mathf.Clamp01(raw));
            }
        }

        _materialBoundaries.Sort();
        for (int i = _materialBoundaries.Count - 1; i > 0; i--)
        {
            if (Mathf.Abs(_materialBoundaries[i] - _materialBoundaries[i - 1]) < 0.001f)
            {
                _materialBoundaries.RemoveAt(i);
            }
        }
    }

    public override void FromString(string s)
    {
        try
        {
            string[] parts = (s ?? string.Empty).Split('~');
            if (parts.Length > 0 && parts[0] == VersionTag)
            {
                ReadV2(parts);
                return;
            }

            ReadLegacy(parts);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed to parse QuicksandZone data: {ex.Message}");
        }
    }

    private void ReadV2(string[] parts)
    {
        if (parts.Length < V2FieldCount)
        {
            return;
        }

        BottomDepth = Mathf.Max(20f, ParseFloat(parts[1], BottomDepth));
        FlowSpeed = ParseFloat(parts[2], FlowSpeed);
        FlowStrength = Mathf.Max(0f, ParseFloat(parts[3], FlowStrength));
        HorizontalDrag = Mathf.Clamp01(ParseFloat(parts[4], HorizontalDrag));
        SinkStrength = Mathf.Max(0f, ParseFloat(parts[5], SinkStrength));
        SurfaceSpline = ReadSpline(parts, 6);
        SetMaterialBoundaries(ParseBoundaries(parts[11]));
        unrecognizedAttributes = SaveUtils.PopulateUnrecognizedStringAttrs(parts, V2FieldCount);
    }

    private void ReadLegacy(string[] parts)
    {
        if (parts.Length < LegacyFieldCount)
        {
            return;
        }

        SurfaceSpline = ReadSpline(parts, 0);
        BezierSpline legacyBottom = ReadSpline(parts, 5);
        FlowSpeed = ParseFloat(parts[10], FlowSpeed);
        FlowStrength = Mathf.Max(0f, ParseFloat(parts[11], FlowStrength));
        HorizontalDrag = Mathf.Clamp01(ParseFloat(parts[12], HorizontalDrag));
        SinkStrength = Mathf.Max(0f, ParseFloat(parts[13], SinkStrength));

        float surfaceY = (SurfaceSpline.posA.y + SurfaceSpline.posB.y) * 0.5f;
        float bottomY = (legacyBottom.posA.y + legacyBottom.posB.y) * 0.5f;
        BottomDepth = Mathf.Max(20f, surfaceY - bottomY);

        // Old QuicksandZone objects were entirely quicksand. Preserve that behaviour.
        SetMaterialBoundaries(new[] { 0f, 1f });
        unrecognizedAttributes = SaveUtils.PopulateUnrecognizedStringAttrs(parts, LegacyFieldCount);
    }

    public override string ToString()
    {
        List<string> fields = new(V2FieldCount)
        {
            VersionTag,
            BottomDepth.ToString("0.###", CultureInfo.InvariantCulture),
            FlowSpeed.ToString("0.###", CultureInfo.InvariantCulture),
            FlowStrength.ToString("0.###", CultureInfo.InvariantCulture),
            HorizontalDrag.ToString("0.###", CultureInfo.InvariantCulture),
            SinkStrength.ToString("0.###", CultureInfo.InvariantCulture)
        };

        WriteSpline(fields, SurfaceSpline);
        fields.Add(SerializeBoundaries());

        string result = string.Join("~", fields);
        result = SaveState.SetCustomData(this, result);
        return SaveUtils.AppendUnrecognizedStringAttrs(result, "~", unrecognizedAttributes);
    }

    private string SerializeBoundaries()
    {
        if (_materialBoundaries.Count == 0)
        {
            return string.Empty;
        }

        string[] values = new string[_materialBoundaries.Count];
        for (int i = 0; i < _materialBoundaries.Count; i++)
        {
            values[i] = _materialBoundaries[i].ToString("0.#####", CultureInfo.InvariantCulture);
        }

        return string.Join("|", values);
    }

    private static IEnumerable<float> ParseBoundaries(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        string[] parts = value.Split('|');
        for (int i = 0; i < parts.Length; i++)
        {
            if (float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            {
                yield return parsed;
            }
        }
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
