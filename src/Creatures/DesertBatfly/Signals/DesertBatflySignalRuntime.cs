using System;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal static class DesertBatflySignalRuntime
{
    internal const int MaxAlarmHop = 2;
    internal const int AlarmTtlTicks = 135;
    internal const float AlarmHop1Scale = 0.56f;
    internal const float AlarmHop2Scale = 0.31f;

    private const int GenerationHistorySize = 12;
    private const int NeutralScanMinTicks = 14;
    private const int NeutralScanMaxTicks = 24;
    private const int SafeDelayTicks = 210;
    private const int NeutralSignalTtl = 90;

    private sealed class ReceiverState
    {
        internal readonly int[] Generations = new int[GenerationHistorySize];
        internal int GenerationCursor;
        internal bool GenerationInitialized;
        internal int NextScan;
        internal int ScanSerial;
        internal float AlarmPressure;
        internal Vector2 AlarmOrigin;
        internal Creature AlarmThreat;
        internal float DistressInterest;
        internal DesertBatfly DistressSource;
        internal float RallyInterest;
        internal DesertBatfly RallySource;
        internal Creature RallyTarget;
        internal float RoostInterest;
        internal DesertBatfly RoostSource;
        internal float HarassInterest;
        internal DesertBatfly HarassSource;
        internal Player HarassTarget;
        internal float SafeConfidence;
        internal int LastAlarmTick = int.MinValue;
        internal int LastSafeEmitTick = int.MinValue;
        internal int LastNeutralEmitTick = int.MinValue;
        internal int LastGeneration;
        internal DesertBatflySignalKind LastKind;
        internal DesertBatflySignalPerception LastPerception;
        internal int LastHop;
        internal string LastDecision = "no signal received";
        internal DesertBatflySignalDisplayState Display;
        internal int DisplayUntil = int.MinValue;
    }

    private static ConditionalWeakTable<DesertBatfly, ReceiverState> states = new();

    internal static void Reset()
    {
        states = new ConditionalWeakTable<DesertBatfly, ReceiverState>();
        DesertBatflySignalRoomRuntime.Reset();
        DesertBatflySignalIntegration.Reset();
    }

    internal static void Forget(DesertBatfly bat)
    {
        if (bat != null) states.Remove(bat);
    }

    internal static void Update(DesertBatfly bat)
    {
        if (bat == null) return;
        ReceiverState state = states.GetValue(bat, _ => new ReceiverState());
        TickInfluence(state);

        if (bat.dead || bat.slatedForDeletetion || !bat.Consious || bat.room == null || bat.inShortcut)
            return;

        int clock = bat.room.game?.clock ?? 0;
        EmitExistingBehaviorSignals(bat, state, clock);

        if (state.NextScan == 0)
            state.NextScan = clock + StableInt(bat, 0x2B19, NeutralScanMinTicks, NeutralScanMaxTicks + 1);
        if (clock < state.NextScan) return;
        state.NextScan = clock + StableInt(
            bat,
            0x41C7 + ++state.ScanSerial * 31,
            NeutralScanMinTicks,
            NeutralScanMaxTicks + 1);

        DesertBatflySignalRoomRuntime.RoomState room = DesertBatflySignalRoomRuntime.For(bat.room);
        room?.Prune(bat.room);
        if (room == null) return;

        for (int i = 0; i < room.ActiveSignals.Count; i++)
        {
            DesertBatflySignalPacket packet = room.ActiveSignals[i];
            if (packet == null || packet.Emitter == bat || packet.Expired(clock)) continue;
            ReceivePacket(bat, packet, out _);
        }
    }

    internal static DesertBatflySignalPacket EmitAlarm(
        DesertBatfly emitter,
        Creature threat,
        Vector2 origin,
        Vector2 direction,
        float intensity,
        string reason)
    {
        if (!Available(emitter)) return null;
        DesertBatflySignalRoomRuntime.RoomState room = DesertBatflySignalRoomRuntime.For(emitter.room);
        DesertBatflySignalPacket packet = room?.AddOrRefresh(
            emitter.room,
            DesertBatflySignalKind.AlarmFlutter,
            emitter,
            emitter,
            threat,
            threat as Player,
            origin,
            direction,
            intensity,
            AlarmTtlTicks);
        if (packet == null) return null;
        SetDisplay(emitter, DesertBatflySignalKind.AlarmFlutter, intensity, 38, direction);
        room.DeliverUrgent(emitter.room, packet);
        TraceEmit(emitter, packet, reason);
        return packet;
    }

    internal static DesertBatflySignalPacket EmitDistress(
        DesertBatfly emitter,
        Creature threat,
        float intensity,
        string reason)
    {
        if (!Available(emitter)) return null;
        ReceiverState emitterState = states.GetValue(emitter, _ => new ReceiverState());
        int clock = emitter.room.game?.clock ?? 0;
        if (clock - emitterState.LastNeutralEmitTick >= 0 && clock - emitterState.LastNeutralEmitTick < 24)
            return null;
        emitterState.LastNeutralEmitTick = clock;

        Vector2 origin = emitter.mainBodyChunk.pos;
        Vector2 direction = threat?.mainBodyChunk != null
            ? Custom.DirVec(origin, threat.mainBodyChunk.pos)
            : Vector2.zero;
        DesertBatflySignalRoomRuntime.RoomState room = DesertBatflySignalRoomRuntime.For(emitter.room);
        DesertBatflySignalPacket packet = room?.AddOrRefresh(
            emitter.room,
            DesertBatflySignalKind.DistressCall,
            emitter,
            emitter,
            threat,
            threat as Player,
            origin,
            direction,
            intensity,
            120);
        if (packet == null) return null;
        SetDisplay(emitter, DesertBatflySignalKind.DistressCall, intensity, 52, direction);
        room.DeliverUrgent(emitter.room, packet);
        TraceEmit(emitter, packet, reason);
        return packet;
    }

    internal static bool ReceivePacket(
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        out bool relayAlarm)
    {
        relayAlarm = false;
        if (!Available(receiver) || packet == null || packet.Emitter == receiver ||
            packet.Emitter?.room != receiver.room || packet.Expired(receiver.room.game?.clock ?? 0))
            return false;

        ReceiverState state = states.GetValue(receiver, _ => new ReceiverState());
        if (HasGeneration(state, packet.Generation)) return false;

        if (!TryPerceive(receiver, packet, out DesertBatflySignalPerception perception, out float attenuation))
            return false;

        RememberGeneration(state, packet.Generation);
        float response = ResponseStrength(receiver, packet, attenuation);
        state.LastGeneration = packet.Generation;
        state.LastKind = packet.Kind;
        state.LastPerception = perception;
        state.LastHop = packet.Hop;

        if (response <= 0.015f)
        {
            state.LastDecision = "perceived but personality/context response was negligible";
            TraceReceive(receiver, packet, perception, response, state.LastDecision);
            return true;
        }

        switch (packet.Kind)
        {
            case DesertBatflySignalKind.AlarmFlutter:
                state.AlarmPressure = Mathf.Max(state.AlarmPressure, response);
                state.AlarmOrigin = packet.Origin;
                state.AlarmThreat = packet.Threat;
                state.LastAlarmTick = receiver.room.game?.clock ?? 0;
                state.LastDecision = "accepted AlarmFlutter as short-term danger context";
                DesertBatflySocialLife.CancelForPriority(receiver, "Task12 AlarmFlutter");
                DesertBatflySignalIntegration.ApplyAlarm(receiver, packet, response);
                relayAlarm = packet.Hop < MaxAlarmHop && ShouldRelayAlarm(receiver, packet, response);
                break;

            case DesertBatflySignalKind.DistressCall:
                state.DistressInterest = Mathf.Max(state.DistressInterest, response);
                state.DistressSource = packet.Subject ?? packet.Emitter;
                state.LastDecision = "accepted DistressCall; existing rescue/vengeance systems retain authority";
                if (response >= 0.38f)
                    DesertBatflySocialLife.CancelForPriority(receiver, "Task12 DistressCall");
                break;

            case DesertBatflySignalKind.RallySignal:
                state.RallyInterest = Mathf.Max(state.RallyInterest, response);
                state.RallySource = packet.Emitter;
                state.RallyTarget = packet.Threat;
                state.LastDecision = "accepted RallySignal as supporter interest only";
                break;

            case DesertBatflySignalKind.RoostCall:
                state.RoostInterest = Mathf.Max(state.RoostInterest, response);
                state.RoostSource = packet.Emitter;
                state.LastDecision = "accepted RoostCall; Task10 still owns legal roost/reservation";
                break;

            case DesertBatflySignalKind.HarassSignal:
                state.HarassInterest = Mathf.Max(state.HarassInterest, response);
                state.HarassSource = packet.Emitter;
                state.HarassTarget = packet.PlayerTarget;
                state.LastDecision = "accepted HarassSignal as target interest only";
                break;

            case DesertBatflySignalKind.SafeSignal:
                if (CanAcceptSafe(receiver))
                {
                    state.SafeConfidence = Mathf.Max(state.SafeConfidence, response);
                    state.AlarmPressure *= Mathf.Lerp(1f, 0.48f, response);
                    state.LastDecision = "accepted SafeSignal; only signal-induced concern decays faster";
                }
                else
                {
                    state.LastDecision = "ignored SafeSignal because receiver still has direct danger";
                }
                break;
        }

        TraceReceive(receiver, packet, perception, response, state.LastDecision);
        return true;
    }

    internal static bool TryGetInfluence(DesertBatfly bat, out DesertBatflySignalInfluence influence)
    {
        influence = default;
        if (bat == null || !states.TryGetValue(bat, out ReceiverState state)) return false;
        influence = new DesertBatflySignalInfluence(
            state.AlarmPressure,
            state.AlarmOrigin,
            state.AlarmThreat,
            state.DistressInterest,
            state.DistressSource,
            state.RallyInterest,
            state.RallySource,
            state.RallyTarget,
            state.RoostInterest,
            state.RoostSource,
            state.HarassInterest,
            state.HarassSource,
            state.HarassTarget,
            state.SafeConfidence,
            state.LastDecision);
        return true;
    }

    internal static bool TryGetDisplay(DesertBatfly bat, out DesertBatflySignalDisplayState display)
    {
        display = default;
        if (bat == null || !states.TryGetValue(bat, out ReceiverState state)) return false;
        int clock = bat.room?.game?.clock ?? int.MaxValue;
        if (clock >= state.DisplayUntil) return false;
        display = new DesertBatflySignalDisplayState(
            state.Display.Kind,
            state.Display.Intensity,
            state.DisplayUntil - clock,
            state.Display.Direction);
        return true;
    }

    internal static bool TryGetDebugState(DesertBatfly bat, out DesertBatflySignalDebugState debug)
    {
        debug = default;
        if (!TryGetInfluence(bat, out DesertBatflySignalInfluence influence) ||
            !states.TryGetValue(bat, out ReceiverState state))
            return false;
        int count = DesertBatflySignalRoomRuntime.For(bat.room)?.Count ?? 0;
        debug = new DesertBatflySignalDebugState(
            influence,
            state.LastGeneration,
            state.LastKind,
            state.LastPerception,
            state.LastHop,
            state.LastDecision,
            count);
        return true;
    }

    private static void EmitExistingBehaviorSignals(DesertBatfly bat, ReceiverState state, int clock)
    {
        if (clock - state.LastNeutralEmitTick < 18) return;

        if (DesertBatflySignalIntegration.IsVengeanceAvenger(bat))
        {
            Creature target = DesertBatflySignalIntegration.VengeanceTarget(bat);
            EmitNeutral(bat, DesertBatflySignalKind.RallySignal, bat, target, target as Player, 0.70f, 70,
                "existing Avenger emits RallySignal");
            state.LastNeutralEmitTick = clock;
            return;
        }

        if (bat.AI?.behavior == FlyAI.Behavior.Chain || bat.movMode == Fly.MovementMode.Hang ||
            bat.DesertAI?.Mode == DesertBatflyAI.Activity.Roost)
        {
            EmitNeutral(bat, DesertBatflySignalKind.RoostCall, bat, null, null,
                Mathf.Lerp(0.42f, 0.78f, bat.Personality.RoostAffinity), 110,
                "committed legal roost emits RoostCall");
            state.LastNeutralEmitTick = clock;
            return;
        }

        if (bat.DesertAI?.Target is Player player && bat.DesertAI.Mode is
            DesertBatflyAI.Activity.Observe or DesertBatflyAI.Activity.Approach or
            DesertBatflyAI.Activity.Circle or DesertBatflyAI.Activity.FakeDive or
            DesertBatflyAI.Activity.Dive)
        {
            EmitNeutral(bat, DesertBatflySignalKind.HarassSignal, bat, player, player,
                Mathf.Lerp(0.34f, 0.74f, bat.Personality.AggressionDrive), 72,
                "existing formal harass posture emits HarassSignal");
            state.LastNeutralEmitTick = clock;
            return;
        }

        if (state.LastAlarmTick != int.MinValue && clock - state.LastAlarmTick >= SafeDelayTicks &&
            clock - state.LastSafeEmitTick >= SafeDelayTicks && CanAcceptSafe(bat) &&
            state.AlarmPressure > 0.04f)
        {
            EmitNeutral(bat, DesertBatflySignalKind.SafeSignal, bat, null, null,
                Mathf.Lerp(0.28f, 0.58f, bat.Personality.Nerve), 80,
                "recent local alarm has remained clear long enough for SafeSignal");
            state.LastSafeEmitTick = clock;
            state.LastNeutralEmitTick = clock;
        }
    }

    private static void EmitNeutral(
        DesertBatfly emitter,
        DesertBatflySignalKind kind,
        DesertBatfly subject,
        Creature threat,
        Player playerTarget,
        float intensity,
        int ttl,
        string reason)
    {
        if (!Available(emitter)) return;
        Vector2 origin = emitter.mainBodyChunk.pos;
        Vector2 direction = threat?.mainBodyChunk != null
            ? Custom.DirVec(origin, threat.mainBodyChunk.pos)
            : Vector2.zero;
        DesertBatflySignalPacket packet = DesertBatflySignalRoomRuntime.For(emitter.room)?.AddOrRefresh(
            emitter.room,
            kind,
            emitter,
            subject,
            threat,
            playerTarget,
            origin,
            direction,
            intensity,
            Mathf.Max(NeutralSignalTtl, ttl));
        if (packet == null) return;
        SetDisplay(emitter, kind, intensity, kind == DesertBatflySignalKind.RallySignal ? 42 : 30, direction);
        TraceEmit(emitter, packet, reason);
    }

    private static bool TryPerceive(
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        out DesertBatflySignalPerception perception,
        out float attenuation)
    {
        perception = DesertBatflySignalPerception.None;
        attenuation = 0f;
        float distance = Vector2.Distance(receiver.mainBodyChunk.pos, packet.Emitter.mainBodyChunk.pos);
        float visualRadius = VisualRadius(packet.Kind);
        if (distance <= visualRadius && receiver.room.VisualContact(
                receiver.mainBodyChunk.pos,
                packet.Emitter.mainBodyChunk.pos))
        {
            perception = DesertBatflySignalPerception.Visual;
            attenuation = Mathf.Lerp(1f, 0.34f, Mathf.Clamp01(distance / Mathf.Max(1f, visualRadius)));
            return true;
        }

        float acoustic = packet.Kind switch
        {
            DesertBatflySignalKind.AlarmFlutter => 95f,
            DesertBatflySignalKind.DistressCall => 108f,
            _ => 0f
        };
        if (acoustic > 0f && distance <= acoustic)
        {
            perception = DesertBatflySignalPerception.CloseAcoustic;
            attenuation = Mathf.Lerp(0.62f, 0.30f, Mathf.Clamp01(distance / acoustic));
            return true;
        }
        return false;
    }

    private static float VisualRadius(DesertBatflySignalKind kind) => kind switch
    {
        DesertBatflySignalKind.AlarmFlutter => 300f,
        DesertBatflySignalKind.DistressCall => 250f,
        DesertBatflySignalKind.RallySignal => 235f,
        DesertBatflySignalKind.RoostCall => 215f,
        DesertBatflySignalKind.HarassSignal => 235f,
        DesertBatflySignalKind.SafeSignal => 195f,
        _ => 200f
    };

    private static float ResponseStrength(
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        float attenuation)
    {
        float c = receiver.Personality.Conformity;
        float n = receiver.Personality.Nerve;
        float t = receiver.Personality.Temperament;
        float bond = packet.Emitter != null
            ? DesertBatflySocialBond.GetBondStrength(receiver, packet.Emitter)
            : 0f;
        float scale = packet.Kind switch
        {
            DesertBatflySignalKind.AlarmFlutter =>
                Mathf.Lerp(0.72f, 1.22f, c) * Mathf.Lerp(1.18f, 0.72f, n),
            DesertBatflySignalKind.DistressCall =>
                0.62f + bond * 0.52f + t * 0.20f + n * 0.18f,
            DesertBatflySignalKind.RallySignal =>
                0.38f + t * 0.34f + n * 0.28f + c * 0.20f + bond * 0.18f,
            DesertBatflySignalKind.RoostCall =>
                0.42f + c * 0.34f + receiver.Personality.RoostAffinity * 0.38f + bond * 0.18f,
            DesertBatflySignalKind.HarassSignal =>
                0.28f + t * 0.38f + n * 0.25f + c * 0.17f,
            DesertBatflySignalKind.SafeSignal =>
                0.38f + n * 0.34f + c * 0.28f,
            _ => 1f
        };
        return Mathf.Clamp01(packet.Intensity * attenuation * scale);
    }

    private static bool ShouldRelayAlarm(
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        float response)
    {
        if (packet.Hop >= MaxAlarmHop || response < 0.24f) return false;
        float chance = Mathf.Clamp01(
            0.14f + receiver.Personality.Conformity * 0.58f +
            (1f - receiver.Personality.Nerve) * 0.18f + response * 0.18f);
        return Stable01(receiver, packet.Generation ^ (packet.Hop + 1) * 0x6D2B) < chance;
    }

    private static bool CanAcceptSafe(DesertBatfly bat)
    {
        if (!Available(bat) || !bat.Consious || bat.DesertAI == null) return false;
        if (bat.DesertAI.HasImmediateDanger || bat.DesertAI.Mode == DesertBatflyAI.Activity.Escape)
            return false;
        if (bat.Injury.IsSeverelyInjured || DesertBatflyTravelNavigation.HasIntent(bat.abstractCreature))
            return false;
        if (DesertBatflyThreatRuntime.TryGetDebugState(bat, out DesertBatflyThreatDebugState threat) &&
            (threat.Cue.ProjectileThreat || threat.AcuteExplosion > 0 ||
             threat.AcuteStartle > 0 || threat.AcuteMassCasualty > 0 || threat.AcuteCapture > 0))
            return false;
        return !DesertBatflyIntimidation.HasActiveFearSuppression(bat);
    }

    private static void TickInfluence(ReceiverState state)
    {
        state.AlarmPressure = Mathf.Max(0f, state.AlarmPressure - 0.0032f);
        state.DistressInterest = Mathf.Max(0f, state.DistressInterest - 0.0050f);
        state.RallyInterest = Mathf.Max(0f, state.RallyInterest - 0.0038f);
        state.RoostInterest = Mathf.Max(0f, state.RoostInterest - 0.0022f);
        state.HarassInterest = Mathf.Max(0f, state.HarassInterest - 0.0030f);
        state.SafeConfidence = Mathf.Max(0f, state.SafeConfidence - 0.0035f);
        if (state.DistressInterest <= 0f) state.DistressSource = null;
        if (state.RallyInterest <= 0f) { state.RallySource = null; state.RallyTarget = null; }
        if (state.RoostInterest <= 0f) state.RoostSource = null;
        if (state.HarassInterest <= 0f) { state.HarassSource = null; state.HarassTarget = null; }
    }

    private static bool HasGeneration(ReceiverState state, int generation)
    {
        if (!state.GenerationInitialized) return false;
        for (int i = 0; i < state.Generations.Length; i++)
            if (state.Generations[i] == generation) return true;
        return false;
    }

    private static void RememberGeneration(ReceiverState state, int generation)
    {
        if (!state.GenerationInitialized)
        {
            for (int i = 0; i < state.Generations.Length; i++) state.Generations[i] = int.MinValue;
            state.GenerationInitialized = true;
        }
        state.Generations[state.GenerationCursor] = generation;
        state.GenerationCursor = (state.GenerationCursor + 1) % state.Generations.Length;
    }

    private static void SetDisplay(
        DesertBatfly emitter,
        DesertBatflySignalKind kind,
        float intensity,
        int ticks,
        Vector2 direction)
    {
        ReceiverState state = states.GetValue(emitter, _ => new ReceiverState());
        int clock = emitter.room?.game?.clock ?? 0;
        state.Display = new DesertBatflySignalDisplayState(kind, intensity, ticks, direction);
        state.DisplayUntil = Mathf.Max(state.DisplayUntil, clock + Mathf.Max(1, ticks));
    }

    private static bool Available(DesertBatfly bat) =>
        bat != null && !bat.dead && !bat.slatedForDeletetion && bat.room != null && !bat.inShortcut;

    private static int StableInt(DesertBatfly bat, int salt, int min, int max)
    {
        if (max <= min) return min;
        return min + Mathf.FloorToInt(Stable01(bat, salt) * (max - min));
    }

    private static float Stable01(DesertBatfly bat, int salt)
    {
        unchecked
        {
            uint x = (uint)((bat?.Personality?.VisualSeed ?? 0) * 1103515245 + salt * 12345);
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 0x00FFFFFFu) / 16777216f;
        }
    }

    private static void TraceEmit(DesertBatfly emitter, DesertBatflySignalPacket packet, string reason)
    {
        if (emitter?.abstractCreature == null || !DryCycle.Debugging.AI.AIDebugTrace.IsWatched(emitter.abstractCreature))
            return;
        DryCycle.Debugging.AI.AIDebugTrace.Record(
            emitter.abstractCreature,
            DryCycle.Debugging.AI.AIDebugEventCategory.Social,
            "Task12SignalEmitted",
            $"{packet.Kind} gen={packet.Generation} hop={packet.Hop}",
            reason ?? string.Empty);
    }

    private static void TraceReceive(
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        DesertBatflySignalPerception perception,
        float response,
        string reason)
    {
        if (receiver?.abstractCreature == null || !DryCycle.Debugging.AI.AIDebugTrace.IsWatched(receiver.abstractCreature))
            return;
        DryCycle.Debugging.AI.AIDebugTrace.Record(
            receiver.abstractCreature,
            DryCycle.Debugging.AI.AIDebugEventCategory.Social,
            "Task12SignalReceived",
            $"{packet.Kind} gen={packet.Generation} hop={packet.Hop} via={perception} response={response:0.00}",
            reason ?? string.Empty);
    }
}
