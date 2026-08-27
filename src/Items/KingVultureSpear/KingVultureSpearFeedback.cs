using System;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.KingVultureSpear;

internal static class KingVultureSpearFeedback
{
    private const int PullFramesRequired = 55;
    private const float PullRange = 70f;
    private const float MaxTuskDistanceFromHead = 120f;

    private sealed class PlayerInteractionState
    {
        public KingTusks.Tusk Hovered;
        public AbstractCreature Target;
        public int Side = -1;
        public int Progress;
        public int Hand = -1;
        public bool RequiresRelease;
    }

    private sealed class TuskVisualState
    {
        public bool ExtractedObserved;
        public int HoverFrames;
        public int EntryBlinkFrames;
        public float PullRatio;
        public float Phase;
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

    private static readonly ConditionalWeakTable<Player, PlayerInteractionState> PlayerStates = new();
    private static readonly ConditionalWeakTable<KingTusks.Tusk, TuskVisualState> VisualStates = new();

    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.Update += Player_Update;
        On.PlayerGraphics.Update += PlayerGraphics_Update;
        On.KingTusks.DrawSprites += KingTusks_DrawSprites;
        On.KingTusks.Tusk.DrawSprites += Tusk_DrawSprites;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Player.Update -= Player_Update;
        On.PlayerGraphics.Update -= PlayerGraphics_Update;
        On.KingTusks.DrawSprites -= KingTusks_DrawSprites;
        On.KingTusks.Tusk.DrawSprites -= Tusk_DrawSprites;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        UpdateInteractionState(self);
        orig(self, eu);
        FinishInteractionAfterPlayerUpdate(self);
    }

    private static void UpdateInteractionState(Player player)
    {
        if (player == null)
        {
            return;
        }

        PlayerInteractionState state = PlayerStates.GetOrCreateValue(player);
        bool pickupHeld = player.input != null &&
                          player.input.Length > 0 &&
                          player.input[0].pckp;

        if (!pickupHeld)
        {
            state.RequiresRelease = false;
        }

        if (player.room == null ||
            player.dead ||
            !player.Consious ||
            player.isNPC ||
            player.inShortcut ||
            player.FreeHand() < 0 ||
            !FindNearestTusk(player, out TuskCandidate candidate))
        {
            ClearHover(state);
            ClearPull(state);
            return;
        }

        MarkHovered(player, state, candidate);

        if (!pickupHeld || state.RequiresRelease)
        {
            ClearPull(state);
            return;
        }

        if (state.Target != candidate.Vulture.abstractCreature || state.Side != candidate.Side)
        {
            state.Target = candidate.Vulture.abstractCreature;
            state.Side = candidate.Side;
            state.Progress = 0;
            state.Hand = player.FreeHand();
        }

        if (state.Hand < 0)
        {
            state.Hand = player.FreeHand();
        }

        state.Progress = Math.Min(PullFramesRequired, state.Progress + 1);

        TuskVisualState visual = VisualStates.GetOrCreateValue(candidate.Tusk);
        visual.HoverFrames = 3;
        visual.PullRatio = Mathf.Clamp01(state.Progress / (float)PullFramesRequired);

        ApplyExtraPullFeedback(player, candidate, visual.PullRatio);

        if (state.Progress >= PullFramesRequired)
        {
            // The real extraction hook completes on the same hold. Keeping a local
            // release latch prevents the feedback layer from immediately targeting
            // the second tusk while the pickup button is still held.
            state.RequiresRelease = true;
        }
    }

    private static void FinishInteractionAfterPlayerUpdate(Player player)
    {
        if (player == null || !PlayerStates.TryGetValue(player, out PlayerInteractionState state))
        {
            return;
        }

        if (state.Hand >= 0 &&
            player.grasps != null &&
            state.Hand < player.grasps.Length &&
            player.grasps[state.Hand]?.grabbed is KingVultureSpear)
        {
            if (state.Hovered != null && VisualStates.TryGetValue(state.Hovered, out TuskVisualState visual))
            {
                visual.PullRatio = 0f;
            }

            state.Target = null;
            state.Side = -1;
            state.Progress = 0;
            state.Hand = -1;
            state.RequiresRelease = true;
        }
    }

    private static void MarkHovered(Player player, PlayerInteractionState state, TuskCandidate candidate)
    {
        if (state.Hovered != candidate.Tusk)
        {
            if (state.Hovered != null && VisualStates.TryGetValue(state.Hovered, out TuskVisualState oldVisual))
            {
                oldVisual.HoverFrames = 0;
                oldVisual.PullRatio = 0f;
            }

            state.Hovered = candidate.Tusk;
            TuskVisualState entered = VisualStates.GetOrCreateValue(candidate.Tusk);
            entered.EntryBlinkFrames = 8;

            if (player.room != null && candidate.Vulture?.bodyChunks != null && candidate.Vulture.bodyChunks.Length > 4)
            {
                player.room.PlaySound(SoundID.UI_Weapon_In_Range_To_Pick_Up, candidate.Vulture.bodyChunks[4]);
            }
        }

        TuskVisualState visual = VisualStates.GetOrCreateValue(candidate.Tusk);
        visual.HoverFrames = 3;

        if (player.input == null || player.input.Length == 0 || !player.input[0].pckp)
        {
            visual.PullRatio = 0f;
        }
    }

