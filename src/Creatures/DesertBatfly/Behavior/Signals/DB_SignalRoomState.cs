using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal static class DB_SignalRoomRuntime
{
    internal const int ActiveSignalCap = 24;
    internal const int AlarmRootMergeTicks = 14;
    internal const float AlarmRootMergeRadius = 58f;

    internal sealed class RoomState
    {
        internal readonly List<DB_SignalPacket> ActiveSignals = new(ActiveSignalCap);
        private readonly Queue<DB_SignalPacket> urgentQueue = new(12);
        private int serial;
        private int lastPruneTick = int.MinValue;

        internal int Count => ActiveSignals.Count;

        internal int NextGeneration(Room room)
        {
            int clock = room?.game?.clock ?? 0;
            unchecked
            {
                serial = (serial + 1) & 0x7FFF;
                return (clock << 15) ^ serial;
            }
        }

        internal void Prune(Room room)
        {
            int clock = room?.game?.clock ?? 0;
            if (clock == lastPruneTick) return;
            lastPruneTick = clock;
            for (int i = ActiveSignals.Count - 1; i >= 0; i--)
            {
                DB_SignalPacket packet = ActiveSignals[i];
                if (packet == null || packet.Expired(clock) || packet.Emitter.room != room)
                    ActiveSignals.RemoveAt(i);
            }
        }

        internal DB_SignalPacket AddOrRefresh(
            Room room,
            DB_SignalKind kind,
            DB_Creature emitter,
            DB_Creature subject,
            Creature threat,
            Player target,
            Vector2 origin,
            Vector2 direction,
            float intensity,
            int ttl,
            int generation = 0,
            int hop = 0)
        {
            if (room == null || emitter == null || emitter.room != room || emitter.dead)
                return null;

            int clock = room.game?.clock ?? 0;
            Prune(room);

            // A continuous capture/escape may call the same alarm source every frame.
            // Keep one live generation for the same emitter+threat until it expires.
            // Separate directly affected bats from one acute event still collapse through
            // the bounded 14-tick / 58px root merge below.
            if (kind == DB_SignalKind.AlarmFlutter && hop == 0 && generation == 0)
            {
                float radiusSq = AlarmRootMergeRadius * AlarmRootMergeRadius;
                for (int i = 0; i < ActiveSignals.Count; i++)
                {
                    DB_SignalPacket existing = ActiveSignals[i];
                    if (existing == null || existing.Kind != DB_SignalKind.AlarmFlutter ||
                        existing.Hop != 0 || existing.Threat != threat)
                        continue;

                    bool sameEmitter = existing.Emitter == emitter && !existing.Expired(clock);
                    bool sameAcuteRoot = clock - existing.CreatedTick >= 0 &&
                                         clock - existing.CreatedTick <= AlarmRootMergeTicks &&
                                         (existing.Origin - origin).sqrMagnitude <= radiusSq;
                    if (!sameEmitter && !sameAcuteRoot) continue;

                    existing.Intensity = Mathf.Max(existing.Intensity, Mathf.Clamp01(intensity));
                    existing.ExpiresTick = Mathf.Max(existing.ExpiresTick, clock + Mathf.Max(1, ttl));
                    return existing;
                }
            }

            // Neutral/display signals from the same emitter are refreshed rather than
            // allocating a new generation every scan. Urgent relays carry their original
            // generation and therefore bypass this merge when hop > 0.
            if (hop == 0 && generation == 0)
            {
                for (int i = 0; i < ActiveSignals.Count; i++)
                {
                    DB_SignalPacket existing = ActiveSignals[i];
                    if (existing.Kind != kind || existing.Emitter != emitter || existing.Hop != 0)
                        continue;
                    if (existing.Subject != subject || existing.Threat != threat || existing.PlayerTarget != target)
                        continue;
                    existing.Intensity = Mathf.Max(existing.Intensity, Mathf.Clamp01(intensity));
                    existing.ExpiresTick = Mathf.Max(existing.ExpiresTick, clock + Mathf.Max(1, ttl));
                    return existing;
                }
            }

            int id = generation != 0 ? generation : NextGeneration(room);
            var packet = new DB_SignalPacket(
                id,
                kind,
                emitter,
                subject,
                threat,
                target,
                origin,
                direction,
                intensity,
                hop,
                clock,
                clock + Mathf.Max(1, ttl));

            if (ActiveSignals.Count >= ActiveSignalCap)
            {
                int replace = OldestReplaceableIndex();
                if (replace >= 0) ActiveSignals.RemoveAt(replace);
                else return null;
            }
            ActiveSignals.Add(packet);
            return packet;
        }

        private int OldestReplaceableIndex()
        {
            int best = -1;
            int bestScore = int.MaxValue;
            for (int i = 0; i < ActiveSignals.Count; i++)
            {
                DB_SignalPacket signal = ActiveSignals[i];
                if (signal == null) return i;
                int urgencyBias = signal.Kind is
                    DB_SignalKind.AlarmFlutter or DB_SignalKind.DistressCall
                    ? 1000000
                    : 0;
                int score = signal.ExpiresTick + urgencyBias;
                if (score >= bestScore) continue;
                bestScore = score;
                best = i;
            }
            return best;
        }

        internal void DeliverUrgent(Room room, DB_SignalPacket root)
        {
            if (room == null || root == null) return;
            urgentQueue.Clear();
            urgentQueue.Enqueue(root);
            int guard = 0;

            while (urgentQueue.Count > 0 && guard++ < ActiveSignalCap * 3)
            {
                DB_SignalPacket packet = urgentQueue.Dequeue();
                if (packet == null || packet.Emitter?.room != room) continue;

                foreach (Fly member in DB_SwarmRoom.For(room).Hive.flies)
                {
                    if (member is not DB_Creature receiver || receiver == packet.Emitter ||
                        receiver.dead || receiver.slatedForDeletetion || !receiver.Consious ||
                        receiver.room != room || receiver.inShortcut)
                        continue;

                    if (!DB_SignalRuntime.ReceivePacket(receiver, packet, out bool relay))
                        continue;
                    if (!relay || packet.Kind != DB_SignalKind.AlarmFlutter ||
                        packet.Hop >= DB_SignalRuntime.MaxAlarmHop)
                        continue;

                    float relayIntensity = packet.Intensity *
                        (packet.Hop == 0
                            ? DB_SignalRuntime.AlarmHop1Scale
                            : DB_SignalRuntime.AlarmHop2Scale);
                    if (relayIntensity < 0.08f) continue;

                    DB_SignalPacket relayed = AddOrRefresh(
                        room,
                        DB_SignalKind.AlarmFlutter,
                        receiver,
                        packet.Subject,
                        packet.Threat,
                        packet.PlayerTarget,
                        packet.Origin,
                        packet.Direction,
                        relayIntensity,
                        DB_SignalRuntime.AlarmTtlTicks,
                        packet.Generation,
                        packet.Hop + 1);
                    if (relayed != null) urgentQueue.Enqueue(relayed);
                }
            }
        }
    }

    private static ConditionalWeakTable<Room, RoomState> rooms = new();

    internal static RoomState For(Room room) =>
        room == null ? null : rooms.GetValue(room, _ => new RoomState());

    internal static void Reset()
    {
        rooms = new ConditionalWeakTable<Room, RoomState>();
    }
}
