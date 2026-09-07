using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Narrow Task13 bridge into already-existing DesertBatfly decision points.
/// It does not implement a second combat, roost or locomotion state machine.
/// </summary>
internal static class DesertBatflyEnvironmentalIntegration
{
    private const float SandstormBlockerRadius = 120f;
    private const float SandstormReturningBatRadius = 340f;

    private delegate bool AggressiveOrig(DesertBatflyPersonality self);
    private delegate bool AggressiveDetour(AggressiveOrig orig, DesertBatflyPersonality self);
    private delegate float RoostChanceOrig(DesertBatflyPersonality self);
    private delegate float RoostChanceDetour(RoostChanceOrig orig, DesertBatflyPersonality self);
    private delegate int RoostDurationOrig(DesertBatflyPersonality self);
    private delegate int RoostDurationDetour(RoostDurationOrig orig, DesertBatflyPersonality self);
    private delegate bool CanHarassOrig(DesertBatflyAI self, Creature creature);
    private delegate bool CanHarassDetour(CanHarassOrig orig, DesertBatflyAI self, Creature creature);
    private delegate void ScanCreaturesOrig(DesertBatflyAI self);
    private delegate void ScanCreaturesDetour(ScanCreaturesOrig orig, DesertBatflyAI self);
    private delegate void SteerOrig(DesertBatflyAI self, Vector2 goal, float speed);
    private delegate void SteerDetour(SteerOrig orig, DesertBatflyAI self, Vector2 goal, float speed);

    private sealed class BatRef
    {
        internal DesertBatfly Bat;
    }

    private static Hook aggressiveHook;
    private static Hook roostChanceHook;
    private static Hook roostDurationHook;
    private static Hook canHarassHook;
    private static Hook scanCreaturesHook;
    private static Hook steerHook;
    private static FieldInfo aiFlyField;
    private static ConditionalWeakTable<DesertBatflyPersonality, BatRef> personalityOwners = new();

    internal static bool Installed => aggressiveHook != null;

    internal static void Enable()
    {
        // These bridges are independent of the private DesertBatflyAI reflection below.
        // Enable them first so a future AI rename cannot silently disable all Task13 social,
        // signal, Task11-visibility or hard-survival vengeance integration.
        DesertBatflyEnvironmentalSocialBridge.Enable();
        DesertBatflyEnvironmentalVengeanceBridge.Enable();
        if (Installed) return;

        try
        {
            BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            PropertyInfo aggressive = typeof(DesertBatflyPersonality).GetProperty("Aggressive", instance);
            PropertyInfo roostChance = typeof(DesertBatflyPersonality).GetProperty("RoostChance", instance);
            PropertyInfo roostDuration = typeof(DesertBatflyPersonality).GetProperty("RoostDuration", instance);
            MethodInfo canHarass = typeof(DesertBatflyAI).GetMethod(
                "CanHarass", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(Creature) }, null);
            MethodInfo scanCreatures = typeof(DesertBatflyAI).GetMethod(
                "ScanCreatures", BindingFlags.Instance | BindingFlags.NonPublic, null,
                Type.EmptyTypes, null);
            MethodInfo steer = typeof(DesertBatflyAI).GetMethod(
                "Steer", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(Vector2), typeof(float) }, null);
            aiFlyField = typeof(DesertBatflyAI).GetField("fly", BindingFlags.Instance | BindingFlags.NonPublic);

            MethodInfo aggressiveGetter = aggressive?.GetGetMethod(true);
            MethodInfo roostChanceGetter = roostChance?.GetGetMethod(true);
            MethodInfo roostDurationGetter = roostDuration?.GetGetMethod(true);
            if (aggressiveGetter == null || roostChanceGetter == null || roostDurationGetter == null ||
                canHarass == null || scanCreatures == null || steer == null || aiFlyField == null)
                return;

