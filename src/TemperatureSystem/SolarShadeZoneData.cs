using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Serialized data for the unified local environment polygon.
/// The zone carries both local Shade [0,1] and Humidity [-1,1].
/// Vertices are stored as offsets from the owning PlacedObject position.
/// </summary>
internal sealed class SolarShadeZoneData : PlacedObject.Data
{
    private const string VersionTag = "V2";
    private const string LegacyVersionTag = "V1";
    private const int FieldCount = 4;
    private const int LegacyFieldCount = 3;
    private const int MinimumVertices = 3;

    private readonly List<Vector2> _vertices = new();

    internal float Shade = 0.5f;
    internal float Humidity = 0f;
    internal IReadOnlyList<Vector2> Vertices => _vertices;

    internal SolarShadeZoneData(PlacedObject owner)
        : base(owner)
    {
        ResetDefaultVertices();
    }

    internal void SetShade(float value)
    {
        Shade = RoomEnvironmentProfile.ClampUnit(value);
    }

    internal void SetHumidity(float value)
    {
        Humidity = RoomEnvironmentProfile.ClampSigned(value);
    }

    internal void SetDefaultsFromRoom(float roomShade, float roomHumidity)
    {
        SetShade(roomShade);
        SetHumidity(roomHumidity);
    }

    internal void SetVertex(int index, Vector2 value)
    {
        if (index < 0 || index >= _vertices.Count)
        {
            return;
        }

        _vertices[index] = value;
    }

    internal void InsertVertex(int index, Vector2 value)
    {
        index = Mathf.Clamp(index, 0, _vertices.Count);
        _vertices.Insert(index, value);
    }

    internal bool RemoveVertexAt(int index)
    {
        if (_vertices.Count <= MinimumVertices || index < 0 || index >= _vertices.Count)
        {
            return false;
        }

        _vertices.RemoveAt(index);
        return true;
    }

    public override void FromString(string s)
    {
        try
        {
            string[] parts = (s ?? string.Empty).Split('~');
            if (parts.Length < LegacyFieldCount)
            {
                return;
            }

            if (string.Equals(parts[0], VersionTag, StringComparison.Ordinal))
            {
                if (parts.Length < FieldCount)
                {
                    return;
                }

                SetShade(ParseFloat(parts[1], Shade));
                SetHumidity(ParseFloat(parts[2], Humidity));
                ReadVertices(parts[3]);
                unrecognizedAttributes = SaveUtils.PopulateUnrecognizedStringAttrs(parts, FieldCount);
                return;
            }

            if (string.Equals(parts[0], LegacyVersionTag, StringComparison.Ordinal))
            {
                // Existing Shade Zone saves remain valid. V1 had no humidity field,
                // so legacy zones retain neutral local humidity until edited.
                SetShade(ParseFloat(parts[1], Shade));
                SetHumidity(0f);
                ReadVertices(parts[2]);
                unrecognizedAttributes = SaveUtils.PopulateUnrecognizedStringAttrs(parts, LegacyFieldCount);
            }
        }
        catch (Exception ex)
        {
            global::DryCycle.Plugin.Logger?.LogWarning(
                $"Failed to parse DryCycle Environment Zone data: {ex.Message}");
            ResetDefaultVertices();
        }
    }

    public override string ToString()
    {
        string result = string.Join(
            "~",
            VersionTag,
            Shade.ToString("0.#####", CultureInfo.InvariantCulture),
            Humidity.ToString("0.#####", CultureInfo.InvariantCulture),
            SerializeVertices());

        result = SaveState.SetCustomData(this, result);
        return SaveUtils.AppendUnrecognizedStringAttrs(result, "~", unrecognizedAttributes);
    }

    private void ReadVertices(string serialized)
    {
        List<Vector2> parsed = new();
        if (!string.IsNullOrWhiteSpace(serialized))
        {
            string[] points = serialized.Split('|');
            for (int i = 0; i < points.Length; i++)
            {
                string[] xy = points[i].Split('^');
                if (xy.Length != 2 ||
                    !float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                {
                    continue;
                }

                parsed.Add(new Vector2(x, y));
            }
        }

        if (parsed.Count < MinimumVertices)
        {
            ResetDefaultVertices();
            return;
        }

        _vertices.Clear();
        _vertices.AddRange(parsed);
    }

    private string SerializeVertices()
    {
        string[] points = new string[_vertices.Count];
        for (int i = 0; i < _vertices.Count; i++)
        {
            Vector2 point = _vertices[i];
            points[i] = point.x.ToString("0.###", CultureInfo.InvariantCulture) + "^" +
                        point.y.ToString("0.###", CultureInfo.InvariantCulture);
        }

        return string.Join("|", points);
    }

    private void ResetDefaultVertices()
    {
        _vertices.Clear();
        _vertices.Add(new Vector2(-80f, -55f));
        _vertices.Add(new Vector2(80f, -55f));
        _vertices.Add(new Vector2(80f, 55f));
        _vertices.Add(new Vector2(-80f, 55f));
    }

    private static float ParseFloat(string value, float fallback)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) &&
               !float.IsNaN(parsed) &&
               !float.IsInfinity(parsed)
            ? parsed
            : fallback;
    }
}
