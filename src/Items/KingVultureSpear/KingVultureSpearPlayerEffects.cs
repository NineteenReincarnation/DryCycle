using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;
using KingVultureSpearItem = global::DryCycle.Items.KingVultureSpear.KingVultureSpear;

namespace DryCycle.Items.KingVultureSpear;

internal static class KingVultureSpearPlayerEffects
{
    private const int PullFramesRequired = 55;
    private const float PullRange = 70f;
    private const float MaxTuskDistanceFromHead = 120f;

    private const float RunSpeedMultiplier = 0.75f;
    private const float PoleClimbSpeedMultiplier = 0.74f;
    private const float CorridorClimbSpeedMultiplier = 0.78f;

    private sealed class PlayerPullPoseState
    {
        public AbstractCreature Target;
        public int Side = -1;
        public int Progress;
        public bool Active;
        public bool RequiresRelease;
    }

    private sealed class VisualExtractionState
    {
        public readonly bool[] Extracted = new bool[2];
    }

    private readonly struct TuskCandidate
    {
        public TuskCandidate(Vulture vulture, KingTusks.Tusk tusk, int side, float distance)
        {
            Vulture = vulture;
            Tusk = tusk;
            Side = side;
            Distance = distance;
        }

        public Vulture Vulture { get; }
        public KingTusks.Tusk Tusk { get; }
        public int Side { get; }
        public float Distance { get; }
    }

    private static readonly ConditionalWeakTable<Player, PlayerPullPoseState> PullPoseStates = new();
    private static readonly ConditionalWeakTable<AbstractCreature, VisualExtractionState> VisualExtractionStates = new();

    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.Update += Player_Update;
        On.Player.UpdateAnimation += Player_UpdateAnimation;
        On.Player.UpdateBodyMode += Player_UpdateBodyMode;
        On.SlugcatHand.Update += SlugcatHand_Update;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Player.Update -= Player_Update;
        On.Player.UpdateAnimation -= Player_UpdateAnimation;
        On.Player.UpdateBodyMode -= Player_UpdateBodyMode;
        On.SlugcatHand.Update -= SlugcatHand_Update;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        if (self == null)
        {
            orig(self, eu);
            return;
        }

        PlayerPullPoseState state = PullPoseStates.GetOrCreateValue(self);
        int carriedBefore = CountCarriedKingVultureSpears(self);

        orig(self, eu);

        int carriedAfter = CountCarriedKingVultureSpears(self);
        if (state.Active &&
            carriedAfter > carriedBefore &&
            state.Target != null &&
            state.Side >= 0 &&
            state.Side < 2)
        {
            VisualExtractionStates.GetOrCreateValue(state.Target).Extracted[state.Side] = true;
            state.Active = false;
            state.Progress = 0;
            state.RequiresRelease = true;
            return;
        }

