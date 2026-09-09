using System.Runtime.CompilerServices;
using DryCycle.Thirst;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum DB_FeedingRole
{
    None,
    Attach,
    Cloud
}

internal readonly struct DB_FeedingAssignment
{
    internal readonly Player Target;
    internal readonly DB_FeedingRole Role;
    internal readonly int SlotIndex;
    internal readonly float Motivation;
    internal readonly PlayerDehydrationStage TargetStage;

    internal bool Valid => Target != null && Role != DB_FeedingRole.None;

    internal DB_FeedingAssignment(
        Player target,
        DB_FeedingRole role,
        int slotIndex,
        float motivation,
        PlayerDehydrationStage targetStage)
    {
        Target = target;
        Role = role;
        SlotIndex = slotIndex;
        Motivation = Mathf.Clamp01(motivation);
        TargetStage = targetStage;
    }
}

/// <summary>
/// Room-level authority for dehydration-feeding aggregation. It owns player target snapshots,
/// per-player participant budgets, attachment/cloud slots and aggregate fluid-loss application.
/// Individual bats never scan room object arrays or independently decide group capacity.
/// </summary>
internal static class DB_FeedingCoordinator
{
    private sealed class TargetEntry
    {
        internal Player Player;
        internal PlayerDehydrationSnapshot Facts;
    }

    private sealed class Reservation
    {
        internal DB_Creature Bat;
        internal Player Target;
        internal DB_FeedingRole Role;
        internal int SlotIndex;
        internal float Motivation;
        internal bool Attached;
    }

    private sealed class RoomState
    {
        internal readonly List<TargetEntry> Targets = new(4);
        internal readonly List<Reservation> Reservations = new(20);
        internal int TargetRefreshClock = int.MinValue;
        internal int DrainClock = int.MinValue;
    }

    private static ConditionalWeakTable<Room, RoomState> rooms = new();

    internal static void Reset()
    {
        rooms = new ConditionalWeakTable<Room, RoomState>();
    }

    internal static bool TryGetOrAcquire(DB_Creature bat, out DB_FeedingAssignment assignment)
    {
        assignment = default;
        if (!CurrentBat(bat)) return false;

        RoomState state = rooms.GetValue(bat.room, _ => new RoomState());
        RefreshTargets(bat.room, state);
        Prune(bat.room, state);

        Reservation existing = FindReservation(state, bat);
        if (existing != null)
        {
            assignment = ToAssignment(state, existing);
            return assignment.Valid;
        }

        TargetEntry bestTarget = null;
        DB_FeedingRole bestRole = DB_FeedingRole.None;
        int bestSlot = -1;
        float bestMotivation = 0f;

        for (int i = 0; i < state.Targets.Count; i++)
        {
            TargetEntry target = state.Targets[i];
            if (!CurrentTarget(target?.Player, bat.room) || !target.Facts.FeedingEligible)
                continue;

            float range = target.Facts.Stage >= PlayerDehydrationStage.Critical
                ? DB_Tuning.FeedingCriticalRange
                : DB_Tuning.FeedingSevereRange;
            float distance = Vector2.Distance(
                bat.mainBodyChunk.pos,
                target.Player.mainBodyChunk.pos);
            if (distance > range || !DB_VisibilityPolicy.CanObserve(
                    bat,
                    target.Player.mainBodyChunk.pos,
                    range,
                    DB_VisibilityChannel.Player))
                continue;

            int attachCap = AttachCapacity(target.Facts.Stage);
            int totalCap = TotalCapacity(target.Facts.Stage);
            CountReservations(state, target.Player, out int attachedRoleCount, out int totalCount);
            if (totalCount >= totalCap) continue;

            DB_FeedingRole role = attachedRoleCount < attachCap
                ? DB_FeedingRole.Attach
                : DB_FeedingRole.Cloud;
            int slot = FindFreeSlot(state, target.Player, role, target.Facts.Stage);
            if (slot < 0) continue;

            float motivation = Motivation(bat, target, distance, range);
            float threshold = target.Facts.Stage >= PlayerDehydrationStage.Critical
                ? DB_Tuning.FeedingCriticalMotivationThreshold
                : DB_Tuning.FeedingSevereMotivationThreshold;
            if (motivation < threshold || motivation <= bestMotivation) continue;

            bestTarget = target;
            bestRole = role;
            bestSlot = slot;
            bestMotivation = motivation;
        }

        if (bestTarget == null) return false;

        var reservation = new Reservation
        {
            Bat = bat,
            Target = bestTarget.Player,
            Role = bestRole,
            SlotIndex = bestSlot,
            Motivation = bestMotivation
        };
        state.Reservations.Add(reservation);
        assignment = new DB_FeedingAssignment(
            reservation.Target,
            reservation.Role,
            reservation.SlotIndex,
            reservation.Motivation,
            bestTarget.Facts.Stage);
        return true;
    }

