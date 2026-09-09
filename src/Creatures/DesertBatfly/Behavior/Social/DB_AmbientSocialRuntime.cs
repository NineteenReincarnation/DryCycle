using System;
using System.Collections.Generic;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Background neutral social ecology for Desert Batflies.
///
/// Event interactions remain owned by <see cref="DB_SocialRuntime"/>. This layer fills the
/// long neutral gaps between those events with low-intensity peer drift, loose microflock
/// motion and occasional roost loitering. It never creates reservations, never advances
/// event SocialDrive/Cooldown, and only executes when the frame Arbiter selects Social.
///
/// Candidate discovery is consumed from DB_SocialRoomRuntime's shared 20-tick room cache.
/// Each selector samples only a bounded peer subset, so ambient life never performs an
/// unbounded all-bats scan for every realized bat every frame.
/// </summary>
internal static class DB_AmbientSocialRuntime
{
    internal const int AmbientSampleLimit = 8;
    internal const int ParticipationEpochTicks = 480;

    private const float AmbientRange = 255f;
    private const float FlockRange = 235f;
    private const float RoostLoiterRange = 260f;

    internal static float ParticipationProbability(DB_Personality personality)
    {
        if (personality == null) return 0f;
        return Mathf.Clamp(
            0.30f +
            personality.Conformity * 0.48f +
            (1f - personality.Temperament) * 0.08f +
            personality.RoostAffinity * 0.06f +
            personality.Nerve * 0.04f,
            0.28f,
            0.88f);
    }

    internal static float LooseFlockPreference(DB_Personality personality, int nearbyCandidates)
    {
        if (personality == null || nearbyCandidates < 2) return 0f;
        return Mathf.Clamp01(
            0.08f +
            personality.Conformity * 0.68f +
            (1f - personality.Temperament) * 0.12f +
            Mathf.InverseLerp(2f, 7f, nearbyCandidates) * 0.12f);
    }

    internal static float Commitment(DB_Personality personality)
        => Mathf.Lerp(0.26f, 0.58f, ParticipationProbability(personality));

    /// <summary>
    /// Decides whether a neutral frame should stay inside Social instead of falling through
    /// to Ordinary/vanilla. Event cooldown is intentionally ignored: cooldown limits discrete
    /// events, not the species' everyday social life.
    /// </summary>
    internal static bool ShouldOwn(in DB_FrameContext frame)
    {
        if (!frame.HasSocialState || !frame.SocialAllowed || !frame.Social.Eligible ||
            frame.Social.Mode != DB_SocialMode.None || frame.Social.CandidateCount <= 0 ||
            frame.Bat?.room == null || frame.Bat.mainBodyChunk == null || frame.Personality == null)
            return false;

        float participation = ParticipationProbability(frame.Personality);
        float environmentScale = Mathf.Clamp(
            DB_EnvironmentalPolicy.SocialDriveScale(frame.Bat), 0.25f, 1f);
        participation *= Mathf.Lerp(0.55f, 1f, environmentScale);

        int clock = Math.Max(0, frame.Clock);
        int epoch = clock / ParticipationEpochTicks;
        float phase = (clock % ParticipationEpochTicks) / (float)ParticipationEpochTicks;
        int seed = frame.Personality.VisualSeed;

        float identityGate = Stable01(seed, 0x6A51);
        float currentGate = Stable01(seed, epoch * 977 + 0x71B3);
        float nextGate = Stable01(seed, (epoch + 1) * 977 + 0x71B3);
        float rotatingGate = Mathf.Lerp(currentGate, nextGate, Mathf.SmoothStep(0f, 1f, phase));
        float gate = Mathf.Lerp(identityGate, rotatingGate, 0.35f);
        return gate <= participation;
    }

    internal static bool ApplyOwnedBehavior(DB_Creature bat)
    {
        if (bat?.room == null || bat.AI == null || bat.mainBodyChunk == null ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Social) ||
            !DB_SocialRuntime.TryGetDebugState(bat, out DB_SocialDebugState social) ||
            social.Mode != DB_SocialMode.None || !social.Eligible)
            return false;

        DB_SocialRoomRuntime.RoomState roomState = DB_SocialRoomRuntime.For(bat.room);
        IReadOnlyList<DB_Creature> candidates = roomState?.Candidates;
        if (candidates == null || candidates.Count <= 1) return false;

        int clock = Math.Max(0, bat.room.game?.clock ?? 0);
        int planEpoch = clock / 120;
        int side = Stable01(bat.Personality.VisualSeed, planEpoch * 31 + 0x219D) < 0.5f ? -1 : 1;

        float flockPreference = LooseFlockPreference(bat.Personality, social.CandidateCount);
        float flockRoll = Stable01(bat.Personality.VisualSeed, planEpoch * 193 + 0x4F31);
        if (flockRoll <= flockPreference &&
            TryBuildLooseFlockGoal(bat, candidates, planEpoch, side, out Vector2 flockGoal))
        {
            float speed = Mathf.Lerp(4.15f, 5.25f, bat.Personality.Nerve);
            return Guide(bat, flockGoal, speed, side);
        }

