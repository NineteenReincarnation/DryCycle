using System;
using System.Collections.Generic;

namespace DryCycle.DevUI.DevTool.Sound;

/// <summary>
/// Builds the immutable sound-library view consumed by the rebuilt frontend directly from the
/// headless filename/sample catalogues. DevInterface SoundPage is deliberately absent: native
/// resource discovery and presentation must remain usable even when no vanilla page is materialized.
/// </summary>
internal static class NativeSoundResourceSnapshot
{
    internal static EditorSoundSampleSnapshot[] Capture(string[] names)
    {
        names ??= Array.Empty<string>();
        if (names.Length == 0)
            return Array.Empty<EditorSoundSampleSnapshot>();

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<EditorSoundSampleSnapshot> entries = new(names.Length);
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                continue;

            entries.Add(SoundSampleCatalog.Resolve(name));
        }

        entries.Sort(Compare);
        return entries.ToArray();
    }

    private static int Compare(EditorSoundSampleSnapshot left, EditorSoundSampleSnapshot right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left == null) return 1;
        if (right == null) return -1;

        int kind = left.SourceKind.CompareTo(right.SourceKind);
        if (kind != 0) return kind;

        int source = string.Compare(left.SourceName, right.SourceName, StringComparison.OrdinalIgnoreCase);
        return source != 0
            ? source
            : string.Compare(left.Sample, right.Sample, StringComparison.OrdinalIgnoreCase);
    }
}
