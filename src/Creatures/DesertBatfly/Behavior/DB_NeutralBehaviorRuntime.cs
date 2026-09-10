using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum DB_NeutralMode
{
    Roam,
    PeerDrift,
    LooseFlock,
    ShortSwarm
}

/// <summary>
/// Single state machine for low-priority Desert Batfly ecology. Formal Social events stay in
/// DB_SocialRuntime; this runtime owns only short peer drift, loose flock and custom swarm
/// windows. Native FlyAI.Swarm is intentionally not used.
///
/// State transitions are refreshed once before arbitration. ShouldOwn is deliberately pure so
/// repeated ResolveFrame calls in one frame cannot advance timers, reroll choices or create a
/// different neutral behavior after another executor yields.
/// </summary>
internal static class DB_NeutralBehaviorRuntime
{
    internal const int SampleLimit = 8;
    private const float PeerRange = 245f;
    private const float FlockRange = 225f;
    private const float SeparationRadius = 74f;

    private sealed class State
    {
        internal Room Room;
        internal DB_NeutralMode Mode;
        internal int Started;
        internal int Until;
        internal int NextDecision;
        internal int LastRefresh = int.MinValue;
        internal int Serial;
        internal int PeerCooldown;
        internal int FlockCooldown;
        internal int SwarmCooldown;
        internal int Side = 1;
        internal DB_Creature Peer;
        internal Vector2 Anchor;
    }

    private static ConditionalWeakTable<DB_Creature, State> states = new();

    internal static void Reset() => states = new ConditionalWeakTable<DB_Creature, State>();
    internal static void Forget(DB_Creature bat) { if (bat != null) states.Remove(bat); }
    internal static bool AllowsNativeSwarm(DB_Creature bat) => false;

    internal static float ParticipationProbability(DB_Personality p)
    {
        if (p == null) return 0f;
        return Mathf.Clamp(
            0.17f + p.Conformity * 0.30f + (1f - p.Temperament) * 0.07f + p.Nerve * 0.04f,
            0.16f,
            0.58f);
    }

    internal static float LooseFlockPreference(DB_Personality p, int nearby)
    {
        if (p == null || nearby < 2) return 0f;
        return Mathf.Clamp01(
            0.10f + p.Conformity * 0.52f + (1f - p.Temperament) * 0.10f +
            Mathf.InverseLerp(2f, 7f, nearby) * 0.10f);
    }

    internal static float Commitment(DB_Personality p)
        => Mathf.Lerp(0.24f, 0.46f, ParticipationProbability(p));

    /// <summary>
    /// Advances the neutral state machine exactly once for the current game clock. This must run
    /// after Social refresh and before DB_BehaviorArbiter.ResolveFrame.
    /// </summary>
    internal static void RefreshState(DB_Creature bat)
    {
        if (bat?.room == null) return;
        DB_FrameContext frame = DB_FrameContextRuntime.For(bat, refresh: true);
        RefreshState(frame);
    }

