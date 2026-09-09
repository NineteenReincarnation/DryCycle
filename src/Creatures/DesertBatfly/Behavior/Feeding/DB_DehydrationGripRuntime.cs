using System.Runtime.CompilerServices;
using DryCycle.Thirst;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Species-specific pickup gate for weakened players. It intercepts only an actual manual
/// SlugcatGrab attempt for a Desert Batfly; other items, forced grabs and NPC grabs retain
/// vanilla semantics. Existing held-grasp weakness is owned by DB_RestraintRuntime.
/// </summary>
internal static class DB_DehydrationGripRuntime
{
    private sealed class State
    {
        internal int RetryUntilClock;
        internal int AttemptSerial;
    }

    private static ConditionalWeakTable<Player, State> states = new();
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        states = new ConditionalWeakTable<Player, State>();
        On.Player.SlugcatGrab += SlugcatGrab;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        enabled = false;
        On.Player.SlugcatGrab -= SlugcatGrab;
        states = new ConditionalWeakTable<Player, State>();
    }

    private static void SlugcatGrab(
        On.Player.orig_SlugcatGrab orig,
        Player self,
        PhysicalObject obj,
        int graspUsed)
    {
        if (self == null || obj is not DB_Creature bat || self.isNPC || self.dead ||
            self.room == null || bat.room != self.room || self.wantToPickUp <= 0 ||
            !ReferenceEquals(self.pickUpCandidate, obj))
        {
            orig(self, obj, graspUsed);
            return;
        }

        PlayerDehydrationSnapshot facts = PlayerDehydrationFacts.For(self);
        if (facts.PickupFailureChance <= 0.0001f)
        {
            orig(self, obj, graspUsed);
            return;
        }

        State state = states.GetOrCreateValue(self);
        int clock = self.room.game?.clock ?? 0;
        if (clock < state.RetryUntilClock)
        {
            self.wantToPickUp = 0;
            return;
        }

        state.AttemptSerial++;
        float roll = Stable01(
            self.playerState?.playerNumber ?? 0,
            bat.Personality.VisualSeed,
            state.AttemptSerial,
            clock);

        if (roll >= facts.PickupFailureChance)
        {
            orig(self, obj, graspUsed);
            return;
        }

        int retryTicks = StableInt(
            self.playerState?.playerNumber ?? 0,
            bat.Personality.VisualSeed,
            state.AttemptSerial + 0x53,
            DB_Tuning.DehydratedPickupRetryMinTicks,
            DB_Tuning.DehydratedPickupRetryMaxTicks + 1);
        state.RetryUntilClock = Mathf.Max(state.RetryUntilClock, clock + retryTicks);
        self.wantToPickUp = 0;

        if (bat.mainBodyChunk != null && self.mainBodyChunk != null)
        {
            Vector2 away = Custom.DirVec(self.mainBodyChunk.pos, bat.mainBodyChunk.pos);
            if (away.sqrMagnitude < 0.01f) away = Vector2.up;
            bat.mainBodyChunk.vel += away * DB_Tuning.DehydratedPickupSlipImpulse +
                                     Vector2.up * 0.35f;
        }
    }

    private static int StableInt(
        int playerSlot,
        int batSeed,
        int serial,
        int minInclusive,
        int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        return minInclusive + Mathf.FloorToInt(
            Stable01(playerSlot, batSeed, serial, 0x27B9) * (maxExclusive - minInclusive));
    }

    private static float Stable01(int playerSlot, int batSeed, int serial, int clock)
    {
        unchecked
        {
            uint x = (uint)(batSeed * 1103515245 + serial * 12345 +
                            playerSlot * 0x45D9F3B + clock * 97);
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 0x00FFFFFFu) / 16777215f;
        }
    }
}