    internal static bool TryGetExisting(DB_Creature bat, out DB_FeedingAssignment assignment)
    {
        assignment = default;
        if (!CurrentBat(bat) || !rooms.TryGetValue(bat.room, out RoomState state)) return false;
        RefreshTargets(bat.room, state);
        Prune(bat.room, state);
        Reservation reservation = FindReservation(state, bat);
        if (reservation == null) return false;
        assignment = ToAssignment(state, reservation);
        return assignment.Valid;
    }

    internal static void MarkAttached(DB_Creature bat, bool attached)
    {
        if (bat?.room == null || !rooms.TryGetValue(bat.room, out RoomState state)) return;
        Reservation reservation = FindReservation(state, bat);
        if (reservation != null)
            reservation.Attached = attached && reservation.Role == DB_FeedingRole.Attach;
    }

    internal static void Release(DB_Creature bat)
    {
        if (bat?.room == null || !rooms.TryGetValue(bat.room, out RoomState state)) return;
        for (int i = state.Reservations.Count - 1; i >= 0; i--)
            if (ReferenceEquals(state.Reservations[i].Bat, bat))
                state.Reservations.RemoveAt(i);
    }

    internal static void UpdateRoom(Room room)
    {
        if (room == null || !rooms.TryGetValue(room, out RoomState state)) return;
        RefreshTargets(room, state);
        Prune(room, state);

        int clock = room.game?.clock ?? 0;
        if (state.DrainClock == clock) return;
        state.DrainClock = clock;

        for (int i = 0; i < state.Targets.Count; i++)
        {
            TargetEntry target = state.Targets[i];
            if (!CurrentTarget(target?.Player, room) || !target.Facts.FeedingEligible) continue;

            int attachedCount = 0;
            for (int r = 0; r < state.Reservations.Count; r++)
            {
                Reservation reservation = state.Reservations[r];
                if (reservation.Attached && ReferenceEquals(reservation.Target, target.Player))
                    attachedCount++;
            }
            if (attachedCount <= 0) continue;

            float aggregatePerSecond = Mathf.Lerp(
                DB_Tuning.FeedingDebtPerSecondOne,
                DB_Tuning.FeedingDebtPerSecondMax,
                Mathf.InverseLerp(1f, DB_Tuning.FeedingCriticalAttachCapacity, attachedCount));
            float debtAmount = aggregatePerSecond / ThirstConstants.SimulationTicksPerSecond;
            if (PlayerDehydrationFacts.ApplyPredationStress(target.Player, debtAmount))
                target.Player.showKarmaFoodRainTime = Mathf.Max(
                    target.Player.showKarmaFoodRainTime,
                    ThirstConstants.HydrationLossHudHoldFrames);

            float relief = DB_Tuning.FeedingThirstReliefPerSecond /
                           ThirstConstants.SimulationTicksPerSecond;
            for (int r = 0; r < state.Reservations.Count; r++)
            {
                Reservation reservation = state.Reservations[r];
                if (!reservation.Attached || !ReferenceEquals(reservation.Target, target.Player) ||
                    reservation.Bat == null)
                    continue;
                reservation.Bat.DesertState.Thirst = Mathf.Max(
                    0f,
                    reservation.Bat.DesertState.Thirst - relief);
            }
        }
    }

