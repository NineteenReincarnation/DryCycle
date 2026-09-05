using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace DryCycle.Creatures.DesertBatfly;

// Warp 1.9.x uses a closed RoomInfo.RoomType enum. Unknown world tags therefore
// fall back to Room, and simply assigning a new numeric enum value would make its
// color/name arrays index out of range. This soft integration adds one runtime
// category without taking a compile-time dependency on Warp.
internal static class DesertBatflyWarpCompatibility
{
    private const int DesertRoomTypeValue = 6;
    private static bool enabled;
    private static object harmony;
    private static Type roomInfoType;
    private static Type roomTypeEnum;
    private static Type colorInfoType;
    private static Type warpMenuType;

    internal static void Enable()
    {
        if (enabled)
        {
            EnsureWarpTypeColors();
            return;
        }

        Type roomFinderType = DesertBatflyRuntimePatch.FindType("RoomFinder");
        roomInfoType = DesertBatflyRuntimePatch.FindType("RoomInfo");
        colorInfoType = DesertBatflyRuntimePatch.FindType("ColorInfo");
        warpMenuType = DesertBatflyRuntimePatch.FindType("WarpModMenu");
        roomTypeEnum = roomInfoType?.GetNestedType("RoomType", BindingFlags.Public | BindingFlags.NonPublic);
        if (roomFinderType == null || roomInfoType == null || colorInfoType == null || warpMenuType == null || roomTypeEnum == null)
            return; // Warp is not installed (or is an incompatible future rewrite).

        harmony = DesertBatflyRuntimePatch.Create("Anno.DesertBatfly.Warp");
        if (harmony == null || !EnsureWarpTypeColors()) return;

        MethodInfo enumGetNames = typeof(Enum).GetMethod(
            nameof(Enum.GetNames), BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Type) }, null);
        MethodInfo enumPrefix = typeof(DesertBatflyWarpCompatibility).GetMethod(
            nameof(EnumGetNamesPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (!DesertBatflyRuntimePatch.Patch(harmony, enumGetNames, enumPrefix))
        {
            ShrinkWarpTypeColors();
            DesertBatflyRuntimePatch.UnpatchSelf(harmony);
            harmony = null;
            return;
        }

        MethodInfo parse = roomFinderType.GetMethod(
            "ParseWorldFile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo parsePostfix = typeof(DesertBatflyWarpCompatibility).GetMethod(
            nameof(ParseWorldFilePostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (!DesertBatflyRuntimePatch.Patch(harmony, parse, null, parsePostfix))
        {
            ShrinkWarpTypeColors();
            DesertBatflyRuntimePatch.UnpatchSelf(harmony);
            harmony = null;
            return;
        }

        MethodInfo colorLoad = colorInfoType.GetMethod(
            "Load", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo colorPrefix = typeof(DesertBatflyWarpCompatibility).GetMethod(
            nameof(ColorLoadPrefix), BindingFlags.NonPublic | BindingFlags.Static);

        // WarpContainer is a top-level class in Warp 1.9.x, not a nested type of
        // WarpModMenu. This prefix is only a safety net; colors are already expanded
        // during Enable and before ColorInfo.Load.
        Type warpContainerType = DesertBatflyRuntimePatch.FindType("WarpContainer");
        MethodInfo generate = warpContainerType?.GetMethod(
            "GenerateRoomButtons", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo generatePrefix = typeof(DesertBatflyWarpCompatibility).GetMethod(
            nameof(GenerateRoomButtonsPrefix), BindingFlags.NonPublic | BindingFlags.Static);

        DesertBatflyRuntimePatch.Patch(harmony, colorLoad, colorPrefix);
        DesertBatflyRuntimePatch.Patch(harmony, generate, generatePrefix);
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled && harmony == null) return;

        // Cached RoomInfo objects can outlive this mod's hooks inside Warp. Put
        // them back into Warp's ordinary Room bucket before removing the Enum patch.
        NormalizeCachedRooms();
        ShrinkWarpTypeColors();
        DesertBatflyRuntimePatch.UnpatchSelf(harmony);

        harmony = null;
        enabled = false;
        roomInfoType = null;
        roomTypeEnum = null;
        colorInfoType = null;
        warpMenuType = null;
    }

    private static bool EnumGetNamesPrefix(Type enumType, ref string[] __result)
    {
        if (enumType != roomTypeEnum) return true;

        List<(long value, string name)> values = new();
        foreach (FieldInfo field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            object raw = field.GetRawConstantValue();
            if (raw == null) continue;
            values.Add((Convert.ToInt64(raw), field.Name));
        }
        values.Sort((a, b) => a.value.CompareTo(b.value));

        var names = values.Select(entry => entry.name).ToList();
        while (names.Count < DesertRoomTypeValue) names.Add("Room" + names.Count);
        if (names.Count == DesertRoomTypeValue) names.Add("Desert Swarmroom");
        else names[DesertRoomTypeValue] = "Desert Swarmroom";
        __result = names.ToArray();
        return false;
    }

    private static void ColorLoadPrefix()
    {
        EnsureWarpTypeColors();
    }

    private static void GenerateRoomButtonsPrefix()
    {
        EnsureWarpTypeColors();
    }

    private static void ParseWorldFilePostfix(object __result, string path)
    {
        if (__result is not IEnumerable rooms || string.IsNullOrEmpty(path) || !File.Exists(path) || roomTypeEnum == null)
            return;

        var desertRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sourceLine in File.ReadAllLines(path))
        {
            string line = sourceLine?.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("//", StringComparison.Ordinal)) continue;
            string[] parts = line.Split(new[] { " : " }, StringSplitOptions.None);
            if (parts.Length < 3 || parts[2].IndexOf("DESERTSWARMROOM", StringComparison.OrdinalIgnoreCase) < 0) continue;

            string roomName = parts[0].Trim();
            int conditionalEnd = Math.Max(roomName.LastIndexOf('}'), roomName.LastIndexOf(')'));
            if (conditionalEnd >= 0 && conditionalEnd + 1 < roomName.Length)
                roomName = roomName.Substring(conditionalEnd + 1).Trim();
            if (!string.IsNullOrEmpty(roomName)) desertRooms.Add(roomName);
        }
        if (desertRooms.Count == 0) return;

        FieldInfo nameField = roomInfoType.GetField("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        FieldInfo typeField = roomInfoType.GetField("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (nameField == null || typeField == null) return;
        object desertType = Enum.ToObject(roomTypeEnum, DesertRoomTypeValue);

        foreach (object room in rooms)
        {
            if (room == null || room.GetType() != roomInfoType) continue;
            if (nameField.GetValue(room) is string name && desertRooms.Contains(name))
                typeField.SetValue(room, desertType);
        }
    }

    private static bool EnsureWarpTypeColors()
    {
        if (colorInfoType == null) return false;
        FieldInfo field = colorInfoType.GetField("typeColors", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (field?.GetValue(null) is not Array current || current.Length < 1) return false;
        if (current.Length > DesertRoomTypeValue) return true;

        Type elementType = field.FieldType.GetElementType();
        if (elementType == null) return false;
        Array expanded = Array.CreateInstance(elementType, DesertRoomTypeValue + 1);
        Array.Copy(current, expanded, current.Length);
        // HSLColor is a Rain World value type; build the sand-brown category color
        // without referencing Warp's assembly.
        expanded.SetValue(new HSLColor(0.075f, 0.62f, 0.55f), DesertRoomTypeValue);
        field.SetValue(null, expanded);
        return true;
    }

    private static void NormalizeCachedRooms()
    {
        if (warpMenuType == null || roomInfoType == null || roomTypeEnum == null) return;
        FieldInfo masterField = warpMenuType.GetField("masterRoomList", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (masterField?.GetValue(null) is not IDictionary dictionary) return;
        FieldInfo typeField = roomInfoType.GetField("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (typeField == null) return;
        object ordinaryRoom = Enum.ToObject(roomTypeEnum, 0);

        foreach (DictionaryEntry region in dictionary)
        {
            if (region.Value is not IEnumerable rooms) continue;
            foreach (object room in rooms)
            {
                if (room == null || room.GetType() != roomInfoType) continue;
                object type = typeField.GetValue(room);
                if (type != null && Convert.ToInt32(type) == DesertRoomTypeValue)
                    typeField.SetValue(room, ordinaryRoom);
            }
        }
    }

    private static void ShrinkWarpTypeColors()
    {
        if (colorInfoType == null) return;
        FieldInfo field = colorInfoType.GetField("typeColors", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (field?.GetValue(null) is not Array current || current.Length <= DesertRoomTypeValue) return;
        Type elementType = field.FieldType.GetElementType();
        if (elementType == null) return;
        Array original = Array.CreateInstance(elementType, DesertRoomTypeValue);
        Array.Copy(current, original, DesertRoomTypeValue);
        field.SetValue(null, original);
    }
}
