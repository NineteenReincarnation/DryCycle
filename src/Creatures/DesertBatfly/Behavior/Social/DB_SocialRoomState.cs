using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Room-scoped, non-persistent cache/reservation layer for neutral social behavior.
/// Candidate discovery is refreshed once per room every 20 ticks; individual bats never
/// perform an all-bats scan every frame. Reservations are deliberately temporary and die
/// with the Room instance.
/// </summary>
internal static class DesertBatflySocialRoomRuntime
{
    internal sealed class Reservation
    {
        internal readonly RoomState Owner;
        internal readonly int Id;
        internal readonly DesertBatflySocialMode Mode;
        internal readonly List<DB_Creature> Members;
        internal readonly DB_Creature Anchor;
        internal bool Active = true;

        internal Reservation(RoomState owner, int id, DesertBatflySocialMode mode,
            List<DB_Creature> members, DB_Creature anchor = null)
        {
            Owner = owner;
            Id = id;
            Mode = mode;
            Members = members;
            Anchor = anchor;
        }
    }

    internal sealed class RoomState
    {
        private const int RefreshInterval = 20;
        private readonly Room room;
        private readonly List<DB_Creature> candidates = new(32);
        private readonly List<DB_Creature> roosting = new(16);
        private readonly Dictionary<long, Reservation> byMember = new();
        private readonly List<Reservation> reservations = new(16);
        private int lastRefreshTick = int.MinValue;
        private int tokenSerial;

        internal RoomState(Room room)
        {
            this.room = room;
        }

        internal IReadOnlyList<DB_Creature> Candidates
        {
            get
            {
                Refresh();
                return candidates;
            }
        }

        internal IReadOnlyList<DB_Creature> Roosting
        {
            get
            {
                Refresh();
                return roosting;
            }
        }

        internal int ActiveMemberCount
        {
            get
            {
                Refresh();
                return byMember.Count;
            }
        }

        internal int CandidateCount
        {
            get
            {
                Refresh();
                return candidates.Count;
            }
        }

        internal bool IsReserved(DB_Creature bat)
        {
            Refresh();
            return bat != null && byMember.ContainsKey(Key(bat));
        }

        internal Reservation ReservationFor(DB_Creature bat)
        {
            Refresh();
            return bat != null && byMember.TryGetValue(Key(bat), out Reservation token)
                ? token
                : null;
        }

        internal bool TryReservePair(DB_Creature a, DB_Creature b,
            DesertBatflySocialMode mode, out Reservation token)
        {
            token = null;
            Refresh();
            if (!ValidMember(a) || !ValidMember(b) || a == b || a.room != room || b.room != room)
                return false;
            if (byMember.ContainsKey(Key(a)) || byMember.ContainsKey(Key(b)))
                return false;

            var members = new List<DB_Creature>(2) { a, b };
            token = Create(mode, members);
            return true;
        }

        internal bool TryReserveGroup(List<DB_Creature> requested, out Reservation token)
        {
            token = null;
            Refresh();
            if (requested == null || requested.Count < 3) return false;

            var members = new List<DB_Creature>(Math.Min(6, requested.Count));
            for (int i = 0; i < requested.Count && members.Count < 6; i++)
            {
                DB_Creature bat = requested[i];
                if (!ValidMember(bat) || bat.room != room || byMember.ContainsKey(Key(bat)) || members.Contains(bat))
                    continue;
                members.Add(bat);
            }
            if (members.Count < 3) return false;

            token = Create(DesertBatflySocialMode.GroupDrift, members);
            return true;
        }

        internal bool TryReserveInvitation(DB_Creature target, DB_Creature source,
            DesertBatflySocialMode mode, int invitationCap, out Reservation token)
        {
            token = null;
            Refresh();
            if (!ValidMember(target) || !ValidMember(source) || target == source ||
                target.room != room || source.room != room || byMember.ContainsKey(Key(target)))
                return false;
            if (ActiveInvitationsFrom(source) >= Math.Max(1, invitationCap))
                return false;

            var members = new List<DB_Creature>(1) { target };
            token = Create(mode, members, source);
            return true;
        }