    private static void RefreshTargets(Room room, RoomState state)
    {
        int clock = room?.game?.clock ?? 0;
        if (state.TargetRefreshClock != int.MinValue && clock >= state.TargetRefreshClock &&
            clock - state.TargetRefreshClock < DB_Tuning.FeedingCoordinatorRefreshTicks)
            return;

        state.TargetRefreshClock = clock;
        state.Targets.Clear();
        DB_RoomContext context = DB_RoomContext.For(room);
        if (context == null) return;

        IReadOnlyList<Player> players = context.Players;
        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            if (!CurrentTarget(player, room)) continue;
            PlayerDehydrationSnapshot facts = PlayerDehydrationFacts.For(player);
            if (!facts.FeedingEligible) continue;
            state.Targets.Add(new TargetEntry { Player = player, Facts = facts });
        }
    }

    private static void Prune(Room room, RoomState state)
    {
        for (int i = state.Reservations.Count - 1; i >= 0; i--)
        {
            Reservation reservation = state.Reservations[i];
            if (!CurrentBat(reservation.Bat) || reservation.Bat.room != room ||
                !CurrentTarget(reservation.Target, room) ||
                !TryTargetFacts(state, reservation.Target, out PlayerDehydrationSnapshot facts) ||
                !facts.FeedingEligible ||
                reservation.SlotIndex < 0 ||
                (reservation.Role == DB_FeedingRole.Attach &&
                 reservation.SlotIndex >= AttachCapacity(facts.Stage)) ||
                (reservation.Role == DB_FeedingRole.Cloud &&
                 reservation.SlotIndex >= CloudCapacity(facts.Stage)))
                state.Reservations.RemoveAt(i);
        }
    }

    private static DB_FeedingAssignment ToAssignment(RoomState state, Reservation reservation)
    {
        if (reservation == null ||
            !TryTargetFacts(state, reservation.Target, out PlayerDehydrationSnapshot facts))
            return default;
        return new DB_FeedingAssignment(
            reservation.Target,
            reservation.Role,
            reservation.SlotIndex,
            reservation.Motivation,
            facts.Stage);
    }

    private static bool TryTargetFacts(
        RoomState state,
        Player player,
        out PlayerDehydrationSnapshot facts)
    {
        for (int i = 0; i < state.Targets.Count; i++)
        {
            if (!ReferenceEquals(state.Targets[i].Player, player)) continue;
            facts = state.Targets[i].Facts;
            return true;
        }
        facts = default;
        return false;
    }

    private static Reservation FindReservation(RoomState state, DB_Creature bat)
    {
        for (int i = 0; i < state.Reservations.Count; i++)
            if (ReferenceEquals(state.Reservations[i].Bat, bat))
                return state.Reservations[i];
        return null;
    }

    private static void CountReservations(
        RoomState state,
        Player target,
        out int attachCount,
        out int totalCount)
    {
        attachCount = 0;
        totalCount = 0;
        for (int i = 0; i < state.Reservations.Count; i++)
        {
            Reservation reservation = state.Reservations[i];
            if (!ReferenceEquals(reservation.Target, target)) continue;
            totalCount++;
            if (reservation.Role == DB_FeedingRole.Attach) attachCount++;
        }
    }

    private static int FindFreeSlot(
        RoomState state,
        Player target,
        DB_FeedingRole role,
        PlayerDehydrationStage stage)
    {
        int capacity = role == DB_FeedingRole.Attach
            ? AttachCapacity(stage)
            : CloudCapacity(stage);
        for (int slot = 0; slot < capacity; slot++)
        {
            bool used = false;
            for (int i = 0; i < state.Reservations.Count; i++)
            {
                Reservation reservation = state.Reservations[i];
                if (ReferenceEquals(reservation.Target, target) && reservation.Role == role &&
                    reservation.SlotIndex == slot)
                {
                    used = true;
                    break;
                }
            }
            if (!used) return slot;
        }
        return -1;
    }

    private static float Motivation(
        DB_Creature bat,
        TargetEntry target,
        float distance,
        float range)
    {
        float proximity = 1f - Mathf.Clamp01(distance / Mathf.Max(1f, range));
        float score = target.Facts.PreyAttraction * 0.42f +
                      bat.DesertState.Thirst * 0.26f +
                      bat.Personality.FeedingBoldness * 0.22f +
                      proximity * 0.10f;

        int playerNumber = target.Player.playerState?.playerNumber ?? 0;
        DB_State persistent = bat.DesertState;
        if (persistent.PlayerTraumaTicks > 0 && persistent.PlayerTraumaPlayer == playerNumber)
        {
            float traumaPenalty = persistent.PlayerTraumaStrength *
                Mathf.Lerp(0.76f, 0.42f, bat.Personality.Nerve);
            score -= traumaPenalty;
        }

        return Mathf.Clamp01(score * Mathf.Lerp(0.72f, 1f, bat.Injury.PhysicalCapability));
    }

    private static int AttachCapacity(PlayerDehydrationStage stage)
        => stage >= PlayerDehydrationStage.Critical
            ? DB_Tuning.FeedingCriticalAttachCapacity
            : DB_Tuning.FeedingSevereAttachCapacity;

    private static int TotalCapacity(PlayerDehydrationStage stage)
        => stage >= PlayerDehydrationStage.Critical
            ? DB_Tuning.FeedingCriticalTotalCapacity
            : DB_Tuning.FeedingSevereTotalCapacity;

    private static int CloudCapacity(PlayerDehydrationStage stage)
        => Mathf.Max(0, TotalCapacity(stage) - AttachCapacity(stage));

    private static bool CurrentBat(DB_Creature bat)
        => bat != null && !bat.dead && !bat.slatedForDeletetion && bat.room != null &&
           bat.mainBodyChunk != null;

    private static bool CurrentTarget(Player player, Room room)
        => player != null && !player.dead && !player.slatedForDeletetion && player.room == room &&
           !player.inShortcut && player.mainBodyChunk != null;
}
