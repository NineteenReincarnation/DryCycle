using UnityEngine;

namespace DryCycle.Items.KarmaSpear;

/// <summary>
/// Broad physical response class for a creature struck by an active Karma Spear.
/// Mass is only the fallback. Known creatures with unusual body plans or signature
/// movement are explicitly overridden before the fallback is consulted.
/// </summary>
internal enum KarmicResponseClass
{
    Fragile,
    Light,
    Standard,
    Heavy,
    Colossal,
    Segmented,
    Anchored,
    Special
}

/// <summary>
/// Optional behavior override layered on top of the broad response class.
/// </summary>
internal enum KarmicSpecialResponse
{
    None,
    RedLizardCrisis,
    CyanLeapInterrupt,
    FlightInterrupt,
    MirosGaitInterrupt,
    RotDisruption,
    ColossalInterrupt,
    SegmentedDisruption,
    AnchoredSuppression
}

internal readonly struct KarmicTargetProfile
{
    internal readonly KarmicResponseClass ResponseClass;
    internal readonly KarmicSpecialResponse Special;
    internal readonly string TypeName;
    internal readonly float TotalMass;

    internal KarmicTargetProfile(
        KarmicResponseClass responseClass,
        KarmicSpecialResponse special,
        string typeName,
        float totalMass)
    {
        ResponseClass = responseClass;
        Special = special;
        TypeName = typeName;
        TotalMass = totalMass;
    }

    internal bool IsFragile => ResponseClass == KarmicResponseClass.Fragile;

    internal bool UsesBodyBinding =>
        ResponseClass == KarmicResponseClass.Light ||
        ResponseClass == KarmicResponseClass.Standard ||
        ResponseClass == KarmicResponseClass.Heavy;

    internal bool UsesDisruption => !IsFragile && !UsesBodyBinding;
}

/// <summary>
/// One authority for deciding how a creature reacts to Karma Spear.
///
/// Priority:
/// 1. body-plan / signature-creature overrides;
/// 2. dangerous movement overrides;
/// 3. explicit colossal overrides;
/// 4. TotalMass fallback for ordinary and unknown mod creatures.
///
/// Type names are compared through ExtEnum.value strings so DLC/Watcher creatures do not
/// become hard compile-time dependencies of this classifier.
/// </summary>
internal static class KarmicTargetClassifier
{
    internal static KarmicTargetProfile Classify(Creature creature)
    {
        string typeName = creature?.Template?.type?.value ?? string.Empty;
        float totalMass = creature == null ? 0f : Mathf.Max(0f, creature.TotalMass);

        // Fixed / burrowed / terrain-like creatures must never fall through to Fragile
        // merely because their realized BodyChunks are light.
        if (IsAny(typeName,
                "GarbageWorm",
                "TentaclePlant",
                "PoleMimic",
                "StowawayBug",
                "BigSandGrub"))
        {
            return Profile(
                KarmicResponseClass.Anchored,
                KarmicSpecialResponse.AnchoredSuppression,
                typeName,
                totalMass);
        }

        // Long multi-segment bodies need signal/motion disruption rather than a point leash.
        if (IsAny(typeName,
                "Centipede",
                "RedCentipede",
                "Centiwing",
                "SmallCentipede",
                "AquaCenti",
                "Millipede"))
        {
            return Profile(
                KarmicResponseClass.Segmented,
                KarmicSpecialResponse.SegmentedDisruption,
                typeName,
                totalMass);
        }

        // Vulture-family flight is interrupted rather than pinning a flying body to space.
        if (IsAny(typeName, "Vulture", "KingVulture", "MirosVulture"))
        {
            return Profile(
                KarmicResponseClass.Special,
                KarmicSpecialResponse.FlightInterrupt,
                typeName,
                totalMass);
        }

        if (typeName == "MirosBird")
        {
            return Profile(
                KarmicResponseClass.Special,
                KarmicSpecialResponse.MirosGaitInterrupt,
                typeName,
                totalMass);
        }

        // Rot creatures have many independently moving chunks/tentacles. Per-chunk anchors
        // are particularly dangerous here, so they receive a synchronization disruption.
        if (IsAny(typeName,
                "BrotherLongLegs",
                "DaddyLongLegs",
                "TerrorLongLegs",
                "HunterDaddy"))
        {
            return Profile(
                KarmicResponseClass.Special,
                KarmicSpecialResponse.RotDisruption,
                typeName,
                totalMass);
        }

        // Signature lizard actions override what raw body mass would imply.
        if (typeName == "RedLizard")
        {
            return Profile(
                KarmicResponseClass.Heavy,
                KarmicSpecialResponse.RedLizardCrisis,
                typeName,
                totalMass);
        }

        if (typeName == "CyanLizard")
        {
            return Profile(
                KarmicResponseClass.Standard,
                KarmicSpecialResponse.CyanLeapInterrupt,
                typeName,
                totalMass);
        }

        // These creatures are too massive or too structurally unusual for a spatial bind.
        // The spear denies their current action instead of pretending it can nail the whole
        // creature to one point in the room.
        if (IsAny(typeName,
                "BigEel",
                "Deer",
                "BigJelly",
                "SkyWhale",
                "Loach",
                "RotLoach"))
        {
            return Profile(
                KarmicResponseClass.Colossal,
                KarmicSpecialResponse.ColossalInterrupt,
                typeName,
                totalMass);
        }

        // Mass is intentionally last. This also gives unknown mod creatures a safe default.
        if (totalMass <= 0.30f)
        {
            return Profile(KarmicResponseClass.Fragile, KarmicSpecialResponse.None, typeName, totalMass);
        }

        if (totalMass <= 1.50f)
        {
            return Profile(KarmicResponseClass.Light, KarmicSpecialResponse.None, typeName, totalMass);
        }

        if (totalMass <= 4.50f)
        {
            return Profile(KarmicResponseClass.Standard, KarmicSpecialResponse.None, typeName, totalMass);
        }

        if (totalMass <= 15f)
        {
            return Profile(KarmicResponseClass.Heavy, KarmicSpecialResponse.None, typeName, totalMass);
        }

        return Profile(
            KarmicResponseClass.Colossal,
            KarmicSpecialResponse.ColossalInterrupt,
            typeName,
            totalMass);
    }

    private static KarmicTargetProfile Profile(
        KarmicResponseClass responseClass,
        KarmicSpecialResponse special,
        string typeName,
        float totalMass)
    {
        return new KarmicTargetProfile(responseClass, special, typeName, totalMass);
    }

    private static bool IsAny(string value, params string[] candidates)
    {
        for (int i = 0; i < candidates.Length; i++)
        {
            if (value == candidates[i])
            {
                return true;
            }
        }

        return false;
    }
}