        internal int ActiveInvitationsFrom(DB_Creature source)
        {
            if (source == null) return 0;
            int count = 0;
            for (int i = 0; i < reservations.Count; i++)
            {
                Reservation token = reservations[i];
                if (token.Active && token.Anchor == source &&
                    token.Mode is DesertBatflySocialMode.RoostInvitation or DesertBatflySocialMode.ChainSocialization)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Removes one member without scanning the room. Returns true only while the
        /// microflock remains valid (three or more members and an active reservation).
        /// </summary>
        internal bool RemoveGroupMember(Reservation token, DB_Creature member)
        {
            if (token == null || !token.Active || token.Owner != this ||
                token.Mode != DesertBatflySocialMode.GroupDrift || member == null)
                return false;

            for (int i = token.Members.Count - 1; i >= 0; i--)
            {
                if (token.Members[i] != member) continue;
                token.Members.RemoveAt(i);
                long key = Key(member);
                if (byMember.TryGetValue(key, out Reservation current) && ReferenceEquals(current, token))
                    byMember.Remove(key);
                break;
            }

            if (token.Members.Count >= 3) return true;
            Release(token);
            return false;
        }

        internal int CountRoostingNear(Vector2 point, float radius)
        {
            Refresh();
            int count = 0;
            for (int i = 0; i < roosting.Count; i++)
            {
                DB_Creature bat = roosting[i];
                if (bat?.mainBodyChunk != null && Custom.DistLess(bat.mainBodyChunk.pos, point, radius))
                    count++;
            }
            return count;
        }

        internal void Release(Reservation token)
        {
            if (token == null || !token.Active || token.Owner != this) return;
            token.Active = false;
            for (int i = 0; i < token.Members.Count; i++)
            {
                DB_Creature member = token.Members[i];
                if (member == null) continue;
                long key = Key(member);
                if (byMember.TryGetValue(key, out Reservation current) && ReferenceEquals(current, token))
                    byMember.Remove(key);
            }
            reservations.Remove(token);
        }

        private Reservation Create(DesertBatflySocialMode mode, List<DB_Creature> members,
            DB_Creature anchor = null)
        {
            var token = new Reservation(this, ++tokenSerial, mode, members, anchor);
            reservations.Add(token);
            for (int i = 0; i < members.Count; i++)
                byMember[Key(members[i])] = token;
            return token;
        }

        private void Refresh()
        {
            int tick = room?.game?.clock ?? 0;
            if (lastRefreshTick != int.MinValue && tick >= lastRefreshTick &&
                tick - lastRefreshTick < RefreshInterval)
                return;
            lastRefreshTick = tick;

            candidates.Clear();
            roosting.Clear();
            if (room == null) return;
            if (DB_SwarmRoom.TryGet(room, out DB_SwarmRoom colony))
            {
                List<Fly> flies = colony.Hive.flies;
                for (int i = 0; i < flies.Count; i++)
                {
                    if (flies[i] is not DB_Creature bat || !ValidMember(bat) || bat.room != room)
                        continue;
                    candidates.Add(bat);
                    if (bat.Consious && bat.AI?.behavior == FlyAI.Behavior.Chain)
                        roosting.Add(bat);
                }
            }

            for (int i = reservations.Count - 1; i >= 0; i--)
            {
                Reservation token = reservations[i];
                if (!token.Active)
                {
                    reservations.RemoveAt(i);
                    continue;
                }

                if (token.Mode == DesertBatflySocialMode.GroupDrift)
                {
                    for (int m = token.Members.Count - 1; m >= 0; m--)
                    {
                        DB_Creature member = token.Members[m];
                        if (ValidMember(member) && member.room == room) continue;
                        if (member != null)
                        {
                            long key = Key(member);
                            if (byMember.TryGetValue(key, out Reservation current) && ReferenceEquals(current, token))
                                byMember.Remove(key);
                        }
                        token.Members.RemoveAt(m);
                    }
                    if (token.Members.Count < 3) Release(token);
                    continue;
                }

                bool invalid = false;
                for (int m = 0; m < token.Members.Count; m++)
                {
                    DB_Creature member = token.Members[m];
                    if (!ValidMember(member) || member.room != room)
                    {
                        invalid = true;
                        break;
                    }
                }
                if (token.Anchor != null && (!ValidMember(token.Anchor) || token.Anchor.room != room))
                    invalid = true;
                if (invalid) Release(token);
            }
        }
    }

    private static ConditionalWeakTable<Room, RoomState> rooms = new();

    internal static RoomState For(Room room) => room == null ? null : rooms.GetValue(room, r => new RoomState(r));

    internal static void Reset()
    {
        rooms = new ConditionalWeakTable<Room, RoomState>();
    }

    internal static long Key(DB_Creature bat) => bat?.abstractCreature == null
        ? long.MinValue
        : Key(bat.abstractCreature.ID);

    internal static long Key(EntityID id) => ((long)id.spawner << 32) ^ (uint)id.number;

    internal static bool ValidMember(DB_Creature bat) => bat != null && !bat.dead &&
        !bat.slatedForDeletetion && bat.room != null && !bat.inShortcut;
}
