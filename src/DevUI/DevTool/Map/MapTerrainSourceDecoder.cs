using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map;

// Shared detached terrain decoder. No RoomSettings lifecycle or unrelated placed-object hooks.
internal static class MapTerrainSourceDecoder
{
    internal static List<PlacedObject> Parse(
        string settings, string roomName)
    {
        List<PlacedObject> result =
            new();

        foreach (string rawLine in (settings ?? string.Empty).Replace("\r", "").Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith(
                    "PlacedObjects:",
                    StringComparison.OrdinalIgnoreCase))
                continue;

            string payload =
                line.Substring(
                    line.IndexOf(':') + 1);

            foreach (string value in payload.Split(','))
            {
                string[] parts =
                    value.Trim().Split(
                        new[] { "><" },
                        StringSplitOptions.None);

                if (parts.Length < 3 ||
                    !TryCurvedTerrainType(
                        parts[0],
                        out PlacedObject.Type type) ||
                    !float.TryParse(
                        parts[1],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out float x) ||
                    !float.TryParse(
                        parts[2],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out float y))
                    continue;

                try
                {
                    PlacedObject placed =
                        new(
                            type,
                            null)
                        {
                            pos =
                                new Vector2(
                                    x,
                                    y)
                        };

                    // Cartography loads detached author data on its source worker. Own terrain
                    // must not rely on the gameplay PlacedObject hook being installed on that path.
                    if (string.Equals(type.value, "QuicksandZone", StringComparison.Ordinal))
                        placed.data = new DryCycle.TerrainExt.QuicksandZone.QuicksandZoneData(placed);

                    if (parts.Length > 3 &&
                        placed.data != null)
                    {
                        placed.data.FromString(
                            parts[3]);
                    }

                    result.Add(
                        placed);
                }
                catch (Exception error)
                {
                    global::DryCycle.Plugin.Logger?.LogWarning(
                        "Map terrain data failed for " + roomName + " / " + type.value + ": " + error);
                }
            }
        }

        return result;
    }

    private static bool TryCurvedTerrainType(
        string value,
        out PlacedObject.Type type)
    {
        value =
            value?.Trim() ??
            string.Empty;

        if (value.Equals(
                PlacedObject.Type.TerrainHandle.value,
                StringComparison.OrdinalIgnoreCase))
        {
            type =
                PlacedObject.Type.TerrainHandle;
            return true;
        }

        if (value.Equals(
                PlacedObject.Type.LocalTerrain.value,
                StringComparison.OrdinalIgnoreCase))
        {
            type =
                PlacedObject.Type.LocalTerrain;
            return true;
        }

        if (value.Equals(
                PlacedObject.Type.CurvedSlope.value,
                StringComparison.OrdinalIgnoreCase))
        {
            type =
                PlacedObject.Type.CurvedSlope;
            return true;
        }

        if (value.Equals(
                PlacedObject.Type.SuperSlope.value,
                StringComparison.OrdinalIgnoreCase))
        {
            type =
                PlacedObject.Type.SuperSlope;
            return true;
        }

        type = null;
        if (value.Equals("QuicksandZone", StringComparison.OrdinalIgnoreCase))
        {
            type = new PlacedObject.Type("QuicksandZone", register: false);
            return true;
        }
        return false;
    }

}
