using System;

namespace DryCycle.Registration;

/// <summary>
/// Definition used by DryCycle's item registry. Custom AbstractPhysicalObject
/// subclasses still own their Realize implementation; the registry supplies save
/// parsing and resource lifecycle in one shared place.
/// </summary>
internal abstract class ItemDefinition
{
    protected ItemDefinition(AbstractPhysicalObject.AbstractObjectType type)
    {
        Type = type;
    }

    internal AbstractPhysicalObject.AbstractObjectType Type { get; }

    internal abstract AbstractPhysicalObject Parse(World world, ItemSaveData saveData);

    internal virtual void LoadResources(RainWorld rainWorld)
    {
    }
}

internal readonly struct ItemSaveData
{
    internal ItemSaveData(
        EntityID id,
        WorldCoordinate position,
        string customData,
        int rippleLayer,
        string[] rawFields)
    {
        ID = id;
        Position = position;
        CustomData = customData;
        RippleLayer = rippleLayer;
        RawFields = rawFields;
    }

    internal EntityID ID { get; }

    internal WorldCoordinate Position { get; }

    internal string CustomData { get; }

    internal int RippleLayer { get; }

    internal string[] RawFields { get; }

    internal static bool TryGetType(
        string serialized,
        out AbstractPhysicalObject.AbstractObjectType type)
    {
        type = null;
        if (string.IsNullOrEmpty(serialized))
        {
            return false;
        }

        string[] fields = serialized.Split(new[] { "<oA>" }, StringSplitOptions.None);
        if (fields.Length < 2 || string.IsNullOrEmpty(fields[1]))
        {
            return false;
        }

        type = new AbstractPhysicalObject.AbstractObjectType(fields[1]);
        return true;
    }

    internal static ItemSaveData Parse(string serialized)
    {
        string[] fields = serialized.Split(new[] { "<oA>" }, StringSplitOptions.None);
        if (fields.Length < 3)
        {
            throw new FormatException("Custom item save data does not contain the required fields.");
        }

        string idField = fields[0];
        int rippleLayer = 0;
        const string RippleSeparator = "<oB>";
        int separatorIndex = idField.IndexOf(RippleSeparator, StringComparison.Ordinal);
        if (separatorIndex >= 0)
        {
            string rippleText = idField.Substring(separatorIndex + RippleSeparator.Length);
            idField = idField.Substring(0, separatorIndex);
            int.TryParse(rippleText, out rippleLayer);
        }

        EntityID id = EntityID.FromString(idField);
        WorldCoordinate position = WorldCoordinate.FromString(fields[2]);
        string customData = fields.Length > 3 ? fields[3] : string.Empty;

        return new ItemSaveData(id, position, customData, rippleLayer, fields);
    }
}
