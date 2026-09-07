using DryCycle.Debugging.AI;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal readonly struct DB_ThreatTacticalProfile
{
    internal readonly int PlayerSlot;
    internal readonly float Confidence;
    internal readonly float ProjectileRisk;
    internal readonly float CloseRisk;
    internal readonly float ExplosionRisk;
    internal readonly float CounterRisk;
    internal readonly float Caution;
    internal readonly bool VisibleSpear;
    internal readonly bool VisibleRock;
    internal readonly bool VisibleExplosive;
    internal readonly bool VisibleStartle;
    internal readonly bool VisibleShock;

    internal DB_ThreatTacticalProfile(
        int playerSlot,
        float confidence,
        float projectileRisk,
        float closeRisk,
        float explosionRisk,
        float counterRisk,
        float caution,
        bool visibleSpear,
        bool visibleRock,
        bool visibleExplosive,
        bool visibleStartle,
        bool visibleShock)
    {
        PlayerSlot = playerSlot;
        Confidence = Mathf.Clamp01(confidence);
        ProjectileRisk = Mathf.Clamp01(projectileRisk);
        CloseRisk = Mathf.Clamp01(closeRisk);
        ExplosionRisk = Mathf.Clamp01(explosionRisk);
        CounterRisk = Mathf.Clamp01(counterRisk);
        Caution = Mathf.Clamp01(caution);
        VisibleSpear = visibleSpear;
        VisibleRock = visibleRock;
        VisibleExplosive = visibleExplosive;
        VisibleStartle = visibleStartle;
        VisibleShock = visibleShock;
    }
}

/// <summary>
/// Shared Task 11 tactical math. It is deliberately read-only with respect to persistent
/// memory: observing a held item changes only the current tactical profile and never trains
/// Threat Signature Memory. This class changes goals/probabilities only; it never owns Fly
/// physics and never writes BodyChunk.vel.
/// </summary>
internal static class DB_ThreatTactics
{
    internal static float AdjustFakeDiveChance(
        DesertBatfly bat,
        Player player,
        float baseChance)
    {
        baseChance = Mathf.Clamp01(baseChance);
        if (!TryProfile(bat, player, out DB_ThreatTacticalProfile profile))
            return baseChance;

        DB_PlayerThreatMemory memory = DB_ThreatMemoryStore.For(
            bat.DesertState, profile.PlayerSlot);
        return LearnedFakeDiveChance(
            baseChance,
            memory?.ProjectilePressure ?? 0f,
            memory?.PiercingPressure ?? 0f,
            memory?.CounterKillPressure ?? 0f,
            profile.Confidence,
            bat.Personality.Nerve,
            bat.Personality.Temperament,
            profile.VisibleSpear);
    }

    internal static float LearnedFakeDiveChance(
        float baseChance,
        float projectile,
        float piercing,
        float counterKill,
        float confidence,
        float nerve,
        float temperament,
        bool visibleSpear)
    {
        baseChance = Mathf.Clamp01(baseChance);
        float learned = Mathf.Clamp01(
            Mathf.Clamp01(projectile) * 0.20f +
            Mathf.Clamp01(piercing) * 0.45f +
            Mathf.Clamp01(counterKill) * 0.35f);
        learned *= Mathf.Lerp(0.58f, 1f, Mathf.Clamp01(confidence));
        if (visibleSpear)
            learned = Mathf.Clamp01(learned + Mathf.Clamp01(piercing) * 0.18f);
        learned *= Mathf.Lerp(1.16f, 0.70f, Mathf.Clamp01(nerve));
        learned *= Mathf.Lerp(1.04f, 0.78f, Mathf.Clamp01(temperament));
        float bonus = Mathf.Clamp01(learned) * 0.44f;
        return Mathf.Clamp01(baseChance + bonus * (1f - baseChance));
    }

    internal static bool TryApplyOrdinaryProjectileEvade(DesertBatfly bat)
    {
        if (bat?.room == null || bat.AI == null || bat.dead || !bat.Consious || bat.inShortcut ||
            DesertBatflyIntimidation.IsExtremeVengeanceActive(bat) ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.ImmediateProjectileEvade))
            return false;

        if (!DesertBatflyThreatRuntime.TryGetDebugState(
                bat,
                out DesertBatflyThreatDebugState threat) ||
            !threat.Cue.ProjectileThreat ||
            threat.Cue.ProjectileThreatDirection.sqrMagnitude < 0.5f ||
            !DesertBatflyThreatRuntime.ValidSlot(threat.Cue.PlayerSlot))
            return false;

