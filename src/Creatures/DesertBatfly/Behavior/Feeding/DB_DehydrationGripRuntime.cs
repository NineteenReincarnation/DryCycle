using System.Runtime.CompilerServices;
using DryCycle.Thirst;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Species-specific pickup gate for weakened players. It covers both ordinary manual pickup
/// and vanilla Player.Collide batfly auto-capture while leaving other items, forced grabs and
/// NPC grabs on vanilla semantics. Existing held-grasp weakness remains owned by
/// DB_RestraintRuntime.
/// </summary>
internal static class DB_DehydrationGripRuntime
{
    private sealed class State
    {
        internal int RetryUntilClock;
        internal int AttemptSerial;
        internal DB_Creature ApprovedCollisionBat;
    }

    private static ConditionalWeakTable<Player, State> states = new();
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        states = new ConditionalWeakTable<Player, State>();
        On.Player.Collide += PlayerCollide;
        On.Player.SlugcatGrab += SlugcatGrab;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        enabled = false;
        On.Player.SlugcatGrab -= SlugcatGrab;
        On.Player.Collide -= PlayerCollide;
        states = new ConditionalWeakTable<Player, State>();
    }

    private static void PlayerCollide(
        On.Player.orig_Collide orig,
        Player self,
        PhysicalObject otherObject,
        int myChunk,
        int otherChunk)
    {
        if (!CollisionPickupAttempt(self, otherObject, out DB_Creature bat,
                out PlayerDehydrationSnapshot facts))
        {
            orig(self, otherObject, myChunk, otherChunk);
            return;
        }

        State state = states.GetOrCreateValue(self);
        int clock = self.room?.game?.clock ?? 0;
        if (PassesGripGate(self, bat, facts, state, clock))
        {
            DB_Creature previousApproved = state.ApprovedCollisionBat;
            state.ApprovedCollisionBat = bat;
            try
            {
                orig(self, otherObject, myChunk, otherChunk);
            }
            finally
            {
                state.ApprovedCollisionBat = previousApproved;
            }
            return;
        }

        // Vanilla auto-capture performs its catch sound/swarm depletion before SlugcatGrab.
        // A temporary shortcutDelay makes vanilla Grabability return CantGrab for this one
        // collision, so a failed dehydration check never produces those false-positive side
        // effects. Restore the real delay immediately after the collision call.
        int originalShortcutDelay = bat.shortcutDelay;
        if (originalShortcutDelay == 0)
            bat.shortcutDelay = 1;
        try
        {
            orig(self, otherObject, myChunk, otherChunk);
        }
        finally
        {
            bat.shortcutDelay = originalShortcutDelay;
        }
    }

    private static void SlugcatGrab(
        On.Player.orig_SlugcatGrab orig,
        Player self,
        PhysicalObject obj,
        int graspUsed)
    {
        if (self == null || obj is not DB_Creature bat || self.isNPC || self.dead ||
            self.room == null || bat.room != self.room)
        {
            orig(self, obj, graspUsed);
            return;
        }

        State state = states.GetOrCreateValue(self);
        if (ReferenceEquals(state.ApprovedCollisionBat, bat))
        {
            orig(self, obj, graspUsed);
            return;
        }

        // Outside Player.Collide, only an actual candidate-driven manual pickup is gated.
        // Scripted/forced SlugcatGrab calls therefore retain vanilla behavior.
        if (self.wantToPickUp <= 0 || !ReferenceEquals(self.pickUpCandidate, obj))
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

        int clock = self.room.game?.clock ?? 0;
        if (PassesGripGate(self, bat, facts, state, clock))
        {
            orig(self, obj, graspUsed);
            return;
        }
    }

    private static bool CollisionPickupAttempt(
        Player self,
        PhysicalObject otherObject,
        out DB_Creature bat,
        out PlayerDehydrationSnapshot facts)
    {
        bat = otherObject as DB_Creature;
        facts = default;
        if (self == null || bat == null || self.isNPC || self.dead || !self.Consious ||
            self.room == null || bat.room != self.room)
            return false;

        facts = PlayerDehydrationFacts.For(self);
        if (facts.PickupFailureChance <= 0.0001f) return false;

        bool manualAttempt = self.wantToPickUp > 0 && self.CanIPickThisUp(bat);
        return manualAttempt || VanillaAutoCaptureWouldAttempt(self, bat);
    }

    private static bool VanillaAutoCaptureWouldAttempt(Player self, DB_Creature bat)
    {
        if (self.FoodInStomach >= self.MaxFoodInStomach || !self.AllowGrabbingBatflys() ||
            self.Grabability(bat) != Player.ObjectGrabability.OneHand)
            return false;

        if (ModManager.MSC &&
            ((self.grabbedBy.Count > 0 && self.grabbedBy[0].grabber is Player) ||
             self.onBack != null))
            return false;

        bool mmfHeavyHandBlock = false;
        for (int i = 0; i < 2; i++)
        {
            Creature.Grasp grasp = self.grasps[i];
            if (grasp != null &&
                (self.Grabability(grasp.grabbed) == Player.ObjectGrabability.TwoHands ||
                 self.isSlugpup))
            {
                mmfHeavyHandBlock = true;
                break;
            }
        }
        if (ModManager.MMF && mmfHeavyHandBlock) return false;

        return self.grasps[0] == null || self.grasps[1] == null;
    }

    private static bool PassesGripGate(
        Player self,
        DB_Creature bat,
        PlayerDehydrationSnapshot facts,
        State state,
        int clock)
    {
        if (clock < state.RetryUntilClock)
        {
            self.wantToPickUp = 0;
            return false;
        }

        state.AttemptSerial++;
        float roll = Stable01(
            self.playerState?.playerNumber ?? 0,
            bat.Personality.VisualSeed,
            state.AttemptSerial,
            clock);
        if (roll >= facts.PickupFailureChance)
            return true;

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
        return false;
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