    internal static void RefreshState(in DB_FrameContext frame)
    {
        DB_Creature bat = frame.Bat;
        int clock = Math.Max(0, frame.Clock);
        if (bat?.room == null || bat.mainBodyChunk == null || frame.Personality == null)
            return;

        State state = For(bat, clock);
        if (state.LastRefresh == clock) return;
        state.LastRefresh = clock;

        if (!EligibleFrame(frame))
        {
            if (state.Mode != DB_NeutralMode.Roam)
                ToRoam(bat, state, clock, 70, 170);
            return;
        }

        if (state.Mode != DB_NeutralMode.Roam)
        {
            if (clock < state.Until) return;
            ToRoam(bat, state, clock, 55, 150);
        }
        if (clock < state.NextDecision) return;

        int serial = ++state.Serial;
        int seed = Seed(bat);
        state.NextDecision = clock + StableInt(seed, serial * 977 + 0x212D, 70, 181);
        float env = Mathf.Clamp(DB_EnvironmentalPolicy.SocialDriveScale(bat), 0.25f, 1f);
        float participate = ParticipationProbability(bat.Personality) * Mathf.Lerp(0.55f, 1f, env);
        if (Stable01(seed, serial * 193 + 0x6D31) > participate) return;

        float peer = clock >= state.PeerCooldown
            ? 0.24f + bat.Personality.Conformity * 0.22f + (1f - bat.Personality.Temperament) * 0.08f
            : 0f;
        float flock = frame.Social.CandidateCount >= 2 && clock >= state.FlockCooldown
            ? LooseFlockPreference(bat.Personality, frame.Social.CandidateCount) * 0.72f
            : 0f;
        float swarm = frame.Social.CandidateCount >= 2 && clock >= state.SwarmCooldown &&
                      DB_SwarmRoom.IsDB_SwarmRoom(bat.room.abstractRoom)
            ? 0.05f + bat.Personality.Conformity * 0.16f
            : 0f;
        float total = peer + flock + swarm;
        if (total <= 0.001f) return;

        float pick = Stable01(seed, serial * 1237 + 0x4E21) * total;
        if ((pick -= peer) < 0f)
        {
            Begin(bat, state, DB_NeutralMode.PeerDrift, clock,
                StableInt(seed, serial * 43 + 0x5311, 32, 73));
            state.PeerCooldown = state.Until + StableInt(seed, serial * 59 + 0x6323, 120, 321);
        }
        else if ((pick -= flock) < 0f)
        {
            Begin(bat, state, DB_NeutralMode.LooseFlock, clock,
                StableInt(seed, serial * 67 + 0x2741, 45, 111));
            state.FlockCooldown = state.Until + StableInt(seed, serial * 71 + 0x7331, 180, 421);
        }
        else
        {
            Begin(bat, state, DB_NeutralMode.ShortSwarm, clock,
                StableInt(seed, serial * 83 + 0x3559, 28, 76));
            state.SwarmCooldown = state.Until + StableInt(seed, serial * 89 + 0x7A13, 320, 721);
        }
    }

    /// <summary>Pure proposal query. It never changes state.</summary>
    internal static bool ShouldOwn(in DB_FrameContext frame)
    {
        DB_Creature bat = frame.Bat;
        if (!EligibleFrame(frame) || bat?.room == null ||
            !states.TryGetValue(bat, out State state) || !ReferenceEquals(state.Room, bat.room))
            return false;
        return state.Mode != DB_NeutralMode.Roam && frame.Clock < state.Until;
    }

    internal static bool ApplyOwnedBehavior(DB_Creature bat)
    {
        if (bat?.room == null || bat.AI == null || bat.mainBodyChunk == null ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.NeutralEcology) ||
            !states.TryGetValue(bat, out State state) || state.Mode == DB_NeutralMode.Roam)
            return false;

        int clock = Math.Max(0, bat.room.game?.clock ?? 0);
        if (!ReferenceEquals(state.Room, bat.room) || clock >= state.Until)
            return false;

        IReadOnlyList<DB_Creature> candidates = DB_SocialRoomRuntime.For(bat.room)?.Candidates;
        if (candidates == null || candidates.Count <= 1)
            return false;

        Vector2 goal;
        switch (state.Mode)
        {
            case DB_NeutralMode.PeerDrift:
                if (!PeerGoal(bat, state, candidates, out goal)) return false;
                break;
            case DB_NeutralMode.LooseFlock:
                if (!FlockGoal(bat, state, candidates, out goal)) return false;
                break;
            case DB_NeutralMode.ShortSwarm:
                goal = SwarmGoal(bat, state, candidates, clock);
                break;
            default:
                return false;
        }

