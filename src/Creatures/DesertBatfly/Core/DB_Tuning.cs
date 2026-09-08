using System;
using System.Globalization;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// All OPEN values live here. Times are simulation ticks (40 ticks/second).
internal static class DB_Tuning
{
    internal const float Radius = 6f, Mass = 0.05f;
    internal const int HivePopulation = 11, CurvePopulation = 3;
    internal const float AggressiveThreshold = 0.52f, ThirstPerTick = 0.000065f;
    internal const float AttackThirst = 0.48f, DrainRelief = 0.65f;

    internal const float MealWater = 50f, AttackWaterPerSecond = 50f;

    internal const int AttackSlots = 2, Cooldown = 1800, FailedCooldown = 240;
    internal const int ObserveTicks = 100, AttachTicks = 180, RockStun = 110;
    internal const int DrainStartTicks = 20, DrainEndTicks = 160;
    internal const int ApproachTicks = 45, CircleTicks = 55, DiveTicks = 36;
    internal const int FakeDivePullUpTicks = 14, FakeDiveTicks = 38, InterestTicks = 1000;
    internal const float ObserveThirst = 0.3f, CounterThirst = 0.2f;

    internal const int RetaliationChargeTicks = 42;
    internal const int RetaliationContactMinTicks = 16, RetaliationContactMaxTicks = 36;
    internal const int RetaliationCooldown = 300;
    internal const float RetaliationMinSpeed = 11.5f, RetaliationMaxSpeed = 15.5f;
    internal const float RetaliationMinImpact = 1.4f, RetaliationMaxImpact = 2.8f;
    internal const float RetaliationMinDrag = 0.025f, RetaliationMaxDrag = 0.065f;
    internal const float RetaliationMinPush = 0.03f, RetaliationMaxPush = 0.08f;

    // The threshold is calibrated against the exact System.Random personality pipeline:
    // seeds 0..9999 yield 494 true avengers (4.94%). Temperament/Nerve remain hard
    // eligibility gates, so the population rate cannot turn every nasty bat into one.
    internal const float VengeanceTraitThreshold = 0.715f;
    internal const float VengeanceMinTemperament = 0.70f;
    internal const float VengeanceMinNerve = 0.58f;

    // Conformity is an independent social personality axis. It never replaces the bat's
    // own decision logic; it only changes how strongly observed flock behavior weighs in.
    internal const float SocialFollowerMinConformity = 0.48f;
    internal const float SocialFollowerRange = 235f;
    internal const int SocialVengeanceGroupCap = 3;

    // PTSD is intentionally much longer lived than immediate fear. These values are
    // persisted in CreatureState so a surviving bat can remain afraid after room changes.
    internal const int TraumaMinTicks = 2400, TraumaMaxTicks = 12000;
    internal const float TraumaAggressionBlock = 0.42f;
    internal const float TraumaSevere = 0.68f;
    internal const float TraumaFearMinDistance = 210f, TraumaFearMaxDistance = 410f;

    internal const float GrabMemoryGain = 0.28f, GrabThrowBonus = 0.24f;
    internal const float GrabThrowSpeed = 6f;
    internal const int GrabMemoryMinTicks = 1200, GrabMemoryMaxTicks = 3600;
    internal const float GrabFearMinDistance = 145f, GrabFearMaxDistance = 270f;

    internal const float SandSpitTraitThreshold = 0.58f;
    internal const float SandSpitMeterMinRate = 0.0032f, SandSpitMeterMaxRate = 0.0064f;
    internal const float SandSpitMovementBonus = 0.0015f;
    internal const float SandSpitThresholdMin = 0.82f, SandSpitThresholdMax = 1.16f;
    internal const int SandSpitWindupTicks = 8;
    internal const int SandSpitCooldownMinTicks = 90, SandSpitCooldownMaxTicks = 150;
    internal const int SandWorldParticleMin = 5, SandWorldParticleMax = 8;
    internal const int SandScreenMarkMin = 4, SandScreenMarkMax = 6;
    internal const int SandScreenLifeMin = 48, SandScreenLifeMax = 78;
    internal const int SandScreenMaxConcurrentBursts = 2;

    // Player-grasp escape is pulse based rather than a per-frame lottery. Every individual
    // eventually reaches a pressure threshold, while personality and condition alter how fast.
    internal const int GrabEscapeMinimumHoldTicks = 22;
    internal const int GrabEscapePulseMinTicks = 12, GrabEscapePulseMaxTicks = 19;
    internal const float GrabEscapePressureMinPerPulse = 0.055f;
    internal const float GrabEscapePressureMaxPerPulse = 0.105f;
    internal const float GrabEscapeHolderMovementBonus = 0.35f;
    internal const float GrabEscapeBurstChanceMin = 0.008f, GrabEscapeBurstChanceMax = 0.085f;
    internal const float GrabEscapeThresholdMin = 0.90f, GrabEscapeThresholdMax = 1.10f;
    internal const float GrabEscapeImpulseMin = 4.6f, GrabEscapeImpulseMax = 6.8f;
    internal const int GrabEscapeRegrabBlockTicks = 16;

    // Rescue is realized-only and uses the existing Combat PrimaryOwner. One victim may have
    // one primary and one delayed backup rescuer; all contact release still goes through the
    // victim's DB_RestraintRuntime exact-grasp authority.
    internal const int RescueMaxConcurrent = 2;
    internal const int RescueScanMinTicks = 14, RescueScanMaxTicks = 24;
    internal const int RescueBackupDelayTicks = 34;
    internal const int RescueCommitTicks = 150;
    internal const int RescueCooldownTicks = 150;
    internal const float RescueRange = 300f;
    internal const float RescueMotivationThreshold = 0.46f;
    internal const float RescueSpeedMin = 11.5f, RescueSpeedMax = 15.5f;
    internal const float RescueContactPadding = 4f;
    internal const float RescueImpactMinSpeed = 5.6f;
    internal const float RescueStrongImpactSpeed = 12.2f;
    internal const float RescueStrongImpactDrive = 0.64f;
    internal const float RescuePressureGainMin = 0.24f, RescuePressureGainMax = 0.62f;
    internal const float RescuePlayerImpulseMin = 0.35f, RescuePlayerImpulseMax = 1.15f;
    internal const float RescueRecoilMin = 3.8f, RescueRecoilMax = 6.2f;

    internal const int RoostMinTicks = 160, RoostMaxTicks = 520;
    internal const float RoostMinChance = 0.012f, RoostMaxChance = 0.045f;

    internal const int AttackerMemory = 640, RetreatTicks = 90, ApproachRetreatTicks = 55;
    internal const float LightTargetMass = 0.55f, SightRange = 340f;
    internal const float AlarmRadius = 110f;
    internal const int MaxSpikes = 4, MaxPatterns = 14;
    internal const int EmergenceTicks = 65, CurveAttempts = 80;
    internal const float SandMargin = 22f, ScavengerHostility = 0.65f;
}
