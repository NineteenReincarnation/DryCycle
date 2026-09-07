using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal static class DesertBatflyEnvironmentalBehavior
{
    internal const int DecisionBaseInterval = 12;
    internal const int MinCommitmentTicks = 120;
    internal const int MaxCommitmentTicks = 300;
    internal const float AnchorSwitchMargin = 0.13f;

    private sealed class State
    {
        internal DesertBatflyEnvironmentalInfluence Influence = DesertBatflyEnvironmentalInfluence.Neutral;
        internal int LastDecisionTick = int.MinValue;
        internal int CommitmentTicks;
        internal int AnchorId;
        internal float AnchorScore = float.NegativeInfinity;
        internal int HeatExposureTicks;
        internal Vector2 FogGoalOffset;
        internal int FogGoalOffsetUntil;
        internal int LastWeatherOrdinal = -1;
        internal DesertBatflyEnvironmentalPhase LastPhase = DesertBatflyEnvironmentalPhase.Calm;
    }

    private static ConditionalWeakTable<DesertBatfly, State> states = new();

    internal static void Reset()
    {
        states = new ConditionalWeakTable<DesertBatfly, State>();
    }

    internal static void Forget(DesertBatfly bat)
    {
        if (bat != null) states.Remove(bat);
    }

    internal static void Update(DesertBatfly bat)
    {
        if (bat?.room == null || bat.AI == null || bat.dead || bat.slatedForDeletetion)
            return;

        State state = states.GetValue(bat, _ => new State());
        int tick = bat.room.game?.clock ?? 0;
        int interval = DecisionBaseInterval + StableBucket(bat.Personality.VisualSeed, 7);
        if (state.LastDecisionTick == int.MinValue || tick - state.LastDecisionTick >= interval)
        {
            state.LastDecisionTick = tick;
            Recompute(bat, state, tick);
        }

        ApplyLocalBehavior(bat, state, tick);
    }

    internal static bool TryGetInfluence(DesertBatfly bat, out DesertBatflyEnvironmentalInfluence influence)
    {
        if (bat != null && states.TryGetValue(bat, out State state))
        {
            influence = state.Influence;
            return true;
        }
        influence = DesertBatflyEnvironmentalInfluence.Neutral;
        return false;
    }

    internal static float SocialScale(DesertBatfly bat)
        => TryGetInfluence(bat, out var influence) ? influence.SocialMultiplier : 1f;

    internal static float PlayScale(DesertBatfly bat)
        => TryGetInfluence(bat, out var influence) ? influence.PlayMultiplier : 1f;

    internal static float HarassScale(DesertBatfly bat)
        => TryGetInfluence(bat, out var influence) ? influence.HarassMultiplier : 1f;

    internal static float RoostScale(DesertBatfly bat)
        => TryGetInfluence(bat, out var influence) ? influence.RoostMultiplier : 1f;

    internal static float GroupCohesionScale(DesertBatfly bat)
        => TryGetInfluence(bat, out var influence) ? influence.GroupCohesionMultiplier : 1f;

    internal static float VisibilityScale(DesertBatfly bat)
        => TryGetInfluence(bat, out var influence) ? influence.VisibilityConfidence : 1f;

    internal static float ObstacleAnticipationScale(DesertBatfly bat)
        => TryGetInfluence(bat, out var influence) ? influence.ObstacleAnticipationScale : 1f;

    internal static bool AllowsEnvironmentalDamageAttack(DesertBatfly bat)
        => TryGetInfluence(bat, out var influence) && influence.DamageAttackPermission && !influence.HardSurvival;

    internal static bool HardSurvival(DesertBatfly bat)
        => TryGetInfluence(bat, out var influence) && influence.HardSurvival;

    internal static bool ShouldHoldEnvironmentalRoost(DesertBatfly bat)
    {
        if (!TryGetInfluence(bat, out var influence)) return false;
        if (influence.HardSurvival) return true;
        return influence.Phase is DesertBatflyEnvironmentalPhase.Sheltering or DesertBatflyEnvironmentalPhase.Acute &&
               influence.RoostMultiplier >= 1.65f;
    }

    internal static float MigrationSuppression(DesertBatfly bat)
        => TryGetInfluence(bat, out var influence) ? influence.MigrationSuppression : 0f;

    internal static bool SuppressNeutralSocial(DesertBatfly bat)
        => TryGetInfluence(bat, out var influence) && influence.SuppressesNeutralSocial;

    private static void Recompute(DesertBatfly bat, State state, int tick)
    {
        DesertBatflyEnvironmentalRoomRuntime.RoomState roomState =
            DesertBatflyEnvironmentalRoomRuntime.For(bat.room);
        if (roomState == null)
        {
            state.Influence = DesertBatflyEnvironmentalInfluence.Neutral;
            return;
        }

        DesertBatflyEnvironmentalRoomContext context = roomState.Context;
        if (context.Phase == DesertBatflyEnvironmentalPhase.Calm)
        {
            state.HeatExposureTicks = Mathf.Max(0, state.HeatExposureTicks - 20);
            state.CommitmentTicks = 0;
            state.AnchorId = 0;
            state.AnchorScore = float.NegativeInfinity;
            state.Influence = DesertBatflyEnvironmentalInfluence.Neutral;
            return;
        }

        if (state.CommitmentTicks > 0)
            state.CommitmentTicks = Mathf.Max(0, state.CommitmentTicks - (DecisionBaseInterval + 7));

        IntVector2 currentTile = bat.room.GetTilePosition(bat.mainBodyChunk.pos);
        float visibility = context.Phase == DesertBatflyEnvironmentalPhase.Recovery
            ? RecoveryVisibility(roomState, tick)
            : DesertBatflyEnvironmentalProfile.VisibilityConfidence(context.Weather, context.ActiveIntensity);
        DesertBatflyEnvironmentalExposureSample exposure =
            DesertBatflyEnvironmentalExposure.Sample(bat.room, currentTile, visibility);

        if (context.Weather is DesertBatflyEnvironmentalWeather.HeatWave or DesertBatflyEnvironmentalWeather.IntenseHeat &&
            context.ActiveIntensity > 0f)
        {
            int add = Mathf.RoundToInt(Mathf.Lerp(2f, 18f, context.ActiveIntensity) * Mathf.Lerp(0.55f, 1f, exposure.Exposure));
            state.HeatExposureTicks = Mathf.Clamp(state.HeatExposureTicks + add, 0, 2400);
        }
        else
        {
            state.HeatExposureTicks = Mathf.Max(0, state.HeatExposureTicks - 18);
        }

        float heatExposure01 = Mathf.Clamp01(state.HeatExposureTicks / 1200f);
        float capability = bat.Injury.PhysicalCapability;
        float injury = 1f - capability;
        float thirst = Mathf.Clamp01(bat.DesertState.Thirst);
        float nerve = bat.Personality.Nerve;
        float temperament = bat.Personality.Temperament;
        float roostAffinity = bat.Personality.RoostAffinity;
        float stable = Stable01(bat.Personality.VisualSeed ^ 0x2A7F1531);
        float sensitivity = Mathf.Clamp01(
            (1f - nerve) * 0.30f + injury * 0.42f + bat.Injury.PostStunShock * 0.20f + (1f - stable) * 0.08f);
        float heatTolerance = Mathf.Clamp01(
            nerve * 0.34f + capability * 0.31f + temperament * 0.13f + stable * 0.10f
            - injury * 0.16f - bat.Injury.PostStunShock * 0.10f - thirst * 0.18f + 0.16f);

        float phasePressure = PhasePressure(context.Phase);
        float shelterDrive = Mathf.Clamp01(context.ShelterUrgency * 0.60f + context.ActiveIntensity * 0.24f + sensitivity * 0.22f);
        float openAversion = Mathf.Clamp01(context.TravelExposure * 0.45f + context.ActiveIntensity * 0.30f + phasePressure * 0.35f);
        float roostMultiplier = 1f + shelterDrive * Mathf.Lerp(0.45f, 1.35f, roostAffinity);
        float harassMultiplier = Mathf.Lerp(1f, 0.20f, phasePressure);
        float socialMultiplier = Mathf.Lerp(1f, 0.30f, phasePressure);
        float playMultiplier = Mathf.Lerp(1f, 0.12f, phasePressure);
        float groupCohesion = Mathf.Lerp(1f, 0.78f, phasePressure);
        float radiusMultiplier = Mathf.Lerp(1f, 0.38f, phasePressure);
        float navUncertainty = context.Phase == DesertBatflyEnvironmentalPhase.Recovery
            ? DesertBatflyEnvironmentalProfile.NavigationUncertainty(context.Weather, 1f) * (1f - RecoveryVisibility(roomState, tick))
            : DesertBatflyEnvironmentalProfile.NavigationUncertainty(context.Weather, context.ActiveIntensity);
        float obstacleScale = Mathf.Clamp01(1f - navUncertainty * 0.72f);
        float homeReturn = 0f;
        float burrow = 0f;
        float migrationSuppression = 0f;
        float heatAgitation = 0f;
        float heatShelter = 0f;
        float thermalExhaustion = 0f;
        bool damagePermission = false;
        bool hardSurvival = context.Phase == DesertBatflyEnvironmentalPhase.Acute;
        string reason = context.PhaseReason;

        ApplyWeatherProfile(
            bat,
            context,
            exposure,
            heatExposure01,
            heatTolerance,
            ref shelterDrive,
            ref openAversion,
            ref roostMultiplier,
            ref harassMultiplier,
            ref socialMultiplier,
            ref playMultiplier,
            ref groupCohesion,
            ref radiusMultiplier,
            ref navUncertainty,
            ref obstacleScale,
            ref homeReturn,
            ref burrow,
            ref migrationSuppression,
            ref heatAgitation,
            ref heatShelter,
            ref thermalExhaustion,
            ref damagePermission,
            ref hardSurvival,
            ref reason);

        if (context.Phase == DesertBatflyEnvironmentalPhase.Recovery)
        {
            float recovery = IndividualRecovery(roomState, bat, tick);
            shelterDrive *= 1f - recovery;
            openAversion *= 1f - recovery;
            roostMultiplier = Mathf.Lerp(roostMultiplier, 1f, recovery);
            harassMultiplier = Mathf.Lerp(harassMultiplier, 1f, recovery);
            socialMultiplier = Mathf.Lerp(socialMultiplier, 1f, recovery);
            playMultiplier = Mathf.Lerp(playMultiplier, 1f, recovery);
            groupCohesion = Mathf.Lerp(groupCohesion, 1f, recovery);
            radiusMultiplier = Mathf.Lerp(radiusMultiplier, 1f, recovery);
            navUncertainty *= 1f - recovery;
            obstacleScale = Mathf.Lerp(obstacleScale, 1f, recovery);
            homeReturn *= 1f - recovery;
            burrow *= 1f - recovery;
            migrationSuppression *= 1f - recovery;
            heatAgitation *= 1f - recovery;
            heatShelter *= 1f - recovery;
            thermalExhaustion *= Mathf.Lerp(1f, 0f, recovery * 0.72f);
            damagePermission = false;
            hardSurvival = false;
            reason = "individual staggered environmental recovery";
        }

        DesertBatflyShelterAnchor preferred = null;
        float anchorScore = float.NegativeInfinity;
        if (shelterDrive >= 0.16f || homeReturn >= 0.20f || hardSurvival)
        {
            DesertBatflyEnvironmentalRoomRuntime.TryChooseAnchor(
                bat,
                context.Weather,
                Mathf.Clamp01((roostMultiplier - 1f) / 1.8f),
                out preferred,
                out anchorScore);

            if (preferred != null)
                ApplyAnchorCommitment(bat, state, preferred, anchorScore);
        }
        else if (state.CommitmentTicks <= 0)
        {
            state.AnchorId = 0;
            state.AnchorScore = float.NegativeInfinity;
        }

        Vector2? preferredPoint = null;
        float preferredQuality = 0f;
        if (state.AnchorId > 0)
        {
            DesertBatflyShelterAnchor committed = FindAnchor(roomState, state.AnchorId);
            if (committed != null)
            {
                preferredPoint = committed.Position;
                preferredQuality = Mathf.Clamp01(DesertBatflyEnvironmentalRoomRuntime.WeatherQuality(committed, context.Weather));
            }
        }

        float recoveryProgress = context.Phase == DesertBatflyEnvironmentalPhase.Recovery
            ? IndividualRecovery(roomState, bat, tick)
            : 0f;

        state.Influence = new DesertBatflyEnvironmentalInfluence(
            context.Phase,
            context.Weather,
            shelterDrive,
            openAversion,
            roostMultiplier,
            harassMultiplier,
            socialMultiplier,
            playMultiplier,
            groupCohesion,
            radiusMultiplier,
            visibility,
            navUncertainty,
            obstacleScale,
            homeReturn,
            burrow,
            migrationSuppression,
            heatAgitation,
            heatShelter,
            thermalExhaustion,
            damagePermission,
            hardSurvival,
            preferredPoint,
            preferredQuality,
            state.CommitmentTicks,
            recoveryProgress,
            reason);

        ApplyLightRainMoisture(bat, context, exposure);
        state.LastWeatherOrdinal = (int)context.Weather;
        state.LastPhase = context.Phase;
    }

    private static void ApplyWeatherProfile(
        DesertBatfly bat,
        in DesertBatflyEnvironmentalRoomContext context,
        in DesertBatflyEnvironmentalExposureSample exposure,
        float heatExposure01,
        float heatTolerance,
        ref float shelterDrive,
        ref float openAversion,
        ref float roostMultiplier,
        ref float harassMultiplier,
        ref float socialMultiplier,
        ref float playMultiplier,
        ref float groupCohesion,
        ref float radiusMultiplier,
        ref float navUncertainty,
        ref float obstacleScale,
        ref float homeReturn,
        ref float burrow,
        ref float migrationSuppression,
        ref float heatAgitation,
        ref float heatShelter,
        ref float thermalExhaustion,
        ref bool damagePermission,
        ref bool hardSurvival,
        ref string reason)
    {
        float i = context.ActiveIntensity;
        float capability = bat.Injury.PhysicalCapability;
        float temperament = bat.Personality.Temperament;
        float nerve = bat.Personality.Nerve;

        switch (context.Weather)
        {
            case DesertBatflyEnvironmentalWeather.LightRain:
                shelterDrive *= 0.32f;
                openAversion *= 0.25f;
                roostMultiplier = Mathf.Lerp(1f, 1.14f, Mathf.Max(i, context.ShelterUrgency));
                harassMultiplier = Mathf.Lerp(1f, 0.95f, i);
                socialMultiplier = 1f;
                playMultiplier = Mathf.Lerp(1f, 0.92f, i);
                groupCohesion = 1f;
                radiusMultiplier = Mathf.Lerp(1f, 0.94f, i);
                reason = "LightRain: weak rest bias / moisture opportunity";
                break;

            case DesertBatflyEnvironmentalWeather.Fog:
                shelterDrive *= 0.32f;
                openAversion *= 0.30f;
                harassMultiplier = Mathf.Lerp(0.92f, 0.70f, i);
                socialMultiplier = Mathf.Lerp(0.96f, 0.78f, i);
                playMultiplier = Mathf.Lerp(0.90f, 0.66f, i);
                groupCohesion = Mathf.Lerp(1.03f, 1.10f, i);
                radiusMultiplier = Mathf.Lerp(0.86f, 0.62f, i);
                roostMultiplier = Mathf.Lerp(1.02f, 1.16f, i);
                reason = "Fog: reduced visual confidence and activity range";
                break;

            case DesertBatflyEnvironmentalWeather.DenseFog:
                shelterDrive = Mathf.Max(shelterDrive, Mathf.Lerp(0.28f, 0.64f, i));
                openAversion = Mathf.Max(openAversion, Mathf.Lerp(0.22f, 0.58f, i));
                harassMultiplier = Mathf.Lerp(0.55f, 0.15f, i);
                socialMultiplier = Mathf.Lerp(0.62f, 0.25f, i);
                playMultiplier = Mathf.Lerp(0.45f, 0.12f, i);
                groupCohesion = Mathf.Lerp(1.08f, 0.82f, i);
                radiusMultiplier = Mathf.Lerp(0.58f, 0.28f, i);
                roostMultiplier = Mathf.Lerp(1.35f, 1.90f, i);
                homeReturn = Mathf.Lerp(0.20f, 0.72f, i);
                navUncertainty = DesertBatflyEnvironmentalProfile.NavigationUncertainty(context.Weather, i);
                obstacleScale = DesertBatflyEnvironmentalProfile.ObstacleAnticipationScale(context.Weather, i);
                reason = "DenseFog: navigation uncertainty / Home and Roost bias";
                break;

            case DesertBatflyEnvironmentalWeather.HeavyRain:
            {
                float rainBurden = DesertBatflyEnvironmentalProfile.HeavyRainBurden(
                    i,
                    exposure.RainExposure,
                    exposure.RoofShielding,
                    context.ShelterUrgency,
                    capability,
                    bat.Injury.PostStunShock,
                    nerve);
                float injury = 1f - capability;
                float covered = Mathf.Clamp01(exposure.RoofShielding * 0.82f + exposure.Enclosure * 0.18f);
                float shelterPressure = Mathf.Clamp01(Mathf.Max(rainBurden, context.ShelterUrgency * 0.72f));

                shelterDrive = Mathf.Max(shelterDrive, Mathf.Lerp(0.16f, 0.94f, shelterPressure));
                openAversion = Mathf.Max(openAversion, Mathf.Lerp(0.22f, 0.96f, rainBurden));
                roostMultiplier = Mathf.Lerp(1.08f, 2.30f, shelterPressure);
                harassMultiplier = Mathf.Lerp(0.92f, 0.20f, shelterPressure);
                socialMultiplier = Mathf.Lerp(0.96f, 0.48f, shelterPressure);
                playMultiplier = Mathf.Lerp(0.90f, 0.12f, shelterPressure);
                groupCohesion = Mathf.Lerp(1.02f, 0.78f, shelterPressure);
                radiusMultiplier = Mathf.Lerp(0.88f, 0.34f, shelterPressure);

                // HeavyRain prefers nearby covered pockets over a blanket Hive recall.
                // Home becomes attractive mainly when exposure is high and the individual
                // is cautious/injured; Burrow is a late fallback, not the default answer.
                float homePressure = Mathf.Clamp01(
                    Mathf.InverseLerp(0.48f, 0.92f, rainBurden) *
                    (0.42f + injury * 0.30f + (1f - nerve) * 0.18f));
                float burrowPressure = Mathf.Clamp01(
                    Mathf.InverseLerp(0.68f, 1f, rainBurden) *
                    (0.28f + injury * 0.32f + (1f - covered) * 0.16f));
                homeReturn = Mathf.Max(homeReturn, homePressure);
                burrow = Mathf.Max(burrow, burrowPressure);

                damagePermission = false;
                hardSurvival = false;
                reason = covered >= 0.62f
                    ? "HeavyRain: covered pocket preserves local colony life"
                    : "HeavyRain: RainExposure drives shelter; Home/Burrow only when local cover is poor";
                break;
            }

            case DesertBatflyEnvironmentalWeather.HeatWave:
            {
                heatAgitation = DesertBatflyEnvironmentalProfile.HeatAgitation(i) * Mathf.Lerp(0.58f, 1.20f, temperament) * Mathf.Lerp(0.82f, 1.10f, nerve);
                heatShelter = DesertBatflyEnvironmentalProfile.HeatShelterDrive(i) * Mathf.Lerp(1.18f, 0.80f, heatTolerance);
                thermalExhaustion = DesertBatflyEnvironmentalProfile.ThermalExhaustion(i, heatExposure01) * Mathf.Lerp(1.18f, 0.72f, heatTolerance);
                shelterDrive = Mathf.Clamp01(Mathf.Max(shelterDrive, heatShelter + thermalExhaustion * 0.45f));
                openAversion = Mathf.Clamp01(Mathf.Max(openAversion, heatShelter * 0.72f));
                float activeAggression = Mathf.Clamp01(heatAgitation * (1f - thermalExhaustion) * (1f - shelterDrive * 0.62f));
                harassMultiplier = Mathf.Lerp(1f, 1.45f, activeAggression);
                socialMultiplier = Mathf.Lerp(1.02f, 0.36f, Mathf.Max(shelterDrive, thermalExhaustion));
                playMultiplier = Mathf.Lerp(1.08f, 0.18f, Mathf.Max(shelterDrive, thermalExhaustion));
                groupCohesion = Mathf.Lerp(1f, 0.76f, thermalExhaustion);
                radiusMultiplier = Mathf.Lerp(1f, 0.36f, Mathf.Max(heatShelter, thermalExhaustion));
                roostMultiplier = Mathf.Lerp(1.05f, 2.25f, Mathf.Max(heatShelter, thermalExhaustion));
                damagePermission = capability >= 0.76f &&
                                   bat.Injury.PostStunShock < 0.30f &&
                                   heatAgitation >= Mathf.Lerp(0.78f, 0.48f, temperament) &&
                                   thermalExhaustion < 0.58f && shelterDrive < 0.72f;
                reason = activeAggression > shelterDrive
                    ? "HeatWave: agitation currently dominates shelter drive"
                    : "HeatWave: shelter/exhaustion overtaking agitation";
                break;
            }

            case DesertBatflyEnvironmentalWeather.IntenseHeat:
            {
                heatAgitation = Mathf.Clamp01(0.72f + i * 0.28f) * Mathf.Lerp(0.78f, 1.12f, temperament);
                heatShelter = Mathf.Clamp01(0.72f + i * 0.30f) * Mathf.Lerp(1.14f, 0.82f, heatTolerance);
                thermalExhaustion = Mathf.Clamp01(0.40f + heatExposure01 * 0.55f + i * 0.22f) * Mathf.Lerp(1.16f, 0.76f, heatTolerance);
                shelterDrive = Mathf.Clamp01(Mathf.Max(shelterDrive, heatShelter));
                openAversion = Mathf.Clamp01(Mathf.Max(openAversion, 0.78f + i * 0.20f));
                homeReturn = Mathf.Clamp01(0.62f + heatShelter * 0.35f);
                burrow = Mathf.Clamp01(0.56f + heatShelter * 0.40f);
                roostMultiplier = Mathf.Lerp(1.75f, 3.20f, Mathf.Max(heatShelter, thermalExhaustion));
                harassMultiplier = Mathf.Lerp(1.10f, 1.65f, heatAgitation * (1f - Mathf.Clamp01(thermalExhaustion * 0.75f)));
                socialMultiplier = Mathf.Lerp(0.52f, 0.05f, Mathf.Max(heatShelter, thermalExhaustion));
                playMultiplier = Mathf.Lerp(0.24f, 0f, Mathf.Max(heatShelter, thermalExhaustion));
                groupCohesion = Mathf.Lerp(0.82f, 0.38f, heatShelter);
                radiusMultiplier = Mathf.Lerp(0.46f, 0.18f, Mathf.Max(heatShelter, thermalExhaustion));

                float aggressionEligibility = Mathf.Clamp01(
                    heatAgitation * 0.62f + temperament * 0.22f + nerve * 0.16f);
                damagePermission = capability >= 0.72f &&
                                   !bat.Injury.HasSevereWingInjury &&
                                   bat.Injury.PostStunShock < 0.32f &&
                                   aggressionEligibility >= 0.54f &&
                                   thermalExhaustion < 0.84f &&
                                   shelterDrive < 0.94f;
                hardSurvival = context.Phase == DesertBatflyEnvironmentalPhase.Acute ||
                               thermalExhaustion >= 0.86f || heatShelter >= 0.94f;
                if (hardSurvival) damagePermission = false;
                reason = hardSurvival
                    ? "IntenseHeat: hard thermal survival overrides aggression"
                    : "IntenseHeat: damaging aggression permission coexists with retreat pressure";
                break;
            }

            case DesertBatflyEnvironmentalWeather.Sandstorm:
                homeReturn = Mathf.Clamp01(0.38f + context.ShelterUrgency * 0.58f + (1f - nerve) * 0.12f);
                burrow = Mathf.Clamp01(0.28f + context.ShelterUrgency * 0.62f);
                shelterDrive = Mathf.Max(shelterDrive, Mathf.Lerp(0.36f, 0.92f, Mathf.Max(i, context.ShelterUrgency)));
                openAversion = Mathf.Max(openAversion, Mathf.Lerp(0.55f, 0.96f, Mathf.Max(i, context.ShelterUrgency)));
                migrationSuppression = Mathf.Max(0f, DesertBatflyEnvironmentalProfile.SandstormMigrationSuppression(
                    context.Weather, DesertBatflyEnvironmentalRoomRuntime.For(bat.room).WeatherSample));
                roostMultiplier = Mathf.Lerp(1.45f, 2.65f, shelterDrive);
                harassMultiplier = Mathf.Lerp(0.68f, 0.08f, shelterDrive);
                socialMultiplier = Mathf.Lerp(0.70f, 0.18f, shelterDrive);
                playMultiplier = Mathf.Lerp(0.52f, 0.04f, shelterDrive);
                groupCohesion = context.Phase == DesertBatflyEnvironmentalPhase.Advisory ? 1.05f : Mathf.Lerp(0.92f, 0.60f, shelterDrive);
                radiusMultiplier = Mathf.Lerp(0.66f, 0.18f, shelterDrive);
                reason = context.ActiveIntensity > 0f
                    ? "Sandstorm: Home retention / Burrow / Roost"
                    : "Sandstorm: species-specific early anticipation";
                break;

            case DesertBatflyEnvironmentalWeather.DeathSandstorm:
                homeReturn = 1f;
                burrow = Mathf.Clamp01(0.82f + i * 0.18f);
                shelterDrive = Mathf.Max(shelterDrive, 0.94f);
                openAversion = 1f;
                migrationSuppression = 1f;
                roostMultiplier = 3.25f;
                harassMultiplier = 0f;
                socialMultiplier = 0.04f;
                playMultiplier = 0f;
                groupCohesion = 0.30f;
                radiusMultiplier = 0.16f;
                hardSurvival = context.Phase is DesertBatflyEnvironmentalPhase.Sheltering or DesertBatflyEnvironmentalPhase.Acute;
                reason = "DeathSandstorm: hard Home retention / late travel suppression";
                break;

            case DesertBatflyEnvironmentalWeather.DeathRain:
            {
                float phaseSeverity = context.Phase switch
                {
                    DesertBatflyEnvironmentalPhase.Advisory => 0.34f,
                    DesertBatflyEnvironmentalPhase.Preparation => 0.62f,
                    DesertBatflyEnvironmentalPhase.Sheltering => 0.92f,
                    DesertBatflyEnvironmentalPhase.Acute => 1f,
                    _ => Mathf.Clamp01(i)
                };
                float injury = 1f - capability;
                float rainExposure = Mathf.Clamp01(exposure.RainExposure);

                shelterDrive = Mathf.Max(shelterDrive, Mathf.Lerp(0.58f, 1f, phaseSeverity));
                openAversion = Mathf.Max(openAversion, Mathf.Lerp(0.72f, 1f, phaseSeverity));
                homeReturn = Mathf.Max(homeReturn, Mathf.Clamp01(0.54f + phaseSeverity * 0.44f + injury * 0.12f));
                burrow = Mathf.Max(burrow, Mathf.Clamp01(0.40f + phaseSeverity * 0.52f + injury * 0.10f));
                roostMultiplier = Mathf.Lerp(1.85f, 3.25f, phaseSeverity);
                harassMultiplier = Mathf.Lerp(0.20f, 0f, phaseSeverity);
                socialMultiplier = Mathf.Lerp(0.24f, 0.03f, phaseSeverity);
                playMultiplier = 0f;
                groupCohesion = Mathf.Lerp(0.55f, 0.28f, phaseSeverity);
                radiusMultiplier = Mathf.Lerp(0.34f, 0.14f, phaseSeverity);
                damagePermission = false;

                // Task09 owns pre-onset cross-room safety. Once this room is the accepted
                // survival room, Task13 must actually drive native Home/Hive/Burrow instead
                // of merely setting HardSurvival with zero Home/Burrow pressure.
                hardSurvival = context.Phase is DesertBatflyEnvironmentalPhase.Sheltering or DesertBatflyEnvironmentalPhase.Acute;
                if (hardSurvival && rainExposure > 0.55f)
                {
                    homeReturn = Mathf.Max(homeReturn, 0.92f);
                    burrow = Mathf.Max(burrow, 0.82f);
                }
                reason = hardSurvival
                    ? "DeathRain: hard local Home/Roost/Burrow survival; Task09 retains cross-room ownership"
                    : "DeathRain: pre-onset local contraction while Task09 handles refuge travel";
                break;
            }
        }

        shelterDrive = Mathf.Clamp01(shelterDrive);
        openAversion = Mathf.Clamp01(openAversion);
        heatAgitation = Mathf.Clamp01(heatAgitation);
        heatShelter = Mathf.Clamp01(heatShelter);
        thermalExhaustion = Mathf.Clamp01(thermalExhaustion);
    }

    private static void ApplyLocalBehavior(DesertBatfly bat, State state, int tick)
    {
        DesertBatflyEnvironmentalInfluence influence = state.Influence;
        if (influence.Phase == DesertBatflyEnvironmentalPhase.Calm) return;

        if (influence.SuppressesNeutralSocial)
            DesertBatflySocialLife.CancelForPriority(bat, "Task13 environmental priority");

        if (influence.PreferredShelterPoint is not Vector2 shelterPoint) return;
        if (influence.ShelterDrive < 0.28f && !influence.HardSurvival) return;
        if (bat.DesertAI.FormalAttack && !influence.HardSurvival) return;
        if (bat.AI.behavior == FlyAI.Behavior.Chain) return;

        Vector2 goal = shelterPoint;
        if (influence.NavigationUncertainty > 0.05f &&
            influence.Weather is DesertBatflyEnvironmentalWeather.Fog or DesertBatflyEnvironmentalWeather.DenseFog)
        {
            if (tick >= state.FogGoalOffsetUntil || state.LastWeatherOrdinal != (int)influence.Weather)
            {
                float angle = Stable01(bat.Personality.VisualSeed ^ tick / 120) * Mathf.PI * 2f;
                float radius = Mathf.Lerp(8f, 70f, influence.NavigationUncertainty);
                state.FogGoalOffset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                state.FogGoalOffsetUntil = tick + 90 + StableBucket(bat.Personality.VisualSeed, 70);
            }
            float distance = Vector2.Distance(bat.mainBodyChunk.pos, shelterPoint);
            float fade = Mathf.InverseLerp(70f, 320f, distance);
            goal += state.FogGoalOffset * fade;
        }

        if (!bat.room.terrain.Contains(goal))
            bat.AI.localGoal = goal;
        else
            bat.AI.localGoal = shelterPoint;
    }

    private static void ApplyAnchorCommitment(
        DesertBatfly bat,
        State state,
        DesertBatflyShelterAnchor candidate,
        float score)
    {
        if (state.AnchorId == candidate.Id)
        {
            state.AnchorScore = score;
            if (state.CommitmentTicks <= 0)
                state.CommitmentTicks = CommitmentDuration(bat);
            return;
        }

        if (state.AnchorId > 0 && state.CommitmentTicks > 0 && score < state.AnchorScore + AnchorSwitchMargin)
            return;

        state.AnchorId = candidate.Id;
        state.AnchorScore = score;
        state.CommitmentTicks = CommitmentDuration(bat);
    }

    private static DesertBatflyShelterAnchor FindAnchor(
        DesertBatflyEnvironmentalRoomRuntime.RoomState roomState,
        int id)
    {
        if (roomState == null || id <= 0) return null;
        for (int i = 0; i < roomState.Anchors.Count; i++)
            if (roomState.Anchors[i].Id == id) return roomState.Anchors[i];
        return null;
    }

    private static int CommitmentDuration(DesertBatfly bat)
    {
        float cautious = Mathf.Clamp01((1f - bat.Personality.Nerve) * 0.55f + bat.Personality.RoostAffinity * 0.25f + (1f - bat.Injury.PhysicalCapability) * 0.20f);
        return Mathf.RoundToInt(Mathf.Lerp(MinCommitmentTicks, MaxCommitmentTicks, cautious));
    }

    private static float PhasePressure(DesertBatflyEnvironmentalPhase phase)
        => phase switch
        {
            DesertBatflyEnvironmentalPhase.Calm => 0f,
            DesertBatflyEnvironmentalPhase.Advisory => 0.14f,
            DesertBatflyEnvironmentalPhase.Preparation => 0.46f,
            DesertBatflyEnvironmentalPhase.Sheltering => 0.78f,
            DesertBatflyEnvironmentalPhase.Acute => 1f,
            DesertBatflyEnvironmentalPhase.Recovery => 0.42f,
            _ => 0f
        };

    private static float RecoveryVisibility(DesertBatflyEnvironmentalRoomRuntime.RoomState state, int tick)
    {
        float progress = DesertBatflyEnvironmentalRoomRuntime.RecoveryProgress(state, tick);
        float worst = DesertBatflyEnvironmentalProfile.VisibilityConfidence(state.LastWeather, 1f);
        return Mathf.Lerp(worst, 1f, progress);
    }

    private static float IndividualRecovery(
        DesertBatflyEnvironmentalRoomRuntime.RoomState roomState,
        DesertBatfly bat,
        int tick)
    {
        float roomProgress = DesertBatflyEnvironmentalRoomRuntime.RecoveryProgress(roomState, tick);
        float delay = Mathf.Clamp01(
            (1f - bat.Personality.Nerve) * 0.24f +
            (1f - bat.Injury.PhysicalCapability) * 0.28f +
            bat.Injury.PostStunShock * 0.18f +
            (1f - bat.Personality.Temperament) * 0.08f +
            Stable01(bat.Personality.VisualSeed ^ 0x6D2B79F5) * 0.14f);
        return Mathf.InverseLerp(delay * 0.46f, 1f, roomProgress);
    }

    private static void ApplyLightRainMoisture(
        DesertBatfly bat,
        in DesertBatflyEnvironmentalRoomContext context,
        in DesertBatflyEnvironmentalExposureSample exposure)
    {
        bool lightRain = context.Weather == DesertBatflyEnvironmentalWeather.LightRain;
        bool tolerableHeavyRain = context.Weather == DesertBatflyEnvironmentalWeather.HeavyRain &&
                                  context.Phase is DesertBatflyEnvironmentalPhase.Advisory or DesertBatflyEnvironmentalPhase.Preparation &&
                                  context.ActiveIntensity < 0.58f;
        if ((!lightRain && !tolerableHeavyRain) ||
            context.ActiveIntensity <= 0f || exposure.RainExposure < 0.55f || bat.DesertState.Thirst <= 0f)
            return;

        float rainFactor = lightRain ? 1f : 0.52f;
        float relief = DesertBatflyTuning.ThirstPerTick * 2.15f * rainFactor * context.ActiveIntensity * exposure.RainExposure;
        bat.DesertState.Thirst = Mathf.Max(0f, bat.DesertState.Thirst - relief);
    }

    private static int StableBucket(int seed, int count)
    {
        if (count <= 1) return 0;
        return Mathf.Clamp(Mathf.FloorToInt(Stable01(seed) * count), 0, count - 1);
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
