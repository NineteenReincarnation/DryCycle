using System;
using System.Text;

namespace DryCycle.DevUI.DevTool.Triggers;

/// <summary>
/// Persists editor-only trigger names inside Rain World's forward-compatible
/// EventTrigger.unrecognizedSaveStrings dictionary. Rain World already round-trips unknown trigger
/// attributes, so names travel with RoomSettings without patching gameplay classes or file formats.
/// </summary>
internal static class TriggerEditorMetadata
{
    private const string NameKey = "DryCycleName64";
    private const int MaxNameLength = 128;

    internal static string GetName(EventTrigger trigger)
    {
        if (trigger?.unrecognizedSaveStrings == null ||
            !trigger.unrecognizedSaveStrings.TryGetValue(NameKey, out string encoded) ||
            string.IsNullOrEmpty(encoded))
            return string.Empty;

        try
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            string value = Encoding.UTF8.GetString(bytes);
            return Normalize(value);
        }
        catch
        {
            // Corrupt/foreign metadata must never stop RoomSettings from loading.
            return string.Empty;
        }
    }

    internal static bool SetName(EventTrigger trigger, string value)
    {
        if (trigger?.unrecognizedSaveStrings == null)
            return false;

        string next = Normalize(value);
        string current = GetName(trigger);
        if (string.Equals(current, next, StringComparison.Ordinal))
            return false;

        if (next.Length == 0)
        {
            trigger.unrecognizedSaveStrings.Remove(NameKey);
            return true;
        }

        trigger.unrecognizedSaveStrings[NameKey] =
            Convert.ToBase64String(Encoding.UTF8.GetBytes(next));
        return true;
    }

    private static string Normalize(string value)
    {
        value = (value ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        return value.Length <= MaxNameLength
            ? value
            : value.Substring(0, MaxNameLength);
    }
}
