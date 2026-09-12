using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// Warp 1.9.x uses a closed RoomInfo.RoomType enum. Unknown world tags therefore
// fall back to Room, and simply assigning a new numeric enum value would make its
// color/name arrays index out of range. This soft integration adds one runtime
// category without taking a compile-time dependency on Warp.
internal static class DB_WarpCompatibility
{
    private const int DesertRoomTypeValue = 6;
    private const float EnglishDesertGroupOffset = -48f;
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

        Type roomFinderType = DB_RuntimePatch.FindType("RoomFinder");
        roomInfoType = DB_RuntimePatch.FindType("RoomInfo");
        colorInfoType = DB_RuntimePatch.FindType("ColorInfo");
        warpMenuType = DB_RuntimePatch.FindType("WarpModMenu");
        roomTypeEnum = roomInfoType?.GetNestedType("RoomType", BindingFlags.Public | BindingFlags.NonPublic);
        if (roomFinderType == null || roomInfoType == null || colorInfoType == null || warpMenuType == null || roomTypeEnum == null)
            return; // Warp is not installed (or is an incompatible future rewrite).

        harmony = DB_RuntimePatch.Create("Anno.DesertBatfly.Warp");
        if (harmony == null || !EnsureWarpTypeColors()) return;

        MethodInfo enumGetNames = typeof(Enum).GetMethod(
            nameof(Enum.GetNames), BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Type) }, null);
        MethodInfo enumPrefix = typeof(DB_WarpCompatibility).GetMethod(
            nameof(EnumGetNamesPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (!DB_RuntimePatch.Patch(harmony, enumGetNames, enumPrefix))
        {
            ShrinkWarpTypeColors();
            DB_RuntimePatch.UnpatchSelf(harmony);
            harmony = null;
            return;
        }

        MethodInfo parse = roomFinderType.GetMethod(
            "ParseWorldFile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo parsePostfix = typeof(DB_WarpCompatibility).GetMethod(
            nameof(ParseWorldFilePostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (!DB_RuntimePatch.Patch(harmony, parse, null, parsePostfix))
        {
            ShrinkWarpTypeColors();
            DB_RuntimePatch.UnpatchSelf(harmony);
            harmony = null;
            return;
        }

        MethodInfo colorLoad = colorInfoType.GetMethod(
            "Load", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo colorPrefix = typeof(DB_WarpCompatibility).GetMethod(
            nameof(ColorLoadPrefix), BindingFlags.NonPublic | BindingFlags.Static);

        // WarpContainer is top-level in Warp 1.9.x and nested in newer builds.
        // Resolve both forms so the integration stays soft across Warp revisions.
        Type warpContainerType = DB_RuntimePatch.FindType("WarpContainer")
            ?? warpMenuType.GetNestedType("WarpContainer", BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo generate = warpContainerType?.GetMethod(
            "GenerateRoomButtons", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo generatePrefix = typeof(DB_WarpCompatibility).GetMethod(
            nameof(GenerateRoomButtonsPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo generatePostfix = typeof(DB_WarpCompatibility).GetMethod(
            nameof(GenerateRoomButtonsPostfix), BindingFlags.NonPublic | BindingFlags.Static);

        DB_RuntimePatch.Patch(harmony, colorLoad, colorPrefix);
        DB_RuntimePatch.Patch(harmony, generate, generatePrefix, generatePostfix);
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled && harmony == null) return;

        // Cached RoomInfo objects can outlive this mod's hooks inside Warp. Put
        // them back into Warp's ordinary Room bucket before removing the Enum patch.
        NormalizeCachedRooms();
        ShrinkWarpTypeColors();
        DB_RuntimePatch.UnpatchSelf(harmony);

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
        string desertName = IsChineseLanguage() ? "沙漠蝙蝠房" : "Desert Swarmroom";
        if (names.Count == DesertRoomTypeValue) names.Add(desertName);
        else names[DesertRoomTypeValue] = desertName;
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

    private static void GenerateRoomButtonsPostfix(object __instance, object[] __args)
    {
        // "DESERT SWARMROOM" is considerably wider than Warp's stock type headers.
        // In English only, move the whole Desert group left so the header no longer
        // collides with the adjacent OUTPOST/TRADER groups while keeping its buttons
        // centered beneath the header. Chinese uses the shorter localized label and
        // therefore keeps Warp's original spacing.
        if (__instance == null || !IsEnglishLanguage() || __args == null || __args.Length == 0 ||
            __args[0] is not IEnumerable rooms || roomInfoType == null)
            return;

        FieldInfo nameField = FindField(roomInfoType, "name");
        FieldInfo typeField = FindField(roomInfoType, "type");
        if (nameField == null || typeField == null) return;

        var desertRoomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (object room in rooms)
        {
            if (room == null || !roomInfoType.IsInstanceOfType(room)) continue;
            object type = typeField.GetValue(room);
            if (type == null || Convert.ToInt32(type) != DesertRoomTypeValue) continue;
            if (nameField.GetValue(room) is string name && !string.IsNullOrEmpty(name))
                desertRoomNames.Add(name);
        }
        if (desertRoomNames.Count == 0) return;

        object categoryLabels = GetMemberValue(__instance, "categoryLabels");
        if (categoryLabels is IEnumerable labels)
        {
            foreach (object label in labels)
            {
                if (!string.Equals(GetMenuLabelText(label), "DESERT SWARMROOM", StringComparison.OrdinalIgnoreCase))
                    continue;
                ShiftMenuObject(label, EnglishDesertGroupOffset);
                break;
            }
        }

        object roomButtons = GetMemberValue(__instance, "roomButtons");
        if (roomButtons is not IEnumerable buttons) return;
        foreach (object button in buttons)
        {
            if (GetMemberValue(button, "signalText") is not string signal ||
                !signal.EndsWith("warp", StringComparison.OrdinalIgnoreCase))
                continue;

            string roomName = signal.Substring(0, signal.Length - 4);
            if (desertRoomNames.Contains(roomName))
                ShiftMenuObject(button, EnglishDesertGroupOffset);
        }
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

    private static bool IsChineseLanguage()
    {
        try
        {
            InGameTranslator.LanguageID language = RWCustom.Custom.rainWorld?.inGameTranslator?.currentLanguage;
            return language == InGameTranslator.LanguageID.Chinese ||
                   language == InGameTranslator.LanguageID.TraditionalChinese;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEnglishLanguage()
    {
        try
        {
            return RWCustom.Custom.rainWorld?.inGameTranslator?.currentLanguage == InGameTranslator.LanguageID.English;
        }
        catch
        {
            return false;
        }
    }

    private static string GetMenuLabelText(object menuLabel)
    {
        object label = GetMemberValue(menuLabel, "label");
        return GetMemberValue(label, "text") as string;
    }

    private static object GetMemberValue(object instance, string name)
    {
        if (instance == null || string.IsNullOrEmpty(name)) return null;
        Type type = instance.GetType();
        while (type != null)
        {
            FieldInfo field = type.GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(instance);

            PropertyInfo property = type.GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property?.CanRead == true) return property.GetValue(instance, null);
            type = type.BaseType;
        }
        return null;
    }

    private static FieldInfo FindField(Type type, string name)
    {
        while (type != null)
        {
            FieldInfo field = type.GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field;
            type = type.BaseType;
        }
        return null;
    }

    private static void ShiftMenuObject(object menuObject, float deltaX)
    {
        if (menuObject == null || Math.Abs(deltaX) < 0.001f) return;
        ShiftVectorField(menuObject, "pos", deltaX);
        ShiftVectorField(menuObject, "lastPos", deltaX);
    }

    private static void ShiftVectorField(object instance, string fieldName, float deltaX)
    {
        FieldInfo field = FindField(instance.GetType(), fieldName);
        if (field?.GetValue(instance) is not Vector2 value) return;
        value.x += deltaX;
        field.SetValue(instance, value);
    }
}