        Player player = PlayerBySlot(bat.room, threat.Cue.PlayerSlot);
        if (player == null || !TryIncomingProjectileEvade(bat, player, out Vector2 evade))
            return false;

        return ApplyProjectileEvadeOwned(bat, evade);
    }

    internal static bool ApplyProjectileEvadeOwned(DesertBatfly bat, Vector2 evade)
    {
        if (bat?.room == null || bat.AI == null || bat.dead || !bat.Consious || bat.inShortcut ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.ImmediateProjectileEvade))
            return false;

        bat.LoseAllGrasps();
        bat.burrowOrHangSpot = null;
        if (bat.AI.behavior != FlyAI.Behavior.Idle)
            bat.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        bat.AI.followingDijkstraMap = -1;
        bat.movMode = Fly.MovementMode.BatFlight;
        if (!DB_FlightMotor.TryGuideNative(
                bat, DB_BehaviorOwner.ImmediateProjectileEvade, evade, 9f))
            return false;
        DesertBatflySocialLife.CancelForPriority(bat, "R3 PrimaryOwner=ImmediateProjectileEvade");
        TraceAdjustment(
            bat,
            "ThreatEvadeStarted",
            evade,
            "real projectile trajectory owns this motor goal; Rain World native Fly locomotion executes the dodge");
        return true;
    }

    internal static Vector2 AdjustExtremeVengeanceGoal(
        DesertBatfly bat,
        Player player,
        Vector2 baseGoal,
        ref float speed)
    {
        if (bat?.room == null || player == null || player.room != bat.room)
            return baseGoal;

        if (TryIncomingProjectileEvade(bat, player, out Vector2 evade))
        {
            speed = Mathf.Max(speed, 9.5f);
            TraceAdjustment(
                bat,
                "ThreatEvadeStarted",
                evade,
                "real incoming projectile overrides vengeance movement for this frame; vengeance intent retained");
            return evade;
        }

        if (!TryProfile(bat, player, out DB_ThreatTacticalProfile profile) ||
            profile.Confidence < 0.04f || profile.Caution < 0.04f)
            return baseGoal;

        Vector2 center = player.mainBodyChunk.pos;
        Vector2 offset = baseGoal - center;
        if (offset.sqrMagnitude < 4f)
            offset = bat.mainBodyChunk.pos - center;
        if (offset.sqrMagnitude < 4f)
            offset = Vector2.right * StableSide(bat, profile.PlayerSlot);

        float radius = offset.magnitude;
        Vector2 direction = offset.normalized;
        float side = StableSide(bat, profile.PlayerSlot);
        Vector2 perpendicular = new Vector2(-direction.y, direction.x) * side;
        Vector2 adjusted;

        if (radius >= 105f)
        {
            float scale = 1f + profile.Caution * (radius >= 150f ? 0.26f : 0.38f);
            adjusted = center + offset * scale;
        }
        else
        {
            float lateralRisk = Mathf.Clamp01(
                profile.ProjectileRisk * 0.62f + profile.CounterRisk * 0.38f);
            float lateral = Mathf.Lerp(6f, 52f, lateralRisk * profile.Confidence);
            adjusted = baseGoal + perpendicular * lateral;
            if (profile.VisibleSpear)
                adjusted += perpendicular * Mathf.Lerp(4f, 18f, profile.ProjectileRisk);
        }

        if (profile.VisibleExplosive && profile.ExplosionRisk > 0.20f)
        {
            Vector2 away = Custom.DirVec(center, adjusted);
            adjusted += away * Mathf.Lerp(8f, 34f, profile.ExplosionRisk * profile.Confidence);
        }

        TraceAdjustment(
            bat,
            "ThreatVengeanceGeometry",
            adjusted,
            $"caution={profile.Caution:0.00}; projectile={profile.ProjectileRisk:0.00}; counter={profile.CounterRisk:0.00}; revenge retained");
        return adjusted;
    }

    internal static bool TryIncomingProjectileEvade(
        DesertBatfly bat,
        Player player,
        out Vector2 evadeGoal)
    {
        evadeGoal = default;
        if (bat?.room == null || player == null || player.room != bat.room)
            return false;

        int slot = DesertBatflyThreatRuntime.PlayerSlot(player);
        if (!DesertBatflyThreatRuntime.TryGetDebugState(
                bat,
                out DesertBatflyThreatDebugState threat) ||
            threat.Cue.PlayerSlot != slot ||
            !threat.Cue.ProjectileThreat ||
            threat.Cue.ProjectileThreatDirection.sqrMagnitude < 0.5f)
            return false;

        Vector2 projectileDirection = threat.Cue.ProjectileThreatDirection.normalized;
        Vector2 perpendicular = new Vector2(-projectileDirection.y, projectileDirection.x);
        Vector2 playerToBat = bat.mainBodyChunk.pos - player.mainBodyChunk.pos;
        float sideDot = Vector2.Dot(playerToBat, perpendicular);
        float side = Mathf.Abs(sideDot) > 1f
            ? Mathf.Sign(sideDot)
            : StableSide(bat, slot);
        Vector2 awayFromPlayer = Custom.DirVec(player.mainBodyChunk.pos, bat.mainBodyChunk.pos);
        evadeGoal = bat.mainBodyChunk.pos + perpendicular * side * 92f + awayFromPlayer * 34f;
        return true;
    }

    internal static bool TryProfile(
        DesertBatfly bat,
        Player player,
        out DB_ThreatTacticalProfile profile)
    {
        profile = default;
        if (bat?.DesertState == null || bat.room == null || player == null ||
            player.room != bat.room || player.dead)
            return false;

        int slot = DesertBatflyThreatRuntime.PlayerSlot(player);
        if (!DesertBatflyThreatRuntime.ValidSlot(slot)) return false;
        DB_PlayerThreatMemory memory = DB_ThreatMemoryStore.For(bat.DesertState, slot);
        if (memory == null || memory.Confidence <= 0.001f) return false;

        DB_WeaponPerception.TryObserveHeldThreats(
            bat,
            player,
            DB_Tuning.SightRange,
            out DB_HeldThreatObservation held);

        bool visibleSpear = held.VisibleSpear;
        bool visibleRock = held.VisibleRock;
        bool visibleExplosive = held.VisibleExplosive;
        bool visibleStartle = held.VisibleStartle;
        bool visibleShock = held.VisibleShock;

        float projectileRisk = Mathf.Clamp01(
            memory.ProjectilePressure * 0.35f + memory.PiercingPressure * 0.65f);
        float closeRisk = Mathf.Clamp01(
            memory.GrabCapturePressure * 0.55f +
            memory.ShockPressure * 0.25f +
            memory.BluntStunPressure * 0.20f);
        float explosionRisk = Mathf.Clamp01(
            memory.ExplosionPressure * 0.72f + memory.AreaDenialPressure * 0.28f);
        float counterRisk = memory.CounterKillPressure;
        float caution = Mathf.Clamp01(
            projectileRisk * 0.38f + closeRisk * 0.22f +
            explosionRisk * 0.22f + counterRisk * 0.18f);
        caution *= Mathf.Lerp(1.18f, 0.72f, bat.Personality.Nerve) *
                   Mathf.Lerp(0.72f, 1f, memory.Confidence);
        if (visibleSpear) caution += memory.PiercingPressure * 0.18f;
        if (visibleRock) caution += memory.BluntStunPressure * 0.08f;
        if (visibleExplosive) caution += memory.ExplosionPressure * 0.18f;
        if (visibleStartle) caution += memory.StartlePressure * 0.10f;
        if (visibleShock) caution += memory.ShockPressure * 0.12f;

        float confidenceRelief = memory.NonAggressionConfidence * 0.10f +
            memory.RetreatTendency * bat.Personality.Temperament * bat.Personality.Nerve * 0.12f;
        caution = Mathf.Clamp01(caution - confidenceRelief);

        profile = new DB_ThreatTacticalProfile(
            slot,
            memory.Confidence,
            projectileRisk,
            closeRisk,
            explosionRisk,
            counterRisk,
            caution,
            visibleSpear,
            visibleRock,
            visibleExplosive,
            visibleStartle,
            visibleShock);
        return true;
    }

    private static Player PlayerBySlot(Room room, int slot)
    {
        if (!DesertBatflyThreatRuntime.ValidSlot(slot)) return null;
        return DB_RoomContext.For(room)?.PlayerBySlot(slot);
    }

    private static float StableSide(DesertBatfly bat, int slot)
    {
        unchecked
        {
            uint x = (uint)(
                (bat?.Personality?.VisualSeed ?? 0) * 1103515245 +
                slot * 486187739 + 0x2C15 * 12345);
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 1u) == 0u ? -1f : 1f;
        }
    }

    private static void TraceAdjustment(
        DesertBatfly bat,
        string name,
        Vector2 goal,
        string reason)
    {
        if (bat?.abstractCreature == null || !AIDebugTrace.IsWatched(bat.abstractCreature))
            return;
        AIDebugTrace.RecordChange(
            bat.abstractCreature,
            AIDebugEventCategory.Combat,
            name,
            $"goal={goal.x:0.0},{goal.y:0.0}",
            reason);
    }
}