            aggressiveHook = new Hook(aggressiveGetter, (AggressiveDetour)AggressiveHook);
            roostChanceHook = new Hook(roostChanceGetter, (RoostChanceDetour)RoostChanceHook);
            roostDurationHook = new Hook(roostDurationGetter, (RoostDurationDetour)RoostDurationHook);
            canHarassHook = new Hook(canHarass, (CanHarassDetour)CanHarassHook);
            scanCreaturesHook = new Hook(scanCreatures, (ScanCreaturesDetour)ScanCreaturesHook);
            steerHook = new Hook(steer, (SteerDetour)SteerHook);
        }
        catch
        {
            DisposeCoreHooks();
        }
    }

    internal static void Disable()
    {
        DisposeCoreHooks();
        DesertBatflyEnvironmentalVengeanceBridge.Disable();
        DesertBatflyEnvironmentalSocialBridge.Disable();
        personalityOwners = new ConditionalWeakTable<DesertBatflyPersonality, BatRef>();
    }

    private static void DisposeCoreHooks()
    {
        try { steerHook?.Dispose(); } catch { }
        try { scanCreaturesHook?.Dispose(); } catch { }
        try { canHarassHook?.Dispose(); } catch { }
        try { roostDurationHook?.Dispose(); } catch { }
        try { roostChanceHook?.Dispose(); } catch { }
        try { aggressiveHook?.Dispose(); } catch { }
        steerHook = null;
        scanCreaturesHook = null;
        canHarassHook = null;
        roostDurationHook = null;
        roostChanceHook = null;
        aggressiveHook = null;
        aiFlyField = null;
    }

    internal static void Register(DesertBatfly bat)
    {
        if (bat?.Personality == null) return;
        personalityOwners.GetValue(bat.Personality, _ => new BatRef()).Bat = bat;
    }

    private static bool AggressiveHook(AggressiveOrig orig, DesertBatflyPersonality personality)
    {
        if (orig(personality)) return true;
        DesertBatfly bat = Resolve(personality);
        return bat != null && DesertBatflyEnvironmentalBehavior.AllowsEnvironmentalDamageAttack(bat);
    }

    private static float RoostChanceHook(RoostChanceOrig orig, DesertBatflyPersonality personality)
    {
        float chance = orig(personality);
        DesertBatfly bat = Resolve(personality);
        if (bat == null) return chance;
        return chance * DesertBatflyEnvironmentalBehavior.RoostScale(bat);
    }

    private static int RoostDurationHook(RoostDurationOrig orig, DesertBatflyPersonality personality)
    {
        int duration = orig(personality);
        DesertBatfly bat = Resolve(personality);
        if (bat == null || !DesertBatflyEnvironmentalBehavior.TryGetInfluence(bat, out var influence))
            return duration;

        float scale = Mathf.Clamp(influence.RoostMultiplier, 1f, 4.5f);
        int adjusted = Mathf.RoundToInt(duration * scale);
        if (DesertBatflyEnvironmentalBehavior.ShouldHoldEnvironmentalRoost(bat))
            adjusted = Mathf.Max(adjusted, influence.HardSurvival ? 2400 : 1200);
        return Mathf.Clamp(adjusted, duration, 4200);
    }

    private static bool CanHarassHook(CanHarassOrig orig, DesertBatflyAI ai, Creature creature)
    {
        // Preserve all existing grief/injury/PTSD/target-validity gates first. Task13 may
        // only weight an already-legal target; it never resurrects a target rejected here.
        if (!orig(ai, creature)) return false;
        DesertBatfly bat = Resolve(ai);
        if (bat == null || !DesertBatflyEnvironmentalBehavior.TryGetInfluence(bat, out var influence))
            return true;
        if (influence.HardSurvival) return false;

        float scale = influence.HarassMultiplier;

        // Sandstorm normally suppresses roaming aggression. The narrow exception is a
        // Player physically occupying the Home/Hive access zone while this bat is already
        // trying to return or burrow. This only raises the probability floor for bats that
        // are already legal/aggressive under the existing AI; AttackSlots and PTSD remain.
        if (creature is Player player && IsSandstormHomeAccessBlocker(bat, player, influence))
        {
            float defensiveDrive = Mathf.Clamp01(
                bat.Personality.Temperament * 0.55f +
                bat.Personality.Nerve * 0.25f +
                Mathf.Max(influence.HomeReturnDrive, influence.BurrowDrive) * 0.20f);
            scale = Mathf.Max(scale, Mathf.Lerp(0.62f, 0.86f, defensiveDrive));
        }

        if (scale >= 0.999f) return true;
        if (scale <= 0.001f) return false;

        int clockBucket = (bat.room?.game?.clock ?? 0) / 80;
        int targetKey = creature?.abstractCreature?.ID.number ?? 0;
        return Stable01(bat.Personality.VisualSeed ^ targetKey * 397 ^ clockBucket * 7919) <= scale;
    }

    internal static bool IsSandstormHomeAccessBlocker(
        DesertBatfly bat,
        Player player,
        in DesertBatflyEnvironmentalInfluence influence)
    {
        if (bat?.room == null || player?.mainBodyChunk == null || player.room != bat.room ||
            influence.HardSurvival ||
            influence.Weather is not (DesertBatflyEnvironmentalWeather.Sandstorm or
                DesertBatflyEnvironmentalWeather.DeathSandstorm) ||
            (influence.HomeReturnDrive < 0.45f && influence.BurrowDrive < 0.45f) ||
            bat.room.hives == null || bat.room.hives.Length == 0)
            return false;

        float playerRadiusSq = SandstormBlockerRadius * SandstormBlockerRadius;
        float batRadiusSq = SandstormReturningBatRadius * SandstormReturningBatRadius;
        Vector2 playerPos = player.mainBodyChunk.pos;
        Vector2 batPos = bat.mainBodyChunk.pos;

        for (int h = 0; h < bat.room.hives.Length; h++)
        {
            IntVector2[] hive = bat.room.hives[h];
            if (hive == null || hive.Length == 0) continue;
            for (int i = 0; i < hive.Length; i++)
            {
                Vector2 point = bat.room.MiddleOfTile(hive[i]);
                if ((playerPos - point).sqrMagnitude > playerRadiusSq) continue;
                if ((batPos - point).sqrMagnitude <= batRadiusSq) return true;
            }
        }
        return false;
    }

    private static void ScanCreaturesHook(ScanCreaturesOrig orig, DesertBatflyAI ai)
    {
        DesertBatfly bat = Resolve(ai);
        if (bat == null || !DesertBatflyEnvironmentalBehavior.TryGetInfluence(bat, out var influence))
        {
            orig(ai);
            return;
        }

        float savedThirst = bat.DesertState.Thirst;
        try
        {
            float decisionThirst = savedThirst;
            if (influence.HarassMultiplier < 1f)
                decisionThirst *= Mathf.Lerp(0.28f, 1f, influence.HarassMultiplier);
            if (influence.HeatAgitation > 0f && !influence.HardSurvival)
            {
                float heatMotivation = influence.HeatAgitation *
                    (1f - influence.ThermalExhaustion) * 0.38f;
                decisionThirst = Mathf.Max(decisionThirst, savedThirst + heatMotivation);
            }
            bat.DesertState.Thirst = Mathf.Clamp01(decisionThirst);
            orig(ai);
        }
        finally
        {
            bat.DesertState.Thirst = savedThirst;
        }

    }

    private static void SteerHook(SteerOrig orig, DesertBatflyAI ai, Vector2 goal, float speed)
    {
        DesertBatfly bat = Resolve(ai);
        if (bat != null && DesertBatflyEnvironmentalBehavior.TryGetInfluence(bat, out var influence) &&
            influence.NavigationUncertainty > 0.04f &&
            influence.Weather is DesertBatflyEnvironmentalWeather.Fog or DesertBatflyEnvironmentalWeather.DenseFog)
        {
            int clock = bat.room?.game?.clock ?? 0;
            int bucket = clock / 110;
            float angle = Stable01(bat.Personality.VisualSeed ^ bucket * 0x45d9f3b) * Mathf.PI * 2f;
            float familiarity = FogNavigationFamiliarityScale(bat, influence.Weather);
            float uncertainty = Mathf.Clamp01(influence.NavigationUncertainty * familiarity);
            float anticipation = Mathf.Clamp(influence.ObstacleAnticipationScale, 0.30f, 1f);
            float errorRadius = Mathf.Lerp(5f, 62f, uncertainty);

            // Lower obstacle anticipation means the animal carries an imprecise goal much
            // closer to nearby geometry before the error fades. Any actual collision remains
            // vanilla Rain World terrain/body physics; Task13 never scripts a wall impact.
            float correctionStart = Mathf.Lerp(22f, 65f, anticipation);
            float correctionFull = Mathf.Lerp(175f, 300f, anticipation);
            float distanceFade = Mathf.InverseLerp(
                correctionStart,
                correctionFull,
                Vector2.Distance(bat.mainBodyChunk.pos, goal));
            goal += new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * errorRadius * distanceFade;
        }
        orig(ai, goal, speed);
    }

    internal static float FogNavigationFamiliarityScale(
        DesertBatfly bat,
        DesertBatflyEnvironmentalWeather weather)
    {
        if (bat?.room?.abstractRoom == null ||
            weather is not (DesertBatflyEnvironmentalWeather.Fog or DesertBatflyEnvironmentalWeather.DenseFog))
            return 1f;

        DesertBatflyColonyRuntime.IndividualRecord record =
            DesertBatflyColonyRuntime.RecordFor(bat.abstractCreature, false);
        bool homeRoom = record != null && !string.IsNullOrEmpty(record.CurrentColony) &&
            string.Equals(
                record.CurrentColony,
                bat.room.abstractRoom.name,
                StringComparison.OrdinalIgnoreCase);
        if (!homeRoom) return 1f;

        // Familiar Home/Hive geometry reduces navigation-position uncertainty but does not
        // restore visual recognition range. DenseFog keeps a meaningful residual error.
        bool nearHive = DesertBatflyEnvironmentalExposure.NearHive(
            bat.room,
            bat.room.GetTilePosition(bat.mainBodyChunk.pos),
            10);
        if (weather == DesertBatflyEnvironmentalWeather.DenseFog)
            return nearHive ? 0.52f : 0.66f;
        return nearHive ? 0.72f : 0.82f;
    }

    private static DesertBatfly Resolve(DesertBatflyPersonality personality)
    {
        if (personality == null || !personalityOwners.TryGetValue(personality, out BatRef holder)) return null;
        DesertBatfly bat = holder.Bat;
        return bat != null && !bat.dead && !bat.slatedForDeletetion ? bat : null;
    }

    private static DesertBatfly Resolve(DesertBatflyAI ai)
    {
        if (ai == null || aiFlyField == null) return null;
        try { return aiFlyField.GetValue(ai) as DesertBatfly; }
        catch { return null; }
    }

    private static float Stable01(int seed)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return (x & 0x00ffffffu) / 16777215f;
        }
    }
}
