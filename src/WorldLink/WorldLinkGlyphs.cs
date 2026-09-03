using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DryCycle.WorldLink;

internal static class WorldLinkGlyphs
{
    private const string FallbackElement = "smallKarmaNoRing-1";
    private static readonly HashSet<string> MissingGlyphWarnings = new(StringComparer.Ordinal);

    internal static string ElementName(WorldLinkPortAddress address)
    {
        RegionGate.GateRequirement requirement = GateUnlockRequirements.Get(address);
        string candidate;
        if (requirement == null)
        {
            candidate = FallbackElement;
        }
        else if (int.TryParse(requirement.value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
        {
            candidate = "smallKarmaNoRing" + Mathf.Clamp(numeric - 1, -1, 4);
        }
        else
        {
            candidate = "smallKarmaNoRing" + requirement.value;
        }

        if (Futile.atlasManager.DoesContainElementWithName(candidate)) return candidate;
        if (MissingGlyphWarnings.Add(candidate))
        {
            Plugin.Logger?.LogWarning($"WorldLink: karma glyph atlas element '{candidate}' does not exist; using '{FallbackElement}'.");
        }
        return FallbackElement;
    }

    internal static FSprite Create(WorldLinkPortAddress address) => new(ElementName(address));

    internal static void Refresh(FSprite sprite, WorldLinkPortAddress address)
    {
        if (sprite == null) return;
        string expected = ElementName(address);
        if (sprite.element == null || sprite.element.name != expected)
        {
            sprite.element = Futile.atlasManager.GetElementWithName(expected);
        }
    }
}
