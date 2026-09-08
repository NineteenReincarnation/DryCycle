using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal static class DB_SignalRuntime
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
    // Retained from the retired SignalIntegration/SignalThreatBridge split:
    // a concrete Creature alarm may trigger Escape at 0.30, while an anonymous
    // hazard needs the old stricter 0.34 confidence before creating Escape.
    private const float ThreatAlarmEscapeThreshold = 0.30f;
    private const float AnonymousAlarmEscapeThreshold = 0.34f;

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
        internal DB_Creature DistressSource;
        internal float RallyInterest;
        internal DB_Creature RallySource;
        internal Creature RallyTarget;
        internal float RoostInterest;
        internal DB_Creature RoostSource;
        internal float HarassInterest;
        internal DB_Creature HarassSource;
        internal Player HarassTarget;
        internal float SafeConfidence;

        internal int LastAlarmTick = int.MinValue;
        internal int LastSafeEmitTick = int.MinValue;
        internal int LastNeutralEmitTick = int.MinValue;
        internal int LastDistressEmitTick = int.MinValue;

        internal int LastGeneration;
        internal DB_SignalKind LastKind;
        internal DB_SignalPerception LastPerception;
        internal int LastHop;
        internal string LastDecision = "no signal received";

        internal DB_SignalDisplayState Display;
        internal int DisplayUntil = int.MinValue;
    }

    private static ConditionalWeakTable<DB_Creature, ReceiverState> states = new();

    internal static void Reset()
    {
        states = new ConditionalWeakTable<DB_Creature, ReceiverState>();
        DesertBatflySignalRoomRuntime.Reset();
    }

    internal static void Forget(DB_Creature bat)
    {
        if (bat != null) states.Remove(bat);
    }

    internal static void Update(DB_Creature bat)
    {
        if (bat == null) return;
        ReceiverState state = states.GetValue(bat, _ => new ReceiverState());
        TickInfluence(state);

        if (!Available(bat) || !bat.Consious) return;

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

        DesertBatflySignalRoomRuntime.RoomState roomState =
            DesertBatflySignalRoomRuntime.For(bat.room);
        roomState?.Prune(bat.room);
        if (roomState == null) return;

        for (int i = 0; i < roomState.ActiveSignals.Count; i++)
        {
            DB_SignalPacket packet = roomState.ActiveSignals[i];
            if (packet == null || packet.Emitter == bat || packet.Expired(clock)) continue;
            ReceivePacket(bat, packet, out _);
        }
    }

    internal static DB_SignalPacket EmitAlarm(
        DB_Creature emitter,
        Creature threat,
        Vector2 origin,
        Vector2 direction,
        float intensity,
        string reason)
    {
        if (!Available(emitter)) return null;
        DesertBatflySignalRoomRuntime.RoomState room = DesertBatflySignalRoomRuntime.For(emitter.room);
        DB_SignalPacket packet = room?.AddOrRefresh(
            emitter.room,
            DB_SignalKind.AlarmFlutter,
            emitter,
            emitter,
            threat,
            threat as Player,
            origin,
            direction,
            intensity,
            AlarmTtlTicks);
        if (packet == null) return null;

        SetDisplay(emitter, DB_SignalKind.AlarmFlutter, intensity, 38, direction);
        room.DeliverUrgent(emitter.room, packet);
        TraceEmit(emitter, packet, reason);
        return packet;
    }


    internal static DB_SignalPacket EmitRally(
        DB_Creature emitter,
        Creature threat,
        float drive,
        string reason)
    {
        if (!Available(emitter) || threat == null) return null;
        Vector2 origin = emitter.mainBodyChunk.pos;
        Vector2 direction = threat.mainBodyChunk != null
            ? Custom.DirVec(origin, threat.mainBodyChunk.pos)
            : Vector2.zero;
        DesertBatflySignalRoomRuntime.RoomState room = DesertBatflySignalRoomRuntime.For(emitter.room);
        DB_SignalPacket packet = room?.AddOrRefresh(
            emitter.room,
            DB_SignalKind.RallySignal,
            emitter,
            emitter,
            threat,
            threat as Player,
            origin,
            direction,
            Mathf.Clamp01(0.55f + Mathf.Clamp01(drive) * 0.30f),
            84);
        if (packet == null) return null;

        SetDisplay(emitter, DB_SignalKind.RallySignal, packet.Intensity, 42, direction);
        room.DeliverUrgent(emitter.room, packet);
        TraceEmit(emitter, packet, reason);
        return packet;
    }

    internal static DB_SignalPacket EmitAcuteAlarm(
        Room room,
        Creature threat,
        Vector2 position,
        float intensity,
        string reason)
    {
        DB_Creature emitter = FindAcuteEmitter(room, position);
        if (emitter == null) return null;
        Vector2 direction = Custom.DirVec(emitter.mainBodyChunk.pos, position);
        return EmitAlarm(emitter, threat, position, direction, intensity, reason);
    }

    internal static DB_SignalPacket EmitDistress(
        DB_Creature emitter,
        Creature threat,
        float intensity,
        string reason)
    {
        if (!Available(emitter)) return null;
        ReceiverState state = states.GetValue(emitter, _ => new ReceiverState());
        int clock = emitter.room.game?.clock ?? 0;
        if (state.LastDistressEmitTick != int.MinValue &&
            clock - state.LastDistressEmitTick >= 0 &&
            clock - state.LastDistressEmitTick < 24)
            return null;
        state.LastDistressEmitTick = clock;

        Vector2 origin = emitter.mainBodyChunk.pos;
        Vector2 direction = threat?.mainBodyChunk != null
            ? Custom.DirVec(origin, threat.mainBodyChunk.pos)
            : Vector2.zero;
        DesertBatflySignalRoomRuntime.RoomState room = DesertBatflySignalRoomRuntime.For(emitter.room);
        DB_SignalPacket packet = room?.AddOrRefresh(
            emitter.room,
            DB_SignalKind.DistressCall,
            emitter,
            emitter,
            threat,
            threat as Player,
            origin,
            direction,
            intensity,
            120);
        if (packet == null) return null;

        SetDisplay(emitter, DB_SignalKind.DistressCall, intensity, 52, direction);
        room.DeliverUrgent(emitter.room, packet);
        TraceEmit(emitter, packet, reason);
        return packet;
    }

    internal static bool ReceivePacket(
        DB_Creature receiver,
        DB_SignalPacket packet,
        out bool relayAlarm)
    {
        relayAlarm = false;
        if (!Available(receiver) || !receiver.Consious || packet == null ||
            packet.Emitter == receiver || packet.Emitter?.room != receiver.room ||
            packet.Expired(receiver.room.game?.clock ?? 0))
            return false;

        ReceiverState state = states.GetValue(receiver, _ => new ReceiverState());
        if (HasGeneration(state, packet.Generation)) return false;
        if (!TryPerceive(receiver, packet, out DB_SignalPerception perception, out float attenuation))
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
            case DB_SignalKind.AlarmFlutter:
                state.AlarmPressure = Mathf.Max(state.AlarmPressure, response);
                state.AlarmOrigin = packet.Origin;
                state.AlarmThreat = packet.Threat;
                state.LastAlarmTick = receiver.room.game?.clock ?? 0;
                state.LastDecision = "accepted AlarmFlutter as short-term danger context";
                DB_SocialRuntime.CancelForPriority(receiver, "AlarmFlutter priority");
                ApplyAlarm(receiver, packet, response);
                relayAlarm = packet.Hop < MaxAlarmHop && ShouldRelayAlarm(receiver, packet, response);
                break;

            case DB_SignalKind.DistressCall:
                state.DistressInterest = Mathf.Max(state.DistressInterest, response);
                state.DistressSource = packet.Subject ?? packet.Emitter;
                state.LastDecision = "accepted DistressCall; existing rescue/vengeance systems retain authority";
                if (response >= 0.38f)
                    DB_SocialRuntime.CancelForPriority(receiver, "DistressCall priority");
                break;

            case DB_SignalKind.RallySignal:
                state.RallyInterest = Mathf.Max(state.RallyInterest, response);
                state.RallySource = packet.Emitter;
                state.RallyTarget = packet.Threat;
                state.LastDecision = "accepted RallySignal as supporter interest only";
                break;

            case DB_SignalKind.RoostCall:
                state.RoostInterest = Mathf.Max(state.RoostInterest, response);
                state.RoostSource = packet.Emitter;
                state.LastDecision = "accepted RoostCall; social runtime still owns legal roost/reservation";
                break;

            case DB_SignalKind.HarassSignal:
                state.HarassInterest = Mathf.Max(state.HarassInterest, response);
                state.HarassSource = packet.Emitter;
                state.HarassTarget = packet.PlayerTarget;
                state.LastDecision = "accepted HarassSignal as target interest only";
                break;

            case DB_SignalKind.SafeSignal:
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


    private static void ApplyAlarm(
        DB_Creature receiver,
        DB_SignalPacket packet,
        float response)
    {
        if (receiver == null || packet == null || response < ThreatAlarmEscapeThreshold || receiver.dead ||
            !receiver.Consious || receiver.room == null || receiver.inShortcut ||
            receiver.Injury.IsSeverelyInjured ||
            DB_TravelRuntime.HasIntent(receiver.abstractCreature))
            return;

        Creature threat = packet.Threat;
        if (threat == null && response < AnonymousAlarmEscapeThreshold) return;
        if (threat != null && (threat.dead || threat.room != receiver.room))
            return;

        // Signal perception may create a short Escape fact, but it never rebroadcasts a new
        // root from ThreatenedAt. Relays remain owned solely by SignalRoomRuntime.
        receiver.DesertAI.ThreatenedAt(threat, packet.Origin, false, false);
    }

    private static DB_Creature FindAcuteEmitter(Room room, Vector2 position)
    {
        if (room == null) return null;
        DB_Creature best = null;
        float bestScore = float.MaxValue;
        foreach (Fly member in DB_SwarmRoom.For(room).Hive.flies)
        {
            if (member is not DB_Creature bat || bat.dead || bat.slatedForDeletetion ||
                !bat.Consious || bat.room != room || bat.inShortcut)
                continue;

            float distance = Vector2.Distance(bat.mainBodyChunk.pos, position);
            if (distance > 480f) continue;
            bool visual = room.VisualContact(bat.mainBodyChunk.pos, position);
            float score = distance + (visual ? 0f : 95f);
            if (score >= bestScore) continue;
            bestScore = score;
            best = bat;
        }
        return best;
    }

    internal static bool TryGetInfluence(DB_Creature bat, out DB_SignalInfluence influence)
    {
        influence = default;
        if (bat == null || !states.TryGetValue(bat, out ReceiverState state)) return false;
        influence = new DB_SignalInfluence(
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

    internal static bool TryGetDisplay(DB_Creature bat, out DB_SignalDisplayState display)
    {
        display = default;
        if (bat == null || !states.TryGetValue(bat, out ReceiverState state)) return false;
        int clock = bat.room?.game?.clock ?? int.MaxValue;
        if (clock >= state.DisplayUntil) return false;
        display = new DB_SignalDisplayState(
            state.Display.Kind,
            state.Display.Intensity,
            state.DisplayUntil - clock,
            state.Display.Direction);
        return true;
    }

    internal static bool TryGetDebugState(DB_Creature bat, out DB_SignalDebugState debug)
    {
        debug = default;
        if (!TryGetInfluence(bat, out DB_SignalInfluence influence) ||
            !states.TryGetValue(bat, out ReceiverState state))
            return false;
        debug = new DB_SignalDebugState(
            influence,
            state.LastGeneration,
            state.LastKind,
            state.LastPerception,
            state.LastHop,
            state.LastDecision,
            DesertBatflySignalRoomRuntime.For(bat.room)?.Count ?? 0);
        return true;
    }

    private static void EmitExistingBehaviorSignals(DB_Creature bat, ReceiverState state, int clock)
    {
        if (state.LastNeutralEmitTick != int.MinValue && clock - state.LastNeutralEmitTick < 18)
            return;

        if (DB_VengeanceRuntime.IsAvenger(bat))
        {
            DB_VengeanceRuntime.TryGetTarget(bat, out Creature target);
            EmitNeutral(
                bat,
                DB_SignalKind.RallySignal,
                bat,
                target,
                target as Player,
                0.70f,
                70,
                "existing Avenger emits RallySignal");
            state.LastNeutralEmitTick = clock;
            return;
        }

        if (bat.AI?.behavior == FlyAI.Behavior.Chain || bat.movMode == Fly.MovementMode.Hang ||
            bat.DesertAI?.Mode == DB_AI.Activity.Roost)
        {
            EmitNeutral(
                bat,
                DB_SignalKind.RoostCall,
                bat,
                null,
                null,
                Mathf.Lerp(0.42f, 0.78f, bat.Personality.RoostAffinity),
                110,
                "committed legal roost emits RoostCall");
            state.LastNeutralEmitTick = clock;
            return;
        }

        if (bat.DesertAI?.Target is Player player && bat.DesertAI.Mode is
            DB_AI.Activity.Observe or DB_AI.Activity.Approach or
            DB_AI.Activity.Circle or DB_AI.Activity.FakeDive or
            DB_AI.Activity.Dive)
        {
            EmitNeutral(
                bat,
                DB_SignalKind.HarassSignal,
                bat,
                player,
                player,
                Mathf.Lerp(0.34f, 0.74f, bat.Personality.AggressionDrive),
                72,
                "existing formal harass posture emits HarassSignal");
            state.LastNeutralEmitTick = clock;
            return;
        }

        if (state.LastAlarmTick != int.MinValue &&
            clock - state.LastAlarmTick >= SafeDelayTicks &&
            (state.LastSafeEmitTick == int.MinValue || clock - state.LastSafeEmitTick >= SafeDelayTicks) &&
            state.AlarmPressure > 0.04f && CanAcceptSafe(bat))
        {
            EmitNeutral(
                bat,
                DB_SignalKind.SafeSignal,
                bat,
                null,
                null,
                Mathf.Lerp(0.28f, 0.58f, bat.Personality.Nerve),
                80,
                "recent local alarm remained clear long enough for SafeSignal");
            state.LastSafeEmitTick = clock;
            state.LastNeutralEmitTick = clock;
        }
    }

    private static void EmitNeutral(
        DB_Creature emitter,
        DB_SignalKind kind,
        DB_Creature subject,
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
        DB_SignalPacket packet = DesertBatflySignalRoomRuntime.For(emitter.room)?.AddOrRefresh(
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

        SetDisplay(
            emitter,
            kind,
            intensity,
            kind == DB_SignalKind.RallySignal ? 42 : 30,
            direction);
        TraceEmit(emitter, packet, reason);
    }
    private static bool TryPerceive(
        DB_Creature receiver,
        DB_SignalPacket packet,
        out DB_SignalPerception perception,
        out float attenuation)
    {
        perception = DB_SignalPerception.None;
        attenuation = 0f;
        float distance = Vector2.Distance(receiver.mainBodyChunk.pos, packet.Emitter.mainBodyChunk.pos);
        float baseVisualRadius = VisualRadius(packet.Kind);
        float visibility = DB_EnvironmentRuntime.VisibilityScale(receiver);
        float visualRadius = DB_VisibilityPolicy.EffectiveRange(
            baseVisualRadius, visibility, DB_VisibilityChannel.Signal);

        if (distance <= visualRadius && DB_VisibilityPolicy.CanObserve(
                receiver,
                packet.Emitter.mainBodyChunk.pos,
                baseVisualRadius,
                DB_VisibilityChannel.Signal))
        {
            perception = DB_SignalPerception.Visual;
            attenuation = Mathf.Lerp(1f, 0.34f, Mathf.Clamp01(distance / Mathf.Max(1f, visualRadius)));
            return true;
        }

        float acousticRadius = packet.Kind switch
        {
            DB_SignalKind.AlarmFlutter => 95f,
            DB_SignalKind.DistressCall => 108f,
            _ => 0f
        };
        if (acousticRadius <= 0f || distance > acousticRadius) return false;

        perception = DB_SignalPerception.CloseAcoustic;
        attenuation = Mathf.Lerp(0.62f, 0.30f, Mathf.Clamp01(distance / acousticRadius));
        return true;
    }

    internal static float VisualRadius(DB_SignalKind kind) => kind switch
    {
        DB_SignalKind.AlarmFlutter => 300f,
        DB_SignalKind.DistressCall => 250f,
        DB_SignalKind.RallySignal => 235f,
        DB_SignalKind.RoostCall => 215f,
        DB_SignalKind.HarassSignal => 235f,
        DB_SignalKind.SafeSignal => 195f,
        _ => 200f
    };

    private static float ResponseStrength(
        DB_Creature receiver,
        DB_SignalPacket packet,
        float attenuation)
    {
        float c = receiver.Personality.Conformity;
        float n = receiver.Personality.Nerve;
        float t = receiver.Personality.Temperament;
        float bond = packet.Emitter != null
            ? DB_SocialBond.GetBondStrength(receiver, packet.Emitter)
            : 0f;

        float scale = packet.Kind switch
        {
            DB_SignalKind.AlarmFlutter =>
                Mathf.Lerp(0.72f, 1.22f, c) * Mathf.Lerp(1.18f, 0.72f, n),
            DB_SignalKind.DistressCall =>
                0.62f + bond * 0.52f + t * 0.20f + n * 0.18f,
            DB_SignalKind.RallySignal =>
                0.38f + t * 0.34f + n * 0.28f + c * 0.20f + bond * 0.18f,
            DB_SignalKind.RoostCall =>
                0.42f + c * 0.34f + receiver.Personality.RoostAffinity * 0.38f + bond * 0.18f,
            DB_SignalKind.HarassSignal =>
                0.28f + t * 0.38f + n * 0.25f + c * 0.17f,
            DB_SignalKind.SafeSignal =>
                0.38f + n * 0.34f + c * 0.28f,
            _ => 1f
        };
        float response = Mathf.Clamp01(packet.Intensity * attenuation * scale);

        // Threat memory stays private to the receiver. Signal response reads it only to
        // modulate the receiver's own willingness; no emitter memory/evidence is copied.
        Player player = packet.PlayerTarget ?? packet.Threat as Player;
        if (player != null)
        {
            int slot = DesertBatflyThreatRuntime.PlayerSlot(player);
            if (DesertBatflyThreatRuntime.ValidSlot(slot))
            {
                DB_PlayerThreatMemory memory =
                    DB_ThreatMemoryStore.For(receiver.DesertState, slot);
                if (memory != null && memory.Confidence >= 0.04f)
                {
                    float lethalCaution = Mathf.Clamp01(
                        memory.PiercingPressure * 0.30f +
                        memory.CounterKillPressure * 0.34f +
                        memory.ExplosionPressure * 0.17f +
                        memory.GrabCapturePressure * 0.10f +
                        memory.PursuitPressure * 0.09f);
                    float caution = lethalCaution * memory.Confidence;
                    response *= packet.Kind switch
                    {
                        DB_SignalKind.AlarmFlutter => 1f + caution * 0.24f,
                        DB_SignalKind.DistressCall => 1f - caution * 0.22f,
                        DB_SignalKind.RallySignal => 1f - caution * 0.52f,
                        DB_SignalKind.HarassSignal => 1f - caution * 0.62f,
                        _ => 1f
                    };
                }
            }
        }

        return Mathf.Clamp01(response);
    }

    private static bool ShouldRelayAlarm(
        DB_Creature receiver,
        DB_SignalPacket packet,
        float response)
    {
        if (packet.Hop >= MaxAlarmHop || response < 0.24f) return false;
        float chance = Mathf.Clamp01(
            0.14f + receiver.Personality.Conformity * 0.58f +
            (1f - receiver.Personality.Nerve) * 0.18f + response * 0.18f);
        return Stable01(receiver, packet.Generation ^ (packet.Hop + 1) * 0x6D2B) < chance;
    }

    private static bool CanAcceptSafe(DB_Creature bat)
    {
        if (!Available(bat) || !bat.Consious || bat.DesertAI == null) return false;
        if (bat.DesertAI.HasImmediateDanger || bat.DesertAI.Mode == DB_AI.Activity.Escape)
            return false;
        if (bat.Injury.IsSeverelyInjured || DB_TravelRuntime.HasIntent(bat.abstractCreature))
            return false;

        if (DesertBatflyThreatRuntime.TryGetDebugState(bat, out DesertBatflyThreatDebugState threat) &&
            (threat.Cue.ProjectileThreat ||
             threat.AcuteExplosionTimer > 0 ||
             threat.AcuteStartleTimer > 0 ||
             threat.AcuteMassCasualtyTimer > 0 ||
             threat.AcuteCaptureTimer > 0 ||
             threat.AcuteShockTimer > 0))
            return false;

        return !DB_FearRuntime.HasActiveFearSuppression(bat);
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
            for (int i = 0; i < state.Generations.Length; i++)
                state.Generations[i] = int.MinValue;
            state.GenerationInitialized = true;
        }
        state.Generations[state.GenerationCursor] = generation;
        state.GenerationCursor = (state.GenerationCursor + 1) % state.Generations.Length;
    }

    private static void SetDisplay(
        DB_Creature emitter,
        DB_SignalKind kind,
        float intensity,
        int ticks,
        Vector2 direction)
    {
        ReceiverState state = states.GetValue(emitter, _ => new ReceiverState());
        int clock = emitter.room?.game?.clock ?? 0;
        state.Display = new DB_SignalDisplayState(kind, intensity, ticks, direction);
        state.DisplayUntil = Mathf.Max(state.DisplayUntil, clock + Mathf.Max(1, ticks));
    }

    private static bool Available(DB_Creature bat) =>
        bat != null && !bat.dead && !bat.slatedForDeletetion && bat.room != null && !bat.inShortcut;

    private static int StableInt(DB_Creature bat, int salt, int min, int max)
    {
        if (max <= min) return min;
        return min + Mathf.FloorToInt(Stable01(bat, salt) * (max - min));
    }

    private static float Stable01(DB_Creature bat, int salt)
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

    private static void TraceEmit(DB_Creature emitter, DB_SignalPacket packet, string reason)
    {
        if (emitter?.abstractCreature == null ||
            !DryCycle.Debugging.AI.AIDebugTrace.IsWatched(emitter.abstractCreature))
            return;
        DryCycle.Debugging.AI.AIDebugTrace.Record(
            emitter.abstractCreature,
            DryCycle.Debugging.AI.AIDebugEventCategory.Social,
            "SignalEmitted",
            $"{packet.Kind} gen={packet.Generation} hop={packet.Hop}",
            reason ?? string.Empty);
    }

    private static void TraceReceive(
        DB_Creature receiver,
        DB_SignalPacket packet,
        DB_SignalPerception perception,
        float response,
        string reason)
    {
        if (receiver?.abstractCreature == null ||
            !DryCycle.Debugging.AI.AIDebugTrace.IsWatched(receiver.abstractCreature))
            return;
        DryCycle.Debugging.AI.AIDebugTrace.Record(
            receiver.abstractCreature,
            DryCycle.Debugging.AI.AIDebugEventCategory.Social,
            "SignalReceived",
            $"{packet.Kind} gen={packet.Generation} hop={packet.Hop} via={perception} response={response:0.00}",
            reason ?? string.Empty);
    }
}