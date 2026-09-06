using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Room-scoped, non-persistent cache/reservation layer for Task 10.
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
        internal readonly List<DesertBatfly> Members;
        internal readonly DesertBatfly Anchor;
        internal bool Active = true;

        internal Reservation(RoomState owner, int id, DesertBatflySocialMode mode,
            List<DesertBatfly> members, DesertBatfly anchor = null)
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
        private readonly List<DesertBatfly> candidates = new(32);
        private readonly Dictionary<long, Reservation> byMember = new();
        private readonly List<Reservation> reservations = new(16);
        private int lastRefreshTick = int.MinValue;
        private int tokenSerial;

        internal RoomState(Room room)
        {
            this.room = room;
        }

        internal IReadOnlyList<DesertBatfly> Candidates
        {
            get
            {
                Refresh();
                return candidates;
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

        internal bool IsReserved(DesertBatfly bat)
        {
            Refresh();
            return bat != null && byMember.ContainsKey(Key(bat));
        }

        internal Reservation ReservationFor(DesertBatfly bat)
        {
            Refresh();
            return bat != null && byMember.TryGetValue(Key(bat), out Reservation token)
                ? token
                : null;
        }

        internal bool TryReservePair(DesertBatfly a, DesertBatfly b,
            DesertBatflySocialMode mode, out Reservation token)
        {
            token = null;
            Refresh();
            if (!ValidMember(a) || !ValidMember(b) || a == b || a.room != room || b.room != room)
                return false;
            if (byMember.ContainsKey(Key(a)) || byMember.ContainsKey(Key(b)))
                return false;

            var members = new List<DesertBatfly>(2) { a, b };
            token = Create(mode, members);
            return true;
        }

        internal bool TryReserveGroup(List<DesertBatfly> requested, out Reservation token)
        {
            token = null;
            Refresh();
            if (requested == null || requested.Count < 3) return false;

            var members = new List<DesertBatfly>(Math.Min(6, requested.Count));
            for (int i = 0; i < requested.Count && members.Count < 6; i++)
            {
                DesertBatfly bat = requested[i];
                if (!ValidMember(bat) || bat.room != room || byMember.ContainsKey(Key(bat)) || members.Contains(bat))
                    continue;
                members.Add(bat);
            }
            if (members.Count < 3) return false;

            token = Create(DesertBatflySocialMode.GroupDrift, members);
            return true;
        }

        internal bool TryReserveInvitation(DesertBatfly target, DesertBatfly source,
            DesertBatflySocialMode mode, int invitationCap, out Reservation token)
        {
            token = null;
            Refresh();
            if (!ValidMember(target) || !ValidMember(source) || target == source ||
                target.room != room || source.room != room || byMember.ContainsKey(Key(target)))
                return false;
            if (ActiveInvitationsFrom(source) >= Math.Max(1, invitationCap))
                return false;

            var members = new List<DesertBatfly>(1) { target };
            token = Create(mode, members, source);
            return true;
        }

        internal int ActiveInvitationsFrom(DesertBatfly source)
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

        internal void RemoveGroupMember(Reservation token, DesertBatfly member)
        {
            if (token == null || !token.Active || token.Owner != this ||
                token.Mode != DesertBatflySocialMode.GroupDrift || member == null)
                return;

            for (int i = token.Members.Count - 1; i >= 0; i--)
            {
                if (token.Members[i] != member) continue;
                token.Members.RemoveAt(i);
                long key = Key(member);
                if (byMember.TryGetValue(key, out Reservation current) && ReferenceEquals(current, token))
                    byMember.Remove(key);
                break;
            }
            if (token.Members.Count < 3) Release(token);
        }

        internal void Release(Reservation token)
        {
            if (token == null || !token.Active || token.Owner != this) return;
            token.Active = false;
            for (int i = 0; i < token.Members.Count; i++)
            {
                DesertBatfly member = token.Members[i];
                if (member == null) continue;
                long key = Key(member);
                if (byMember.TryGetValue(key, out Reservation current) && ReferenceEquals(current, token))
                    byMember.Remove(key);
            }
            reservations.Remove(token);
        }

        private Reservation Create(DesertBatflySocialMode mode, List<DesertBatfly> members,
            DesertBatfly anchor = null)
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
            if (room == null) return;
            if (DesertSwarmRoom.TryGet(room, out DesertSwarmRoom colony))
            {
                List<Fly> flies = colony.Hive.flies;
                for (int i = 0; i < flies.Count; i++)
                    if (flies[i] is DesertBatfly bat && ValidMember(bat) && bat.room == room)
                        candidates.Add(bat);
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
                        DesertBatfly member = token.Members[m];
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
                    DesertBatfly member = token.Members[m];
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

    internal static long Key(DesertBatfly bat) => bat?.abstractCreature == null
        ? long.MinValue
        : Key(bat.abstractCreature.ID);

    internal static long Key(EntityID id) => ((long)id.spawner << 32) ^ (uint)id.number;

    internal static bool ValidMember(DesertBatfly bat) => bat != null && !bat.dead &&
        !bat.slatedForDeletetion && bat.room != null && !bat.inShortcut;
}