    private static void ClearHover(PlayerInteractionState state)
    {
        if (state.Hovered != null && VisualStates.TryGetValue(state.Hovered, out TuskVisualState visual))
        {
            visual.HoverFrames = 0;
            visual.PullRatio = 0f;
        }

        state.Hovered = null;
    }

    private static void ClearPull(PlayerInteractionState state)
    {
        state.Target = null;
        state.Side = -1;
        state.Progress = 0;
        state.Hand = -1;
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
                KingTusks.Tusk tusk = vulture.kingTusks.tusks[side];
                TuskVisualState visual = VisualStates.GetOrCreateValue(tusk);

                if (visual.ExtractedObserved || !IsTuskStillAtHead(tusk))
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

    private static bool IsTuskStillAtHead(KingTusks.Tusk tusk)
    {
        if (tusk?.vulture == null || tusk.chunkPoints == null || tusk.attached < 0.65f)
        {
            return false;
        }

        return Vector2.Distance(GetTuskCenter(tusk), tusk.head.pos) <= MaxTuskDistanceFromHead;
    }

    private static void ApplyExtraPullFeedback(Player player, TuskCandidate candidate, float pullRatio)
    {
        KingTusks.Tusk tusk = candidate.Tusk;
        Vector2 center = GetTuskCenter(tusk);
        Vector2 pullDirection = Custom.DirVec(center, player.mainBodyChunk.pos);
        Vector2 sideways = Custom.PerpendicularVector(pullDirection) *
                           Mathf.Sin(pullRatio * Mathf.PI * 10f) *
                           (0.04f + 0.08f * pullRatio);

        if (tusk.chunkPoints != null)
        {
            tusk.chunkPoints[0, 2] += pullDirection * (0.10f + 0.18f * pullRatio) + sideways;
            tusk.chunkPoints[1, 2] += pullDirection * (0.06f + 0.10f * pullRatio) + sideways * 0.6f;
        }

        if (candidate.Vulture?.bodyChunks != null && candidate.Vulture.bodyChunks.Length > 4)
        {
            candidate.Vulture.bodyChunks[4].vel += pullDirection * (0.015f + 0.025f * pullRatio);
        }
    }

    private static void PlayerGraphics_Update(On.PlayerGraphics.orig_Update orig, PlayerGraphics self)
    {
        orig(self);

        Player player = self?.player;
        if (player == null ||
            !PlayerStates.TryGetValue(player, out PlayerInteractionState state) ||
            state.Progress <= 0 ||
            state.Hand < 0 ||
            self.hands == null ||
            state.Hand >= self.hands.Length ||
            state.Target?.realizedCreature is not Vulture vulture ||
            vulture.kingTusks?.tusks == null ||
            state.Side < 0 ||
            state.Side >= vulture.kingTusks.tusks.Length)
        {
            return;
        }

        KingTusks.Tusk tusk = vulture.kingTusks.tusks[state.Side];
        if (VisualStates.TryGetValue(tusk, out TuskVisualState visual) && visual.ExtractedObserved)
        {
            return;
        }

        Vector2 target = GetTuskCenter(tusk);
        float pullRatio = Mathf.Clamp01(state.Progress / (float)PullFramesRequired);
        SlugcatHand hand = self.hands[state.Hand];

        hand.mode = Limb.Mode.HuntAbsolutePosition;
        hand.reachingForObject = true;
        hand.retract = false;
        hand.absoluteHuntPos = target;
        hand.vel += Custom.DirVec(hand.pos, target) * (2f + 2f * pullRatio);
        hand.pos = Vector2.Lerp(hand.pos, target, 0.25f + 0.30f * pullRatio);

        if (self.head != null)
        {
            self.head.vel += Custom.DirVec(self.head.pos, target) * (0.15f + 0.35f * pullRatio);
        }
    }

    private static void KingTusks_DrawSprites(
        On.KingTusks.orig_DrawSprites orig,
        KingTusks self,
        VultureGraphics vGraphics,
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        // Tusk.FirstSprite dynamically swaps the two sides between the front and
        // behind sprite slots as the head turns. The extraction hook hides one of
        // those slots. Re-enable both body/detail slots before every corpse draw so
        // a previously hidden slot can safely be reused by the still-attached side.
        if (self?.vulture?.dead == true && vGraphics != null)
        {
            ShowSprite(sLeaser, vGraphics.FirstKingTuskSpriteBehind + 1);
            ShowSprite(sLeaser, vGraphics.FirstKingTuskSpriteBehind + 2);
            ShowSprite(sLeaser, vGraphics.FirstKingTuskSpriteFront + 1);
            ShowSprite(sLeaser, vGraphics.FirstKingTuskSpriteFront + 2);
        }

        orig(self, vGraphics, sLeaser, rCam, timeStacker, camPos);
    }

    private static void Tusk_DrawSprites(
        On.KingTusks.Tusk.orig_DrawSprites orig,
        KingTusks.Tusk self,
        VultureGraphics vGraphics,
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        orig(self, vGraphics, sLeaser, rCam, timeStacker, camPos);

        if (self?.vulture?.dead != true || vGraphics == null || sLeaser?.sprites == null)
        {
            return;
        }

        int bodyIndex = self.TuskSprite(vGraphics);
        int detailIndex = self.TuskDetailSprite(vGraphics);
        TuskVisualState visual = VisualStates.GetOrCreateValue(self);

        // The extraction hook runs inside this hook's orig chain and hides exactly
        // the removed side. Because the parent hook above reset both dynamic slots
        // before drawing, hidden here is an unambiguous observation of extraction.
        bool bodyVisible = IsSpriteVisible(sLeaser, bodyIndex);
        bool detailVisible = IsSpriteVisible(sLeaser, detailIndex);
        visual.ExtractedObserved = !bodyVisible && !detailVisible;

        if (visual.ExtractedObserved)
        {
            visual.HoverFrames = 0;
            visual.PullRatio = 0f;
            return;
        }

        ShowSprite(sLeaser, bodyIndex);
        ShowSprite(sLeaser, detailIndex);
        RestoreVanillaTuskColors(self, vGraphics, sLeaser, rCam.currentPalette);

        visual.Phase += 0.35f;

        if (visual.HoverFrames > 0)
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(visual.Phase);
            float highlight = 0.20f + 0.16f * pulse;

            if (visual.PullRatio > 0f)
            {
                highlight = Mathf.Max(
                    highlight,
                    0.38f + 0.42f * visual.PullRatio + 0.10f * pulse);
            }

            if (visual.EntryBlinkFrames > 0 && visual.EntryBlinkFrames % 2 == 0)
            {
                highlight = Mathf.Max(highlight, 0.88f);
            }

            HighlightMesh(sLeaser.sprites[bodyIndex] as TriangleMesh, highlight);
            HighlightMesh(sLeaser.sprites[detailIndex] as TriangleMesh, highlight);
        }

        if (visual.HoverFrames > 0)
        {
            visual.HoverFrames--;
        }

        if (visual.EntryBlinkFrames > 0)
        {
            visual.EntryBlinkFrames--;
        }
    }

