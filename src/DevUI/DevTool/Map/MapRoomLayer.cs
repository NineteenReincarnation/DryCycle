using System;
using System.Globalization;

namespace DryCycle.DevUI.DevTool.Map;

/// <summary>
/// Room layers use the exact map-config index everywhere: 0, 1, 2. Labels, filters, edit commands
/// and output share this convention; no view may renumber or clamp a value just for display.
/// </summary>
public static class MapRoomLayer
{
    public const int Count = 3;
    public const int Default = 1;

    public static string Label(int layer) => "L" + layer.ToString(CultureInfo.InvariantCulture);

    internal static void Validate(int layer)
    {
        if (layer < 0 || layer >= Count)
            throw new ArgumentOutOfRangeException(nameof(layer), layer, "Room map layer must be 0, 1 or 2.");
    }
}