        float speed = state.Mode == DB_NeutralMode.ShortSwarm
            ? Mathf.Lerp(4.0f, 4.8f, bat.Personality.Nerve)
            : Mathf.Lerp(4.2f, 5.2f, bat.Personality.Nerve);
        return Guide(bat, goal, speed, state.Side);
    }

    internal static void CancelForPriority(DB_Creature bat)
    {
        if (bat?.room == null || !states.TryGetValue(bat, out State state) ||
            state.Mode == DB_NeutralMode.Roam)
            return;
        ToRoam(bat, state, Math.Max(0, bat.room.game?.clock ?? 0), 70, 170);
    }

    private static bool EligibleFrame(in DB_FrameContext frame)
        => frame.Bat?.room != null && frame.Bat.mainBodyChunk != null && frame.Personality != null &&
           frame.HasSocialState && frame.SocialAllowed && frame.Social.Eligible &&
           frame.Social.Mode == DB_SocialMode.None && frame.Social.CandidateCount > 0;

    private static State For(DB_Creature bat, int clock)
    {
        State state = states.GetOrCreateValue(bat);
        if (ReferenceEquals(state.Room, bat.room)) return state;
        state.Room = bat.room;
        state.Mode = DB_NeutralMode.Roam;
        state.Started = clock;
        state.Until = 0;
        state.NextDecision = clock + StableInt(Seed(bat), 0x4117, 70, 181);
        state.LastRefresh = int.MinValue;
        state.Serial = 0;
        state.PeerCooldown = state.FlockCooldown = state.SwarmCooldown = 0;
        state.Peer = null;
        state.Anchor = bat.mainBodyChunk.pos;
        state.Side = Stable01(Seed(bat), 0x51A9) < 0.5f ? -1 : 1;
        return state;
    }

    private static void Begin(DB_Creature bat, State state, DB_NeutralMode mode, int clock, int duration)
    {
        state.Mode = mode;
        state.Started = clock;
        state.Until = clock + Math.Max(1, duration);
        state.Side = Stable01(Seed(bat), state.Serial * 101 + 0x1823) < 0.5f ? -1 : 1;
        state.Peer = null;
        state.Anchor = bat.mainBodyChunk.pos;
    }

    private static void ToRoam(DB_Creature bat, State state, int clock, int minDelay, int maxDelay)
    {
        state.Mode = DB_NeutralMode.Roam;
        state.Started = clock;
        state.Until = 0;
        state.Peer = null;
        state.NextDecision = Math.Max(state.NextDecision,
            clock + StableInt(Seed(bat), state.Serial * 109 + 0x1937, minDelay, maxDelay + 1));
    }

    private static bool PeerGoal(DB_Creature bat, State state, IReadOnlyList<DB_Creature> peers, out Vector2 goal)
    {
        goal = default;
        if (!ValidPeer(bat, state.Peer)) state.Peer = SelectPeer(bat, peers, state.Serial);
        DB_Creature peer = state.Peer;
        if (!ValidPeer(bat, peer)) return false;
        Vector2 delta = peer.mainBodyChunk.pos - bat.mainBodyChunk.pos;
        float distance = delta.magnitude;
        if (distance <= 0.01f || distance > PeerRange) return false;
        if (distance > 95f && !bat.room.VisualContact(bat.mainBodyChunk.pos, peer.mainBodyChunk.pos)) return false;

        if (distance < 50f)
        {
            Vector2 away = -delta / distance;
            Vector2 tangent = new Vector2(-away.y, away.x) * state.Side;
            goal = bat.mainBodyChunk.pos + away * 92f + tangent * 36f;
            return true;
        }

        float bond = Mathf.Max(DB_SocialBond.GetBondStrength(bat, peer), DB_SocialBond.GetBondStrength(peer, bat));
        Vector2 back = peer.mainBodyChunk.vel.sqrMagnitude > 1f
            ? -peer.mainBodyChunk.vel.normalized * 18f
            : Vector2.zero;
        float vertical = StableRange(Seed(bat), state.Serial * 131 + 0x2517, -16f, 16f);
        goal = peer.mainBodyChunk.pos + back +
               new Vector2(state.Side * Mathf.Lerp(54f, 72f, 1f - bond), vertical);
        return true;
    }

    private static DB_Creature SelectPeer(DB_Creature bat, IReadOnlyList<DB_Creature> peers, int serial)
    {
        DB_Creature best = null;
        float bestScore = float.MinValue;
        int samples = Math.Min(SampleLimit, peers.Count);
        int start = StableInt(Seed(bat), serial * 137 + 0x3151, 0, peers.Count);
        for (int i = 0; i < samples; i++)
        {
            DB_Creature peer = peers[(start + i) % peers.Count];
            if (!ValidPeer(bat, peer)) continue;
            float distance = Vector2.Distance(bat.mainBodyChunk.pos, peer.mainBodyChunk.pos);
            if (distance < 24f || distance > PeerRange ||
                (distance > 95f && !bat.room.VisualContact(bat.mainBodyChunk.pos, peer.mainBodyChunk.pos)))
                continue;
            float bond = Mathf.Max(DB_SocialBond.GetBondStrength(bat, peer), DB_SocialBond.GetBondStrength(peer, bat));
            float score = (1f - Mathf.Clamp01(Mathf.Abs(distance - 105f) / 140f)) * 0.65f + bond * 0.35f;
            if (score > bestScore)
            {
                bestScore = score;
                best = peer;
            }
        }
        return best;
    }

    private static bool FlockGoal(DB_Creature bat, State state, IReadOnlyList<DB_Creature> peers, out Vector2 goal)
    {
        goal = default;
        Vector2 center = Vector2.zero;
        Vector2 velocity = Vector2.zero;
        Vector2 separation = Vector2.zero;
        int contributors = 0;
        int samples = Math.Min(SampleLimit, peers.Count);
        int start = StableInt(Seed(bat), state.Serial * 157 + 0x5129, 0, peers.Count);
        for (int i = 0; i < samples; i++)
        {
            DB_Creature peer = peers[(start + i) % peers.Count];
            if (!ValidPeer(bat, peer)) continue;
            Vector2 delta = peer.mainBodyChunk.pos - bat.mainBodyChunk.pos;
            float distance = delta.magnitude;
            if (distance <= 0.01f || distance > FlockRange ||
                (distance > 90f && !bat.room.VisualContact(bat.mainBodyChunk.pos, peer.mainBodyChunk.pos)))
                continue;
            center += peer.mainBodyChunk.pos;
            velocity += peer.mainBodyChunk.vel;
            contributors++;
            if (distance < SeparationRadius)
            {
                float strength = Mathf.Pow(1f - distance / SeparationRadius, 2f);
                Vector2 away = -delta / distance;
                separation += new Vector2(away.x, away.y * 0.78f) * strength;
            }
        }
        if (contributors < 2) return false;
        center /= contributors;
        velocity /= contributors;
        Vector2 align = velocity.sqrMagnitude > 0.5f
            ? velocity.normalized * 38f
            : Vector2.right * state.Side * 32f;
        align.y *= 0.48f;
        if (Mathf.Abs(align.x) < 16f) align.x = state.Side * 16f;
        Vector2 cohesion = center - bat.mainBodyChunk.pos;
        Vector2 offset = align + new Vector2(
            Mathf.Clamp(cohesion.x * 0.12f, -24f, 24f),
            Mathf.Clamp(cohesion.y * 0.10f, -14f, 14f));
        offset += new Vector2(
            Mathf.Clamp(separation.x * 92f, -105f, 105f),
            Mathf.Clamp(separation.y * 82f, -92f, 92f));
        offset.x += state.Side * 12f;
        goal = bat.mainBodyChunk.pos + offset;
        return true;
    }

    private static Vector2 SwarmGoal(DB_Creature bat, State state, IReadOnlyList<DB_Creature> peers, int clock)
    {
        int elapsed = Math.Max(0, clock - state.Started);
        float phase = elapsed * 0.105f +
            StableRange(Seed(bat), state.Serial * 163 + 0x6197, 0f, 6.2831855f);
        Vector2 goal = state.Anchor + new Vector2(
            Mathf.Sin(phase) * 46f + state.Side * (24f + elapsed * 0.55f),
            Mathf.Sin(phase * 0.71f + 1.2f) * 18f);
        Vector2 separation = Separation(bat, peers);
        goal += new Vector2(separation.x * 78f, separation.y * 68f);
        return Custom.RestrictInRect(
            goal,
            new FloatRect(18f, 18f, bat.room.PixelWidth - 18f, bat.room.PixelHeight - 18f));
    }

    private static Vector2 Separation(DB_Creature bat, IReadOnlyList<DB_Creature> peers)
    {
        Vector2 result = Vector2.zero;
        int samples = Math.Min(SampleLimit, peers.Count);
        int start = StableInt(Seed(bat), (bat.room.game?.clock ?? 0) / 20 + 0x7331, 0, peers.Count);
        for (int i = 0; i < samples; i++)
        {
            DB_Creature peer = peers[(start + i) % peers.Count];
            if (!ValidPeer(bat, peer)) continue;
            Vector2 delta = bat.mainBodyChunk.pos - peer.mainBodyChunk.pos;
            float distance = delta.magnitude;
            if (distance <= 0.01f || distance >= SeparationRadius) continue;
            Vector2 away = delta / distance;
            float strength = 1f - distance / SeparationRadius;
            result += new Vector2(away.x, away.y * 0.82f) * strength;
        }
        return result;
    }

    private static bool Guide(DB_Creature bat, Vector2 goal, float speed, int side)
    {
        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, goal);
        if (direction == Vector2.zero) return true;
        if (Obstructed(bat.room, bat.mainBodyChunk.pos + direction * 26f))
        {
            Vector2 preferred = Vector2.right * (side == 0 ? 1 : side);
            if (!Obstructed(bat.room, bat.mainBodyChunk.pos + preferred * 32f))
                goal = bat.mainBodyChunk.pos + preferred * 66f;
            else if (!Obstructed(bat.room, bat.mainBodyChunk.pos - preferred * 32f))
                goal = bat.mainBodyChunk.pos - preferred * 66f;
            else if (!Obstructed(bat.room, bat.mainBodyChunk.pos + Vector2.down * 32f))
                goal = bat.mainBodyChunk.pos + Vector2.down * 60f;
            else if (!Obstructed(bat.room, bat.mainBodyChunk.pos + Vector2.up * 32f))
                goal = bat.mainBodyChunk.pos + Vector2.up * 60f;
            else
                return false;
        }
        bat.burrowOrHangSpot = null;
        if (bat.AI.behavior != FlyAI.Behavior.Idle)
            bat.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        bat.AI.followingDijkstraMap = -1;
        bat.movMode = Fly.MovementMode.BatFlight;
        return DB_FlightMotor.TryGuideNative(
            bat,
            DB_BehaviorOwner.NeutralEcology,
            goal,
            speed);
    }

    private static bool Obstructed(Room room, Vector2 point)
    {
        IntVector2 tile = room.GetTilePosition(point);
        if (tile.x < 0 || tile.y < 0 || tile.x >= room.TileWidth || tile.y >= room.TileHeight)
            return true;
        return room.GetTile(tile).Solid ||
               (room.terrain != null && room.terrain.ObstructsTile(tile));
    }

    private static bool ValidPeer(DB_Creature source, DB_Creature peer)
    {
        if (source == null || peer == null || source == peer || peer.room != source.room ||
            !DB_SocialRoomRuntime.ValidMember(peer) || !peer.Consious || peer.mainBodyChunk == null ||
            source.abstractCreature == null || peer.abstractCreature == null)
            return false;
        if (source.abstractCreature.rippleLayer != peer.abstractCreature.rippleLayer &&
            !source.abstractCreature.rippleBothSides && !peer.abstractCreature.rippleBothSides)
            return false;
        return DB_SocialRuntime.TryGetDebugState(peer, out DB_SocialDebugState social) &&
               social.Eligible && social.Mode == DB_SocialMode.None;
    }

    private static int Seed(DB_Creature bat)
        => bat?.Personality?.VisualSeed ?? bat?.abstractCreature?.ID.RandomSeed ?? 0;

    private static float StableRange(int seed, int salt, float min, float max)
        => Mathf.Lerp(min, max, Stable01(seed, salt));

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

    private static int StableInt(int seed, int salt, int min, int max)
        => max <= min ? min : min + Mathf.FloorToInt(Stable01(seed, salt) * (max - min));
}