    private static void RestoreVanillaTuskColors(
        KingTusks.Tusk tusk,
        VultureGraphics graphics,
        RoomCamera.SpriteLeaser sLeaser,
        RoomPalette palette)
    {
        if (sLeaser.sprites[tusk.TuskSprite(graphics)] is not TriangleMesh body ||
            sLeaser.sprites[tusk.TuskDetailSprite(graphics)] is not TriangleMesh detail)
        {
            return;
        }

        float darkness = ModManager.MMF ? graphics.darkness : 0f;
        int count = Mathf.Min(body.verticeColors.Length, detail.verticeColors.Length);

        for (int i = 0; i < count; i++)
        {
            float t = Mathf.InverseLerp(0f, body.verticeColors.Length - 1f, i);
            Color bodyColor = Color.Lerp(tusk.armorColor, Color.white, Mathf.Pow(t, 2f));
            Color detailColor = Color.Lerp(
                Color.Lerp(
                    HSLColor.Lerp(graphics.ColorA, graphics.ColorB, t).rgb,
                    palette.blackColor,
                    0.65f - 0.4f * t),
                tusk.armorColor,
                Mathf.Pow(t, 2f));

            body.verticeColors[i] = Color.Lerp(bodyColor, palette.blackColor, darkness);
            detail.verticeColors[i] = Color.Lerp(detailColor, palette.blackColor, darkness);
        }

        detail.alpha = tusk.owner.patternDisplace;
    }

    private static void HighlightMesh(TriangleMesh mesh, float amount)
    {
        if (mesh?.verticeColors == null)
        {
            return;
        }

        float t = Mathf.Clamp01(amount);
        for (int i = 0; i < mesh.verticeColors.Length; i++)
        {
            mesh.verticeColors[i] = Color.Lerp(mesh.verticeColors[i], Color.white, t);
        }
    }

    private static Vector2 GetTuskCenter(KingTusks.Tusk tusk)
    {
        return (tusk.chunkPoints[0, 0] + tusk.chunkPoints[1, 0]) * 0.5f;
    }

    private static bool IsSpriteVisible(RoomCamera.SpriteLeaser sLeaser, int index)
    {
        return sLeaser?.sprites != null &&
               index >= 0 &&
               index < sLeaser.sprites.Length &&
               sLeaser.sprites[index]?.isVisible == true;
    }

    private static void ShowSprite(RoomCamera.SpriteLeaser sLeaser, int index)
    {
        if (sLeaser?.sprites != null &&
            index >= 0 &&
            index < sLeaser.sprites.Length &&
            sLeaser.sprites[index] != null)
        {
            sLeaser.sprites[index].isVisible = true;
        }
    }
}