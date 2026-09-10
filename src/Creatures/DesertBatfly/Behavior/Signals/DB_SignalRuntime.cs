using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Signal emitter/transport runtime. Receiver belief, confidence, generation de-duplication and
/// interpretation belong to DB_PerceptionRuntime. This type owns packet creation, emitter
/// cadence and display state only; it never creates receiver or behavior state.
/// </summary>
internal static class DB_SignalRuntime
{
    private const int SafeDelayTicks = 210;

    private sealed class EmitterState
    {
        internal int LastSafeEmitTick = int.MinValue;
        internal int LastNeutralEmitTick = int.MinValue;
        internal int LastDistressEmitTick = int.MinValue;
        internal DB_SignalDisplayState Display;
        internal int DisplayUntil = int.MinValue;
    }

    private static ConditionalWeakTable<DB_Creature, EmitterState> states = new();

    internal static void Reset()
    {
        states = new ConditionalWeakTable<DB_Creature, EmitterState>();
        DB_SignalRoomRuntime.Reset();
    }

    internal static void Forget(DB_Creature bat)
    {
        if (bat != null) states.Remove(bat);
    }

    /// <summary>
    /// End-of-frame emitter maintenance only. Signal reception is refreshed by Perception R2
    /// and urgent room delivery enters DB_PerceptionRuntime directly.
    /// </summary>
    internal static void Update(DB_Creature bat)
    {
        if (!Available(bat)) return;
        EmitterState state = states.GetValue(bat, _ => new EmitterState());
        EmitExistingBehaviorSignals(bat, state, bat.room.game?.clock ?? 0);
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
        DB_SignalDefinition definition = DB_SignalDefinition.For(DB_SignalKind.AlarmFlutter);
        DB_SignalRoomRuntime.RoomState room = DB_SignalRoomRuntime.For(emitter.room);
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
            definition.RootTtlTicks);
        if (packet == null) return null;

        // The emitting animal has direct knowledge of its own alarm. Store that knowledge in
        // Perception rather than in the transport object so SafeSignal timing has one owner.
        emitter.DesertAI?.Perception?.NoteLocalAlarm(threat, origin, intensity);
        SetDisplay(emitter, DB_SignalKind.AlarmFlutter, intensity, definition.DisplayTicks, direction);
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
        DB_SignalDefinition definition = DB_SignalDefinition.For(DB_SignalKind.RallySignal);
        Vector2 origin = emitter.mainBodyChunk.pos;
        Vector2 direction = threat.mainBodyChunk != null
            ? Custom.DirVec(origin, threat.mainBodyChunk.pos)
            : Vector2.zero;
        DB_SignalRoomRuntime.RoomState room = DB_SignalRoomRuntime.For(emitter.room);
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
            definition.RootTtlTicks);
        if (packet == null) return null;

        SetDisplay(emitter, DB_SignalKind.RallySignal, packet.Intensity, definition.DisplayTicks, direction);
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
        EmitterState state = states.GetValue(emitter, _ => new EmitterState());
        int clock = emitter.room.game?.clock ?? 0;
        if (state.LastDistressEmitTick != int.MinValue &&
            clock - state.LastDistressEmitTick >= 0 &&
            clock - state.LastDistressEmitTick < 24)
            return null;
        state.LastDistressEmitTick = clock;

        DB_SignalDefinition definition = DB_SignalDefinition.For(DB_SignalKind.DistressCall);
        Vector2 origin = emitter.mainBodyChunk.pos;
        Vector2 direction = threat?.mainBodyChunk != null
            ? Custom.DirVec(origin, threat.mainBodyChunk.pos)
            : Vector2.zero;
        DB_SignalRoomRuntime.RoomState room = DB_SignalRoomRuntime.For(emitter.room);
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
            definition.RootTtlTicks);
        if (packet == null) return null;

        SetDisplay(emitter, DB_SignalKind.DistressCall, intensity, definition.DisplayTicks, direction);
        room.DeliverUrgent(emitter.room, packet);
        TraceEmit(emitter, packet, reason);
        return packet;
    }

    internal static bool TryGetDisplay(DB_Creature bat, out DB_SignalDisplayState display)
    {
        display = default;
        if (bat == null || !states.TryGetValue(bat, out EmitterState state)) return false;
        int clock = bat.room?.game?.clock ?? int.MaxValue;
        if (clock >= state.DisplayUntil) return false;
        display = new DB_SignalDisplayState(
            state.Display.Kind,
            state.Display.Intensity,
            state.DisplayUntil - clock,
            state.Display.Direction);
        return true;
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

    private static void EmitExistingBehaviorSignals(
        DB_Creature bat,
        EmitterState state,
        int clock)
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
                "existing formal harass posture emits HarassSignal");
            state.LastNeutralEmitTick = clock;
            return;
        }

        DB_PerceptionRuntime perception = bat.DesertAI?.Perception;
        if (perception == null) return;
        DB_PerceptionSignalContext signal = perception.Snapshot.Signals;
        int lastAlarm = perception.LastSignalAlarmTick;
        if (lastAlarm != int.MinValue &&
            clock - lastAlarm >= SafeDelayTicks &&
            (state.LastSafeEmitTick == int.MinValue || clock - state.LastSafeEmitTick >= SafeDelayTicks) &&
            signal.AlarmPressure > 0.04f && perception.CanAcceptSafeSignal())
        {
            EmitNeutral(
                bat,
                DB_SignalKind.SafeSignal,
                bat,
                null,
                null,
                Mathf.Lerp(0.28f, 0.58f, bat.Personality.Nerve),
                "recent perceived alarm remained clear long enough for SafeSignal");
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
        string reason)
    {
        if (!Available(emitter)) return;
        DB_SignalDefinition definition = DB_SignalDefinition.For(kind);
        Vector2 origin = emitter.mainBodyChunk.pos;
        Vector2 direction = threat?.mainBodyChunk != null
            ? Custom.DirVec(origin, threat.mainBodyChunk.pos)
            : Vector2.zero;
        DB_SignalPacket packet = DB_SignalRoomRuntime.For(emitter.room)?.AddOrRefresh(
            emitter.room,
            kind,
            emitter,
            subject,
            threat,
            playerTarget,
            origin,
            direction,
            intensity,
            definition.AmbientTtlTicks);
        if (packet == null) return;

        SetDisplay(emitter, kind, intensity, definition.DisplayTicks, direction);
        TraceEmit(emitter, packet, reason);
    }

    private static void SetDisplay(
        DB_Creature emitter,
        DB_SignalKind kind,
        float intensity,
        int ticks,
        Vector2 direction)
    {
        EmitterState state = states.GetValue(emitter, _ => new EmitterState());
        int clock = emitter.room?.game?.clock ?? 0;
        state.Display = new DB_SignalDisplayState(kind, intensity, ticks, direction);
        state.DisplayUntil = Mathf.Max(state.DisplayUntil, clock + Mathf.Max(1, ticks));
    }

    private static bool Available(DB_Creature bat) =>
        bat != null && !bat.dead && !bat.slatedForDeletetion && bat.room != null && !bat.inShortcut;

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
}