        float roostRoll = Stable01(bat.Personality.VisualSeed, planEpoch * 211 + 0x6C17);
        float roostChance = 0.08f + bat.Personality.RoostAffinity * 0.34f +
            bat.Personality.Conformity * 0.10f;
        if (roostRoll <= roostChance &&
            TryBuildRoostLoiterGoal(bat, roomState, planEpoch, side, out Vector2 roostGoal))
        {
            return Guide(bat, roostGoal, 4.15f, side);
        }

        if (TryBuildPeerDriftGoal(bat, candidates, planEpoch, side, out Vector2 peerGoal))
        {
            float speed = Mathf.Lerp(4.0f, 4.9f, bat.Personality.Nerve);
            return Guide(bat, peerGoal, speed, side);
        }

        return false;
    }

    private static bool TryBuildLooseFlockGoal(
        DB_Creature bat,
        IReadOnlyList<DB_Creature> candidates,
        int epoch,
        int side,
        out Vector2 goal)
    {
        goal = default;
        int peerCount = 0;
        int count = candidates.Count;
        int samples = Math.Min(AmbientSampleLimit, count);
        int start = StableInt(bat.Personality.VisualSeed, epoch * 83 + 0x3115, 0, count);

        Vector2 center = bat.mainBodyChunk.pos;
        Vector2 averageVelocity = bat.mainBodyChunk.vel;
        Vector2 separation = Vector2.zero;
        int contributors = 1;

        for (int i = 0; i < samples; i++)
        {
            DB_Creature peer = candidates[(start + i) % count];
            if (!AmbientPeer(bat, peer)) continue;
            Vector2 delta = peer.mainBodyChunk.pos - bat.mainBodyChunk.pos;
            float distance = delta.magnitude;
            if (distance > FlockRange ||
                (distance > 85f && !bat.room.VisualContact(bat.mainBodyChunk.pos, peer.mainBodyChunk.pos)))
                continue;

            center += peer.mainBodyChunk.pos;
            averageVelocity += peer.mainBodyChunk.vel;
            contributors++;
            peerCount++;

            if (distance > 0.01f && distance < 48f)
            {
                float strength = 1f - distance / 48f;
                Vector2 away = -delta / distance;
                separation.x += away.x * strength;
                separation.y += away.y * strength * 0.20f;
            }
        }

        if (peerCount < 2) return false;

        center /= contributors;
        averageVelocity /= contributors;
        Vector2 alignment = averageVelocity.sqrMagnitude > 0.4f
            ? averageVelocity.normalized * 46f
            : Vector2.right * side * 36f;
        Vector2 cohesion = center - bat.mainBodyChunk.pos;
        Vector2 offset = alignment + new Vector2(
            Mathf.Clamp(cohesion.x * 0.18f, -32f, 32f),
            Mathf.Clamp(cohesion.y * 0.05f, -8f, 8f)) + new Vector2(
            Mathf.Clamp(separation.x * 62f, -68f, 68f),
            Mathf.Clamp(separation.y * 18f, -7f, 7f));
        offset.x += side * 10f;
        goal = bat.mainBodyChunk.pos + offset;
        return true;
    }

    private static bool TryBuildPeerDriftGoal(
        DB_Creature bat,
        IReadOnlyList<DB_Creature> candidates,
        int epoch,
        int side,
        out Vector2 goal)
    {
        goal = default;
        DB_Creature best = null;
        float bestScore = 0f;
        int count = candidates.Count;
        int samples = Math.Min(AmbientSampleLimit, count);
        int start = StableInt(bat.Personality.VisualSeed, epoch * 101 + 0x52A7, 0, count);

        for (int i = 0; i < samples; i++)
        {
            DB_Creature peer = candidates[(start + i) % count];
            if (!AmbientPeer(bat, peer)) continue;
            float distance = Vector2.Distance(bat.mainBodyChunk.pos, peer.mainBodyChunk.pos);
            if (distance > AmbientRange ||
                (distance > 90f && !bat.room.VisualContact(bat.mainBodyChunk.pos, peer.mainBodyChunk.pos)))
                continue;

            float bond = Mathf.Max(
                DB_SocialBond.GetBondStrength(bat, peer),
                DB_SocialBond.GetBondStrength(peer, bat));
            float score = DB_SocialRuntime.PartnerPreference(
                bat.Personality.Conformity,
                1f - bat.Personality.Temperament,
                bond,
                Mathf.InverseLerp(45f, AmbientRange, distance));
            score *= 0.88f + Stable01(peer.Personality.VisualSeed, epoch * 59 + 0x221B) * 0.24f;
            if (score <= bestScore) continue;
            bestScore = score;
            best = peer;
        }

        if (best == null) return false;
        float bestBond = Mathf.Max(
            DB_SocialBond.GetBondStrength(bat, best),
            DB_SocialBond.GetBondStrength(best, bat));
        Vector2 backward = best.mainBodyChunk.vel.sqrMagnitude > 1f
            ? -best.mainBodyChunk.vel.normalized * 16f
            : Vector2.zero;
        Vector2 offset = DB_SocialRuntime.CompanionOffset(side, bestBond);
        offset.y *= 0.65f;
        goal = best.mainBodyChunk.pos + backward + offset;
        return true;
    }

    private static bool TryBuildRoostLoiterGoal(
        DB_Creature bat,
        DB_SocialRoomRuntime.RoomState roomState,
        int epoch,
        int side,
        out Vector2 goal)
    {
        goal = default;
        IReadOnlyList<DB_Creature> roosting = roomState?.Roosting;
        if (roosting == null || roosting.Count == 0) return false;

        DB_Creature best = null;
        float bestDistance = float.MaxValue;
        int count = roosting.Count;
        int samples = Math.Min(4, count);
        int start = StableInt(bat.Personality.VisualSeed, epoch * 71 + 0x61C9, 0, count);
        for (int i = 0; i < samples; i++)
        {
            DB_Creature peer = roosting[(start + i) % count];
            if (peer == null || peer == bat || peer.room != bat.room || peer.dead ||
                !peer.Consious || peer.mainBodyChunk == null || !SameRipple(bat, peer))
                continue;
            float distance = Vector2.Distance(bat.mainBodyChunk.pos, peer.mainBodyChunk.pos);
            if (distance > RoostLoiterRange || distance >= bestDistance) continue;
            if (distance > 100f && !bat.room.VisualContact(bat.mainBodyChunk.pos, peer.mainBodyChunk.pos))
                continue;
            best = peer;
            bestDistance = distance;
        }

        if (best == null) return false;
        float lateral = Mathf.Lerp(58f, 78f, 1f - bat.Personality.Conformity);
        goal = best.mainBodyChunk.pos + new Vector2(side * lateral, -30f);
        return true;
    }

    private static bool AmbientPeer(DB_Creature source, DB_Creature peer)
    {
        if (source == null || peer == null || source == peer || peer.room != source.room ||
            !DB_SocialRoomRuntime.ValidMember(peer) || !peer.Consious || peer.mainBodyChunk == null ||
            !SameRipple(source, peer))
            return false;

        return DB_SocialRuntime.TryGetDebugState(peer, out DB_SocialDebugState peerSocial) &&
               peerSocial.Eligible && peerSocial.Mode == DB_SocialMode.None;
    }

    private static bool SameRipple(DB_Creature a, DB_Creature b)
        => a?.abstractCreature != null && b?.abstractCreature != null &&
           (a.abstractCreature.rippleLayer == b.abstractCreature.rippleLayer ||
            a.abstractCreature.rippleBothSides || b.abstractCreature.rippleBothSides);

    private static bool Guide(
        DB_Creature bat,
        Vector2 goal,
        float speed,
        int preferredSide)
    {
        if (bat?.room == null || bat.AI == null || bat.mainBodyChunk == null ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Social))
            return false;

        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, goal);
        if (direction == Vector2.zero) return true;
        Vector2 probe = bat.mainBodyChunk.pos + direction * 25f;
        if (Obstructed(bat.room, probe))
        {
            Vector2 sideProbe = bat.mainBodyChunk.pos + Vector2.right * preferredSide * 30f;
            if (!Obstructed(bat.room, sideProbe))
                goal = bat.mainBodyChunk.pos + Vector2.right * preferredSide * 60f + Vector2.up * 5f;
            else
                goal = bat.mainBodyChunk.pos + Vector2.up * 64f;
            speed = Mathf.Min(speed, 4.5f);
        }

        bat.burrowOrHangSpot = null;
        if (bat.AI.behavior != FlyAI.Behavior.Idle)
            bat.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        bat.AI.followingDijkstraMap = -1;
        bat.movMode = Fly.MovementMode.BatFlight;

        return DB_FlightMotor.TryGuideNative(
            bat,
            DB_BehaviorOwner.Social,
            goal,
            speed);
    }

    private static bool Obstructed(Room room, Vector2 point)
    {
        if (room == null) return true;
        IntVector2 tile = room.GetTilePosition(point);
        if (room.GetTile(tile).Solid) return true;
        return room.terrain != null && room.terrain.ObstructsTile(tile);
    }

    private static float Stable01(int seed, int salt)
    {
        unchecked
        {
            uint x = (uint)(seed * 1103515245 + salt * 12345 + 0x6D2B79F5);
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 0x00FFFFFFu) / 16777215f;
        }
    }

    private static int StableInt(int seed, int salt, int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        return minInclusive + Mathf.FloorToInt(Stable01(seed, salt) * (maxExclusive - minInclusive));
    }
}
