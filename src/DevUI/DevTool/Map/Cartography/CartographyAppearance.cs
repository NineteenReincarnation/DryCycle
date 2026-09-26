using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal enum CartographyRouteMode { Straight, HorizontalFirst, VerticalFirst, Manual }

// These records contain author choices only. Geometry, decoded images and font caches live elsewhere.
internal sealed class CartographyAppearance
{
    public string ParentId = "", Category = "", Subregion = "", Icon = "", Font = "", Image = "";
    public string From = "", To = "", LeftKarma = "1", RightKarma = "1", TargetRegion = "", Availability = "";
    public int FromPort = -1, ToPort = -1, WaterLevel = -2;
    public bool OverridePalette = false, Bold = false, Italic = false, Underline = false, Shade = true, DropShadow = false, Dashed = false;
    public bool BetterCutout = true, CutAllSolid, Shortcuts = true, Acid, Deathpit, WaterFront = true, WhiteRed = true;
    public uint Background = 0xFFC53D0F, Wall = 0xFF070707, Water = 0xFF0000FF, AcidColor = 0xFF89D43B;
    public uint ShadeColor = 0xFF000000, LeftColor = 0xFFFFFFFF, RightColor = 0xFFFFFFFF;
    public uint LeftArrow = 0xFFFFFFFF, RightArrow = 0xFFFFFFFF, Splitter = 0xFFFFFFFF;
    public float Opacity = 1, Outline = 3, Alignment = 0, Scale = 1;
    public CartographyRouteMode Route = CartographyRouteMode.Straight;
    internal CartographyAppearance Clone() => (CartographyAppearance)MemberwiseClone();
}

internal sealed class CartographyOptions
{
    public bool TileWalls = true, Borders = true, MarkShortcuts = true, ExitsOnly = true, ShortcutBackground = true;
    public bool Objects = true, Pickups, Slugcats, Diamonds = true, HollowDiamonds, SpecialRooms = true, InRoomShortcuts = true;
    public uint Canvas = 0x00000000, RoomNameColor = 0xFFFFFF00, Wall = 0xFF050505;
    public float WaterOpacity = .3f, BorderSize = 3;
    public string HiddenTypes = "DevToken", Campaign = "White", Installation = "", SpriteDirectory = "";
    public string PanKey = "Right", DeleteKey = "Delete", DuplicateKey = "Ctrl+D", CopyKey = "Ctrl+C", CutKey = "Ctrl+X", PasteKey = "Ctrl+V";
    public bool ExportArea;
    public float AreaX, AreaY, AreaWidth = 800, AreaHeight = 600;
    internal CartographyOptions Clone() => (CartographyOptions)MemberwiseClone();
}

internal sealed class CartographyPalette
{
    public string Name = "";
    public uint Background = 0xFFC53D0F, Water = 0xFF0000FF, Wall = 0xFF050505;
    public bool Visible = true;
    internal CartographyPalette Clone() => (CartographyPalette)MemberwiseClone();
}

internal sealed class CartographyPoint
{
    public float X, Y;
    internal CartographyPoint Clone() => (CartographyPoint)MemberwiseClone();
}

// A deliberately small scalar-record codec. It never serializes source objects or arbitrary types.
internal static class CartographyRecord
{
    internal static XElement Write(string name, object record) => new(name, Fields(record).Select(f =>
        new XAttribute(f.Name, Convert.ToString(f.GetValue(record), CultureInfo.InvariantCulture) ?? "")));
    internal static void Read(XElement element, object record)
    {
        if (element == null) return; // Version 1 receives explicit version 2 defaults.
        foreach (FieldInfo field in Fields(record))
        {
            string value = (string)element.Attribute(field.Name);
            if (value == null) continue;
            field.SetValue(record, field.FieldType.IsEnum ? Enum.Parse(field.FieldType, value) :
                Convert.ChangeType(value, field.FieldType, CultureInfo.InvariantCulture));
        }
    }
    internal static string Key(object record) => Write("r", record).ToString(SaveOptions.DisableFormatting);
    private static IEnumerable<FieldInfo> Fields(object record) => record.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
    internal static void Validate(object record)
    {
        foreach (FieldInfo field in Fields(record))
        {
            object value = field.GetValue(record);
            if (value is float f && (float.IsNaN(f) || float.IsInfinity(f) || Math.Abs(f) > 1000000))
                throw new InvalidOperationException("Invalid " + field.Name);
            if (field.FieldType.IsEnum && !Enum.IsDefined(field.FieldType, value)) throw new InvalidOperationException("Invalid " + field.Name);
            if (value is string s && s.Length > (field.Name == "Image" ? 16 * 1024 * 1024 : 4096)) throw new InvalidOperationException("Too long: " + field.Name);
        }
    }
}
