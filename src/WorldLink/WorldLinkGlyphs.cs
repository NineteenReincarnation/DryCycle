using System.Globalization;
using UnityEngine;

namespace DryCycle.WorldLink;

/// <summary>
/// One glyph naming policy for the in-room gate and HUD map. Keeping this centralized
/// also lets GateUnlockRequirements hot-reload without reconstructing drawable objects.
/// </summary>
internal static class WorldLinkGlyphs
{
    internal static string ElementName(WorldLinkPortAddress address)
    {
        RegionGate.GateRequirement requirement = GateUnlockRequirements.Get(address);
        if (requirement == null)
        {
            return "smallKarmaNoRing-1";
        }

        if (int.TryParse(requirement.value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
        {
            return "smallKarmaNoRing" + Mathf.Clamp(numeric - 1, -1, 4);
        }

        return "smallKarmaNoRing" + requirement.value;
    }

    internal static FSprite Create(WorldLinkPortAddress address) => new(ElementName(address));

    internal static void Refresh(FSprite sprite, WorldLinkPortAddress address)
    {
        if (sprite == null)
        {
            return;
        }

        string expected = ElementName(address);
        if (sprite.element == null || sprite.element.name != expected)
        {
            sprite.element = Futile.atlasManager.GetElementWithName(expected);
        }
    }
}