        UpdatePullPoseState(self, state);
        ApplyPullStrain(self, state);
    }

    private static void UpdatePullPoseState(Player player, PlayerPullPoseState state)
    {
        bool pickupHeld = player.input != null &&
                          player.input.Length > 0 &&
                          player.input[0].pckp;

        if (!pickupHeld)
        {
            ResetPullPose(state, clearReleaseLatch: true);
            return;
        }

        if (state.RequiresRelease)
        {
            state.Active = false;
            return;
        }

        if (player.room == null ||
            player.dead ||
            !player.Consious ||
            player.isNPC ||
            player.inShortcut ||
            player.FreeHand() < 0)
        {
            ResetPullPose(state, clearReleaseLatch: false);
            return;
        }

        if (!FindNearestTusk(player, out TuskCandidate candidate))
        {
            ResetPullPose(state, clearReleaseLatch: false);
            return;
        }

        if (state.Target != candidate.Vulture.abstractCreature || state.Side != candidate.Side)
        {
            state.Target = candidate.Vulture.abstractCreature;
            state.Side = candidate.Side;
            state.Progress = 0;
        }

        state.Active = true;
        state.Progress = Mathf.Min(state.Progress + 1, PullFramesRequired);
    }

    private static void ApplyPullStrain(Player player, PlayerPullPoseState state)
    {
        if (!state.Active || !TryGetActiveTusk(player, state, out KingTusks.Tusk tusk))
        {
            return;
        }

        Vector2 grabPoint = GetPullGrabPoint(tusk);
        Vector2 towardTusk = Custom.DirVec(player.mainBodyChunk.pos, grabPoint);
        float strain = Mathf.InverseLerp(0f, PullFramesRequired, state.Progress);

        player.mainBodyChunk.vel += towardTusk * Mathf.Lerp(0.025f, 0.08f, strain);

        if (player.bodyChunks != null && player.bodyChunks.Length > 1)
        {
            player.bodyChunks[1].vel -= towardTusk * Mathf.Lerp(0.008f, 0.025f, strain);
        }
    }

    private static void SlugcatHand_Update(On.SlugcatHand.orig_Update orig, SlugcatHand self)
    {
        PlayerGraphics graphics = self?.owner as PlayerGraphics;
        Player player = graphics?.player;

        if (player != null &&
            !player.isNPC &&
            PullPoseStates.TryGetValue(player, out PlayerPullPoseState state) &&
            state.Active &&
            (player.grasps == null ||
             self.limbNumber < 0 ||
             self.limbNumber >= player.grasps.Length ||
             player.grasps[self.limbNumber] == null) &&
            TryGetActiveTusk(player, state, out KingTusks.Tusk tusk))
        {
            Vector2 grabPoint = GetPullGrabPoint(tusk);
            Vector2 targetToPlayer = player.mainBodyChunk.pos - grabPoint;
            if (targetToPlayer.sqrMagnitude < 0.0001f)
            {
                targetToPlayer = new Vector2(-player.flipDirection, 0f);
            }
            targetToPlayer.Normalize();

            Vector2 perpendicular = Custom.PerpendicularVector(targetToPlayer);
            float handSide = self.limbNumber == 0 ? -1f : 1f;
            float strain = Mathf.InverseLerp(0f, PullFramesRequired, state.Progress);
            float tremor = Mathf.Sin(state.Progress * 0.6f + self.limbNumber * 1.7f) * 0.55f * strain;

            // Match vanilla HeavyCarry/Drag presentation: both free hands hunt an
            // absolute point on opposite sides of the immovable target.
            self.reachingForObject = true;
            self.absoluteHuntPos = grabPoint +
                                   perpendicular * handSide * (6f + tremor) +
                                   targetToPlayer * 1.5f;
            self.huntSpeed = 20f;
            self.quickness = 1f;
        }

        orig(self);
    }

    private static void Player_UpdateAnimation(On.Player.orig_UpdateAnimation orig, Player self)
    {
        if (!ShouldApplyCarryPenalty(self) || self.slugcatStats == null)
        {
            orig(self);
            return;
        }

        float originalPoleClimb = self.slugcatStats.poleClimbSpeedFac;
        try
        {
            self.slugcatStats.poleClimbSpeedFac = originalPoleClimb * PoleClimbSpeedMultiplier;
            orig(self);
        }
        finally
        {
            self.slugcatStats.poleClimbSpeedFac = originalPoleClimb;
        }
    }

    private static void Player_UpdateBodyMode(On.Player.orig_UpdateBodyMode orig, Player self)
    {
        if (!ShouldApplyCarryPenalty(self) || self.slugcatStats == null)
        {
            orig(self);
            return;
        }

        float originalRunSpeed = self.slugcatStats.runspeedFac;
        float originalCorridorClimb = self.slugcatStats.corridorClimbSpeedFac;

        try
        {
            self.slugcatStats.runspeedFac = originalRunSpeed * RunSpeedMultiplier;
            self.slugcatStats.corridorClimbSpeedFac = originalCorridorClimb * CorridorClimbSpeedMultiplier;
            orig(self);
        }
        finally
        {
            self.slugcatStats.runspeedFac = originalRunSpeed;
            self.slugcatStats.corridorClimbSpeedFac = originalCorridorClimb;
        }
    }

    private static bool ShouldApplyCarryPenalty(Player player)
    {
        return player != null &&
               !player.isNPC &&
               CountCarriedKingVultureSpears(player) > 0;
    }

    private static int CountCarriedKingVultureSpears(Player player)
    {
        if (player == null)
        {
            return 0;
        }

        int count = 0;
        if (player.grasps != null)
        {
            for (int i = 0; i < player.grasps.Length; i++)
            {
                if (player.grasps[i]?.grabbed is KingVultureSpearItem)
                {
                    count++;
                }
            }
        }

        if (player.spearOnBack?.spear is KingVultureSpearItem)
        {
            count++;
        }

        return count;
    }

    private static bool FindNearestTusk(Player player, out TuskCandidate candidate)
    {
        candidate = default;
        float bestDistance = float.MaxValue;
        bool found = false;

        if (player?.room?.abstractRoom?.creatures == null)
        {
            return false;
        }

        foreach (AbstractCreature abstractCreature in player.room.abstractRoom.creatures)
        {
            if (abstractCreature?.realizedCreature is not Vulture vulture ||
                !vulture.IsKing ||
                !vulture.dead ||
                vulture.kingTusks?.tusks == null ||
                vulture.kingTusks.tusks.Length < 2)
            {
                continue;
            }

            for (int side = 0; side < 2; side++)
            {
                if (IsVisuallyExtracted(abstractCreature, side))
                {
                    continue;
                }

                KingTusks.Tusk tusk = vulture.kingTusks.tusks[side];
                if (!IsTuskStillAtHead(tusk))
                {
                    continue;
                }

                Vector2 center = GetTuskCenter(tusk);
                float distance = Mathf.Min(
                    Vector2.Distance(player.mainBodyChunk.pos, center),
                    Vector2.Distance(player.mainBodyChunk.pos, vulture.bodyChunks[4].pos));

                if (distance > PullRange || distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                candidate = new TuskCandidate(vulture, tusk, side, distance);
                found = true;
            }
        }

        return found;
    }

    private static bool TryGetActiveTusk(
        Player player,
        PlayerPullPoseState state,
        out KingTusks.Tusk tusk)
    {
        tusk = null;

        if (player == null ||
            state?.Target?.realizedCreature is not Vulture vulture ||
            vulture.room != player.room ||
            !vulture.IsKing ||
            !vulture.dead ||
            state.Side < 0 ||
            state.Side >= 2 ||
            IsVisuallyExtracted(state.Target, state.Side) ||
            vulture.kingTusks?.tusks == null ||
            vulture.kingTusks.tusks.Length <= state.Side)
        {
            return false;
        }

        tusk = vulture.kingTusks.tusks[state.Side];
        return IsTuskStillAtHead(tusk);
    }

    private static bool IsTuskStillAtHead(KingTusks.Tusk tusk)
    {
        if (tusk?.vulture == null ||
            tusk.chunkPoints == null ||
            tusk.attached < 0.65f)
        {
            return false;
        }

        return Vector2.Distance(GetTuskCenter(tusk), tusk.head.pos) <= MaxTuskDistanceFromHead;
    }

    private static bool IsVisuallyExtracted(AbstractCreature creature, int side)
    {
        return creature != null &&
               side >= 0 &&
               side < 2 &&
               VisualExtractionStates.TryGetValue(creature, out VisualExtractionState state) &&
               state.Extracted[side];
    }

    private static Vector2 GetTuskCenter(KingTusks.Tusk tusk)
    {
        return (tusk.chunkPoints[0, 0] + tusk.chunkPoints[1, 0]) * 0.5f;
    }

    private static Vector2 GetPullGrabPoint(KingTusks.Tusk tusk)
    {
        return Vector2.Lerp(tusk.head.pos, GetTuskCenter(tusk), 0.5f);
    }

    private static void ResetPullPose(PlayerPullPoseState state, bool clearReleaseLatch)
    {
        state.Target = null;
        state.Side = -1;
        state.Progress = 0;
        state.Active = false;

        if (clearReleaseLatch)
        {
            state.RequiresRelease = false;
        }
    }
}
