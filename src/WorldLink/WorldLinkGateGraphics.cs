using System;
using System.Collections.Generic;
using RWCustom;
using UnityEngine;

namespace DryCycle.WorldLink;

/// <summary>
/// Port-local adaptation of vanilla RegionGateGraphics.DoorGraphic.
/// Sprites 0..66 preserve the vanilla door's exact semantic layout. WorldLink-only
/// collision silhouettes, jambs and glyph sprites are appended after that range.
/// </summary>
internal static class WorldLinkGateGraphics
{
    private const int VanillaDoorSprites = 67;
    private const int LeafBody0 = VanillaDoorSprites;
    private const int LeafBody1 = VanillaDoorSprites + 1;
    private const int Jamb0 = VanillaDoorSprites + 2;
    private const int Jamb1 = VanillaDoorSprites + 3;
    private const int GlyphBack = VanillaDoorSprites + 4;
    private const int GlyphGear = VanillaDoorSprites + 5;
    private const int GlyphGlow = VanillaDoorSprites + 6;
    private const int Glyph = VanillaDoorSprites + 7;

    internal const int TotalSprites = VanillaDoorSprites + 8;

    private static readonly float[,] BlockPhases = { { 0.12f, 0.34f }, { 0.25f, 0.39f } };
    private static readonly float[,] ArmPhases = { { 0.34f, 0.73f }, { 0.51f, 0.88f } };
    private static readonly HashSet<string> MissingAtlasWarnings = new(StringComparer.Ordinal);

    private static int CogSprite(int vertical, int side, int cog) => vertical * 4 + side * 2 + (1 - cog);
    private static int BehindPansarSprite(int side) => 8 + side;
    private static int PoleSprite(int pole) => 10 + pole;
    private static int TrackSprite(int side, int vertical) => 14 + vertical + side * 2;
    private static int CenterTrackSprite(int vertical) => 18 + vertical;
    private static int ClampSprite(int side, int clamp) => 20 + clamp * 2 + side;
    private static int BlockSprite(int side, int block) => 38 + block + side * 2;
    private static int HandSprite(int side, int block) => 42 + block + side * 2;
    private static int ArmSprite(int side, int block) => 46 + block + side * 2;
    private static int BoltSprite(int bolt) => 50 + bolt;
    private static int PansarSprite(int side) => 54 + side;
    private static int PansarSegmentSprite(int segment) => 56 + segment / 2 + ((segment & 1) == 0 ? 0 : 5);
    private static int BigScrewSprite(int vertical) => 65 + vertical;

    internal static void InitiateSprites(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        sLeaser.sprites = new FSprite[TotalSprites];

        for (int vertical = 0; vertical < 2; vertical++)
        {
            sLeaser.sprites[CenterTrackSprite(vertical)] = Atlas("RegionGate_CenterTrack" + (vertical == 0 ? "A" : "B"));
            sLeaser.sprites[CenterTrackSprite(vertical)].anchorY = vertical == 0 ? 1f : 0f;
            sLeaser.sprites[BigScrewSprite(vertical)] = Atlas("RegionGate_BigScrew");

            sLeaser.sprites[PansarSprite(vertical)] = Atlas("RegionGate_Pansar" + (vertical + 1));
            sLeaser.sprites[PansarSprite(vertical)].anchorX = vertical == 0 ? 1f : 0f;
            sLeaser.sprites[BehindPansarSprite(vertical)] = Atlas("RegionGate_Pansar" + (vertical + 1));
            sLeaser.sprites[BehindPansarSprite(vertical)].anchorX = vertical == 0 ? 1f : 0f;

            for (int side = 0; side < 2; side++)
            {
                FSprite track = Atlas("RegionGate_Track" + (vertical == 0 ? "A" : "B"));
                track.anchorY = vertical == 0 ? 1f : 0f;
                track.anchorX = 1f;
                track.scaleX = side == 0 ? 1f : -1f;
                sLeaser.sprites[TrackSprite(vertical, side)] = track;

                int blockNumber = 3 - (vertical * 2 + (1 - side)) + 1;
                FSprite block = Atlas("RegionGate_Block" + blockNumber);
                block.anchorX = side == 0 ? 1f : 0f;
                block.anchorY = 1f;
                sLeaser.sprites[BlockSprite(vertical, side)] = block;

                FSprite hand = Atlas("RegionGate_Hand");
                hand.scaleX = side == 0 ? 1f : -1f;
                hand.alpha = 14f / 15f;
                sLeaser.sprites[HandSprite(vertical, side)] = hand;

                FSprite arm = Atlas("RegionGate_Pixel");
                arm.anchorY = 0f;
                arm.alpha = 14f / 15f;
                sLeaser.sprites[ArmSprite(vertical, side)] = arm;

                for (int cog = 0; cog < 2; cog++)
                {
                    FSprite gear = Atlas("RegionGate_Cog");
                    gear.alpha = 1f - (cog == 0 ? 12f : 15f) / 30f;
                    sLeaser.sprites[CogSprite(vertical, side, cog)] = gear;
                }
            }

            for (int clamp = 0; clamp < 9; clamp++)
            {
                FSprite sprite = Atlas("RegionGate_Clamp" + ((clamp & 1) == 0 ? "A" : "B") + (vertical + 1));
                sprite.anchorX = vertical == 0 ? 1f : 0f;
                sprite.anchorY = 0f;
                sLeaser.sprites[ClampSprite(vertical, clamp)] = sprite;
            }
        }

        for (int pole = 0; pole < 4; pole++) sLeaser.sprites[PoleSprite(pole)] = Pixel();
        for (int bolt = 0; bolt < 4; bolt++)
        {
            sLeaser.sprites[BoltSprite(bolt)] = Atlas("RegionGate_Bolt");
            sLeaser.sprites[BoltSprite(bolt)].alpha = 14f / 15f;
        }
        for (int segment = 0; segment < 9; segment++)
            sLeaser.sprites[PansarSegmentSprite(segment)] = Atlas((segment & 1) == 0 ? "RegionGate_PansarSegment" : "RegionGate_PansarLock");

        sLeaser.sprites[LeafBody0] = Pixel();
        sLeaser.sprites[LeafBody1] = Pixel();
        sLeaser.sprites[Jamb0] = Pixel();
        sLeaser.sprites[Jamb1] = Pixel();
        sLeaser.sprites[GlyphBack] = Pixel();
        sLeaser.sprites[GlyphGear] = Atlas("RegionGate_Cog");
        sLeaser.sprites[GlyphGlow] = Pixel();
        sLeaser.sprites[Glyph] = WorldLinkGlyphs.Create(port.Address);

        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            sLeaser.sprites[i] ??= Pixel();
            ApplyVanillaGateShader(port, sLeaser.sprites[i], i);
        }
        AddToContainer(port, sLeaser, rCam, null);
    }

    internal static void DrawSprites(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
    {
        bool visible = port.ShouldRender;
        for (int i = 0; i < sLeaser.sprites.Length; i++) sLeaser.sprites[i].isVisible = visible;
        if (!visible)
        {
            CleanupIfNeeded(port, sLeaser, rCam);
            return;
        }

        // Vanilla RegionGateGraphics applies this quarter-pixel camera bias before
        // drawing every door part. It keeps rotated point-filtered gate sprites on the
        // same sampling phase while the room camera settles or the DevUI pans.
        camPos.x += 0.25f;
        camPos.y += 0.25f;

        float mechanical = Mathf.Clamp01(Mathf.Lerp(port.LastMechanicalFactor, port.MechanicalFactor, timeStacker));
        float open = Mathf.Clamp01(Mathf.Lerp(port.LastOpenFactor, port.OpenFactor, timeStacker));
        float closed = 1f - mechanical;
        float lengthScale = Mathf.Max(0.22f, port.Data.PassageWidth / 180f);
        // Vanilla's cogs, blocks, hands and clamps are always drawn at 1x. Only the
        // door-length dimension needs adapting for a configurable WorldLink passage.
        // Upscaling the small semi-transparent ColoredSprite2 pieces made their rotated
        // pixel silhouette shimmer, most visibly in the upper cog bank.
        float detailScale = Mathf.Clamp(lengthScale, 0.75f, 1f);
        float baseRotation = TangentAlongSpriteY(port);

        float tracksClosed = Mathf.InverseLerp(0f, 0.2f, closed);
        float blocksClosed = Mathf.InverseLerp(0.2f, 0.5f, closed);
        float armsWithdrawn = Mathf.InverseLerp(0.52f, 0.73f, closed);
        float pansarClosed = Mathf.InverseLerp(0.55f, 0.75f, closed);
        float locksClosed = Mathf.InverseLerp(0.78f, 0.9f, closed);
        float gearsTurned = Mathf.InverseLerp(0.2f, 0.9f, closed);

        DrawCollisionLeaves(port, sLeaser, open, mechanical, camPos);
        DrawVanillaTracks(port, sLeaser, tracksClosed, lengthScale, detailScale, baseRotation, camPos);
        DrawVanillaBlocksAndArms(port, sLeaser, blocksClosed, armsWithdrawn, lengthScale, detailScale, baseRotation, camPos);
        DrawVanillaClamps(port, sLeaser, closed, lengthScale, detailScale, baseRotation, camPos);
        DrawVanillaPansar(port, sLeaser, pansarClosed, gearsTurned, mechanical, lengthScale, detailScale, baseRotation, camPos);
        DrawVanillaLocksAndPoles(port, sLeaser, closed, locksClosed, mechanical, lengthScale, detailScale, baseRotation, camPos);
        DrawJambs(port, sLeaser, camPos);
        DrawGlyphAssembly(port, sLeaser, detailScale, gearsTurned, timeStacker, camPos);
        CleanupIfNeeded(port, sLeaser, rCam);
    }

    private static void DrawCollisionLeaves(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float open, float mechanical, Vector2 camPos)
    {
        float half = port.Data.PassageWidth * 0.5f;
        float inner = half * open;
        float length = Mathf.Max(0f, half - inner);
        float alpha = Mathf.Lerp(0.82f, 0.10f, Smooth01(Mathf.InverseLerp(0.18f, 0.74f, mechanical)));
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            Vector2 center = Local(port, side * (inner + length * 0.5f), 0f);
            FSprite body = sLeaser.sprites[i == 0 ? LeafBody0 : LeafBody1];
            body.x = center.x - camPos.x;
            body.y = center.y - camPos.y;
            body.rotation = TangentAlongSpriteY(port);
            body.scaleX = port.Data.PanelThickness;
            body.scaleY = length;
            body.alpha = alpha;
            body.isVisible = length > 0.1f && alpha > 0.015f;
        }
    }

    private static void DrawVanillaTracks(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float tracksClosed, float lengthScale, float detailScale, float baseRotation, Vector2 camPos)
    {
        float centerProgress = Mathf.Pow(Mathf.Max(0f, Mathf.InverseLerp(1f, 0.28f, tracksClosed)), 0.6f);
        for (int vertical = 0; vertical < 2; vertical++)
        {
            FSprite center = sLeaser.sprites[CenterTrackSprite(vertical)];
            float y = vertical == 0 ? centerProgress * 65f : -180f - centerProgress * 130f;
            PlaceOriginal(port, center, 0f, y, baseRotation, lengthScale, detailScale, camPos);
            SetOriginalScale(center, detailScale, lengthScale);
            center.alpha = 1f - Mathf.Lerp(1.5f, 2.5f, centerProgress) / 30f;

            for (int side = 0; side < 2; side++)
            {
                float threshold = 0.70f;
                float phase = side == 0 ? 0.20f : 0.34f;
                float progress = Mathf.Pow(Mathf.Max(0f, Mathf.InverseLerp(threshold, phase, tracksClosed)), 1.4f);
                FSprite track = sLeaser.sprites[TrackSprite(vertical, side)];
                float trackY = vertical == 0 ? progress * 130f : -180f - progress * 65f;
                PlaceOriginal(port, track, side == 0 ? -9f : 9f, trackY, baseRotation, lengthScale, detailScale, camPos);
                SetOriginalScale(track, (side == 0 ? 1f : -1f) * detailScale, lengthScale);
                track.alpha = 1f - Mathf.Lerp(1.5f, 2.5f, progress) / 30f;
            }
        }
    }

    private static void DrawVanillaBlocksAndArms(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float blocksClosed, float armsWithdrawn, float lengthScale, float detailScale, float baseRotation, Vector2 camPos)
    {
        for (int vertical = 0; vertical < 2; vertical++)
        {
            Vector2 armObstacle = new(0f, vertical == 0 ? -220f : 40f);
            for (int side = 0; side < 2; side++)
            {
                float sideSign = side == 0 ? -1f : 1f;
                float verticalSign = vertical == 0 ? -1f : 1f;
                float blockProgress = Mathf.InverseLerp(BlockPhases[vertical, side], 1f, blocksClosed);
                Vector2 blockPos = Vector2.zero;
                Vector2 offset = Custom.DegToVec(30f + blockProgress * 150f);
                offset.y += 1f;
                offset.x *= 20f * sideSign * Mathf.Lerp(blockProgress, 1f, 0.5f);
                offset.y *= 30f * verticalSign;
                offset.y += 60f * Mathf.InverseLerp(0.2f, 0f, blockProgress) * verticalSign;
                blockPos += offset;
                if (vertical == 0) blockPos.y -= 90f;

                float blockRotation = Mathf.Pow(Mathf.Sin(Mathf.PI * blockProgress), 3f) * -2f * sideSign * verticalSign;
                FSprite block = sLeaser.sprites[BlockSprite(vertical, side)];
                PlaceOriginal(port, block, blockPos.x, blockPos.y, baseRotation + blockRotation, lengthScale, detailScale, camPos);
                SetOriginalScale(block, detailScale, detailScale);
                block.alpha = 1f - Mathf.Lerp(3f, 2f, Mathf.Pow(blockProgress, 7f)) / 30f;

                float armProgress = Mathf.InverseLerp(0f, ArmPhases[vertical, side], armsWithdrawn);
                float armShift = Mathf.Lerp(0f, 100f * verticalSign, Mathf.Pow(Mathf.Max(0f, armProgress), 0.8f));
                float handY = vertical == 0 ? -10f : -80f;
                Vector2 handPos = blockPos + Custom.RotateAroundOrigo(new Vector2(22f * sideSign, handY), blockRotation);
                handPos.y += armShift;
                Vector2 armTarget = new(
                    sideSign * Mathf.Lerp(30f, -35f, Mathf.Pow(0.5f * blockProgress + 0.5f * Mathf.Sin(blockProgress * Mathf.PI), 1f + 4f * blockProgress)),
                    vertical == 1 ? 60f : -240f);
                armTarget.y += armShift;

                float collisionTime = Custom.CirclesCollisionTime(handPos.x, handPos.y, armObstacle.x, armObstacle.y,
                    armTarget.x - handPos.x, armTarget.y - handPos.y, 1f, 28f);
                if (collisionTime > 0f && collisionTime < 1f) armTarget = Vector2.Lerp(handPos, armTarget, collisionTime);

                Vector2 handWorld = OriginalWorld(port, handPos.x, handPos.y, lengthScale, detailScale);
                Vector2 targetWorld = OriginalWorld(port, armTarget.x, armTarget.y, lengthScale, detailScale);
                FSprite hand = sLeaser.sprites[HandSprite(vertical, side)];
                hand.x = handWorld.x - camPos.x;
                hand.y = handWorld.y - camPos.y;
                hand.rotation = baseRotation + blockRotation;
                SetOriginalScale(hand, (side == 0 ? 1f : -1f) * detailScale, detailScale);

                FSprite arm = sLeaser.sprites[ArmSprite(vertical, side)];
                SetLine(arm, handWorld, targetWorld, 3f * detailScale, camPos);
                arm.alpha = 14f / 15f;
            }
        }
    }

    private static void DrawVanillaClamps(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float closed, float lengthScale, float detailScale, float baseRotation, Vector2 camPos)
    {
        for (int side = 0; side < 2; side++)
        {
            float sideSign = side == 0 ? -1f : 1f;
            for (int clamp = 0; clamp < 9; clamp++)
            {
                float start = 0.16f + clamp * 0.016f;
                float lockProgress = Smooth01(Mathf.InverseLerp(start, start + 0.17f, closed));
                int stackedAbove = Mathf.Min(2, 8 - clamp);
                Vector2 stackPos = new(sideSign * (7f + 7f * stackedAbove), -5f + 6f * stackedAbove);
                Vector2 lockPos = new(0f, -180f * ((clamp + 1f) / 9f));
                Vector2 pos = Vector2.Lerp(stackPos, lockPos, lockProgress);
                float rotation = sideSign * 45f * Mathf.Sin(lockProgress * Mathf.PI) * (1f - clamp / 12f);

                FSprite sprite = sLeaser.sprites[ClampSprite(side, clamp)];
                PlaceOriginal(port, sprite, pos.x, pos.y, baseRotation + rotation, lengthScale, detailScale, camPos);
                SetOriginalScale(sprite, detailScale, detailScale);
                sprite.alpha = Mathf.Lerp(1f, 0.9f, lockProgress);
            }
        }
    }

    private static void DrawVanillaPansar(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float pansarClosed, float gearsTurned, float mechanical, float lengthScale, float detailScale, float baseRotation, Vector2 camPos)
    {
        for (int side = 0; side < 2; side++)
        {
            float sideSign = side == 0 ? -1f : 1f;
            float x = sideSign * (7f * pansarClosed + Mathf.Sin(pansarClosed * Mathf.PI) * 25f);
            FSprite front = sLeaser.sprites[PansarSprite(side)];
            FSprite behind = sLeaser.sprites[BehindPansarSprite(side)];
            PlaceOriginal(port, front, x, -90f, baseRotation, lengthScale, detailScale, camPos);
            PlaceOriginal(port, behind, x, -90f, baseRotation, lengthScale, detailScale, camPos);
            SetOriginalScale(front, detailScale, lengthScale);
            SetOriginalScale(behind, detailScale, lengthScale);
            front.isVisible = pansarClosed > 0.5f;
            front.alpha = Mathf.Lerp(0.7f, 1f, pansarClosed);
            behind.alpha = Mathf.Lerp(0.7f, 1f, pansarClosed);

            float screwY = side == 0 ? -220f : 40f;
            FSprite screw = sLeaser.sprites[BigScrewSprite(side)];
            PlaceOriginal(port, screw, 0f, screwY, baseRotation + (side == 0 ? -1f : 1f) * mechanical * 720f, lengthScale, detailScale, camPos);
            SetOriginalScale(screw, detailScale, detailScale);

            for (int lateral = 0; lateral < 2; lateral++)
            {
                for (int cog = 0; cog < 2; cog++)
                {
                    // Vanilla subtracts the signed endpoint offset from -90. Keeping
                    // this sign is important: the upper and lower gear banks occupy
                    // opposite fixed machine bays instead of appearing to eject from
                    // the door leaf.
                    float y = -90f - (side == 0 ? -1f : 1f) * (cog == 0 ? 150f : 175f) * (side == 0 ? 1f : 1.2f);
                    float xCog = (lateral == 0 ? -1f : 1f) * (cog == 0 ? 40f : 50f) * (side == 0 ? 1f : 0.8f);
                    float turn = (gearsTurned * 0.5f + 0.5f * Mathf.Sin(gearsTurned * Mathf.PI)) * (cog == 0 ? 90f : 210f);
                    turn *= (lateral == 0 ? -1f : 1f) * (side == 0 ? 1f : -1f);
                    FSprite gear = sLeaser.sprites[CogSprite(side, lateral, cog)];
                    PlaceOriginal(port, gear, xCog, y, baseRotation + turn, lengthScale, detailScale, camPos);
                    SetOriginalScale(gear, detailScale, detailScale);
                }
            }
        }
    }

    private static void DrawVanillaLocksAndPoles(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float closed, float locksClosed, float mechanical, float lengthScale, float detailScale, float baseRotation, Vector2 camPos)
    {
        float lockBolt = Smooth01(Mathf.InverseLerp(0.93f, 1f, closed));
        for (int segment = 0; segment < 9; segment++)
        {
            float segmentTarget = (segment + 0.5f) / 9f;
            float position = Mathf.Lerp(0f, segmentTarget, Mathf.Pow(locksClosed, 1.5f - segmentTarget)) - Mathf.Pow(1f - locksClosed, 2f) * 0.3f;
            float rotation = 0f;
            if ((segment & 1) == 1) rotation = 90f * lockBolt * ((((segment / 2) & 1) == 0) ? -1f : 1f);

            FSprite sprite = sLeaser.sprites[PansarSegmentSprite(segment)];
            PlaceOriginal(port, sprite, 0f, -180f * position, baseRotation + rotation, lengthScale, detailScale, camPos);
            SetOriginalScale(sprite, detailScale, lengthScale);
            sprite.alpha = Mathf.Lerp(1f, 0.8f, Mathf.InverseLerp(0.3f, -0.2f, position));
        }

        for (int bolt = 0; bolt < 4; bolt++)
        {
            FSprite sprite = sLeaser.sprites[BoltSprite(bolt)];
            PlaceOriginal(port, sprite, 0f, -30f - 40f * bolt, baseRotation, lengthScale, detailScale, camPos);
            SetOriginalScale(sprite, detailScale, detailScale);
            sprite.isVisible = closed >= 0.955f + bolt * 0.008f;
        }

        float deployment = Smooth01(Mathf.InverseLerp(0.08f, 0f, closed));
        for (int pole = 0; pole < 4; pole++)
        {
            FSprite sprite = sLeaser.sprites[PoleSprite(pole)];
            float x = (((pole > 0 && pole < 3) ? 14f : 11f) + (pole < 2 ? 1f : 0f)) * (pole - 1.5f);
            float y = -90f + (((pole & 1) == 0) ? -1f : 1f) * 200f * Mathf.Pow(deployment, 1.4f);
            PlaceOriginal(port, sprite, x, y, baseRotation, lengthScale, detailScale, camPos);
            sprite.scaleX = 3f * detailScale;
            sprite.scaleY = 200f * lengthScale;
            // Vanilla keeps the retracted poles visible at the center as soon as the
            // door leaves the fully closed pose, then deploys them only at the end.
            sprite.isVisible = mechanical > 0.001f;
        }
    }

    private static void DrawJambs(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, Vector2 camPos)
    {
        float half = port.Data.PassageWidth * 0.5f;
        float jambThickness = Mathf.Max(18f, port.Data.PanelThickness * 2.4f);
        float jambDepth = Mathf.Max(10f, port.Data.PanelThickness * 1.25f);
        for (int i = 0; i < 2; i++)
        {
            FSprite jamb = sLeaser.sprites[i == 0 ? Jamb0 : Jamb1];
            Vector2 center = Local(port, (i == 0 ? -1f : 1f) * (half + jambThickness * 0.22f), 0f);
            jamb.x = center.x - camPos.x;
            jamb.y = center.y - camPos.y;
            jamb.rotation = TangentAlongSpriteY(port);
            jamb.scaleX = jambDepth;
            jamb.scaleY = jambThickness;
        }
    }

    private static void DrawGlyphAssembly(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float detailScale, float gearsTurned, float timeStacker, Vector2 camPos)
    {
        Vector2 gp = port.Placed.pos + port.Data.GlyphWorldOffset;
        float clock = (port.room?.game?.clock ?? 0) + timeStacker;
        float pulse = 0.5f + 0.5f * Mathf.Sin(clock / (port.Denied ? 7f : 14f));
        float housingScale = Mathf.Clamp(detailScale, 0.78f, 1.35f);

        FSprite back = sLeaser.sprites[GlyphBack];
        back.x = gp.x - camPos.x;
        back.y = gp.y - camPos.y;
        back.rotation = TangentAlongSpriteY(port);
        back.scaleX = 36f * housingScale;
        back.scaleY = 36f * housingScale;
        back.alpha = 0.88f;

        FSprite gear = sLeaser.sprites[GlyphGear];
        gear.x = gp.x - camPos.x;
        gear.y = gp.y - camPos.y;
        gear.rotation = gearsTurned * 180f + clock * 0.12f;
        gear.scale = 0.88f * housingScale;
        gear.alpha = 0.72f;

        FSprite glow = sLeaser.sprites[GlyphGlow];
        glow.x = gp.x - camPos.x;
        glow.y = gp.y - camPos.y;
        glow.scaleX = 28f * housingScale;
        glow.scaleY = 28f * housingScale;
        glow.alpha = port.Denied ? 0.12f + 0.18f * pulse : 0.04f + 0.08f * pulse;
        glow.color = port.Denied ? Color.red : Color.white;

        FSprite glyph = sLeaser.sprites[Glyph];
        WorldLinkGlyphs.Refresh(glyph, port.Address);
        glyph.x = gp.x - camPos.x;
        glyph.y = gp.y - camPos.y;
        glyph.rotation = 0f;
        glyph.scale = Mathf.Clamp(0.92f * housingScale, 0.75f, 1.25f);
        glyph.color = port.Denied
            ? Color.Lerp(Color.red, Color.white, 0.28f + 0.42f * pulse)
            : Color.Lerp(new Color(0.65f, 0.65f, 0.70f), Color.white, 0.28f + 0.34f * pulse);
    }

    internal static void ApplyPalette(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
    {
        Color black = palette.blackColor;
        Color fog = palette.fogColor;
        Color poleColor = Color.Lerp(black, fog, 0.12f);
        Color armor = Color.Lerp(new Color(0.05f, 0.05f, 0f), Color.Lerp(palette.texture.GetPixel(6, 4), fog, 0.5f), 0.25f);

        for (int pole = 0; pole < 4; pole++) sLeaser.sprites[PoleSprite(pole)].color = poleColor;
        for (int side = 0; side < 2; side++)
            for (int block = 0; block < 2; block++)
                sLeaser.sprites[BlockSprite(side, block)].color = armor;

        sLeaser.sprites[LeafBody0].color = black;
        sLeaser.sprites[LeafBody1].color = black;
        sLeaser.sprites[Jamb0].color = black;
        sLeaser.sprites[Jamb1].color = black;
        sLeaser.sprites[GlyphBack].color = Color.Lerp(black, fog, 0.08f);
        sLeaser.sprites[GlyphGear].color = Color.Lerp(black, fog, 0.22f);
    }

    internal static void AddToContainer(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer newContainer)
    {
        FContainer front = newContainer ?? rCam.ReturnFContainer("Items");
        FContainer back = rCam.ReturnFContainer("Midground");
        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            if ((i >= PoleSprite(0) && i <= PoleSprite(3)) || i == LeafBody0 || i == LeafBody1) back.AddChild(sLeaser.sprites[i]);
            else front.AddChild(sLeaser.sprites[i]);
        }
    }

    internal static void OnMechanicalFactorChanged(MultiGatePortRuntime port, float oldMechanical, float newMechanical)
    {
        if (port?.room == null || port.Placed == null || Mathf.Abs(newMechanical - oldMechanical) < 0.00001f) return;
        if (newMechanical > oldMechanical)
        {
            Cross(port, oldMechanical, newMechanical, 0.10f, SoundID.Gate_Secure_Rail_Up, 0.95f);
            Cross(port, oldMechanical, newMechanical, 0.25f, SoundID.Gate_Panser_Off, 1.00f);
            Cross(port, oldMechanical, newMechanical, 0.50f, SoundID.Gate_Pillows_Move_Out, 0.98f);
            Cross(port, oldMechanical, newMechanical, 0.94f, SoundID.Gate_Poles_Out, 1.00f);
        }
        else
        {
            CrossDown(port, oldMechanical, newMechanical, 0.94f, SoundID.Gate_Poles_And_Rails_In, 1.00f);
            CrossDown(port, oldMechanical, newMechanical, 0.50f, SoundID.Gate_Pillows_Move_In, 0.98f);
            CrossDown(port, oldMechanical, newMechanical, 0.25f, SoundID.Gate_Panser_On, 1.00f);
            if (oldMechanical > 0.10f && newMechanical <= 0.10f)
            {
                port.room.PlaySound(SoundID.Gate_Secure_Rail_Down, port.Placed.pos, 1f, 1f);
                port.room.ScreenMovement(port.Placed.pos, Vector2.zero, 0.35f);
            }
        }
    }

    private static Vector2 OriginalWorld(MultiGatePortRuntime port, float x, float yFromTop, float lengthScale, float detailScale)
    {
        // Stretch the original 180px door span, but keep machinery outside the top and
        // bottom endpoints at vanilla-sized offsets. Uniformly multiplying those outer
        // bays by PassageWidth made the gears drift far away on tall authored ports.
        float half = port.Data.PassageWidth * 0.5f;
        float tangent = yFromTop switch
        {
            > 0f => half + yFromTop * detailScale,
            < -180f => -half + (yFromTop + 180f) * detailScale,
            _ => half + yFromTop / 180f * port.Data.PassageWidth
        };
        float normal = x * detailScale;
        return Local(port, tangent, normal);
    }

    private static void PlaceOriginal(MultiGatePortRuntime port, FSprite sprite, float x, float yFromTop, float rotation,
        float lengthScale, float detailScale, Vector2 camPos)
    {
        Vector2 pos = OriginalWorld(port, x, yFromTop, lengthScale, detailScale);
        sprite.x = pos.x - camPos.x;
        sprite.y = pos.y - camPos.y;
        sprite.rotation = rotation;
        sprite.isVisible = true;
    }

    private static void SetOriginalScale(FSprite sprite, float xScale, float yScale)
    {
        sprite.scaleX = xScale;
        sprite.scaleY = yScale;
    }

    private static Vector2 Local(MultiGatePortRuntime port, float tangent, float normal) =>
        port.Placed.pos + port.Data.Tangent * tangent + port.Data.Normal * normal;

    private static float TangentAlongSpriteY(MultiGatePortRuntime port) => Custom.VecToDeg(port.Data.Tangent);

    private static void SetLine(FSprite sprite, Vector2 a, Vector2 b, float width, Vector2 camPos)
    {
        sprite.x = a.x - camPos.x;
        sprite.y = a.y - camPos.y;
        sprite.anchorX = 0.5f;
        sprite.anchorY = 0f;
        sprite.rotation = Custom.AimFromOneVectorToAnother(a, b);
        float sourceWidth = Mathf.Max(1f, sprite.element.sourceSize.x);
        float sourceHeight = Mathf.Max(1f, sprite.element.sourceSize.y);
        sprite.scaleX = width / sourceWidth;
        sprite.scaleY = Vector2.Distance(a, b) / sourceHeight;
        sprite.isVisible = Vector2.Distance(a, b) > 0.5f;
    }

    private static float Smooth01(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - 2f * t);
    }

    private static void CleanupIfNeeded(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        if (port.slatedForDeletetion || port.room != rCam.room) sLeaser.CleanSpritesAndRemove();
    }

    private static FSprite Pixel() => new("pixel") { anchorX = 0.5f, anchorY = 0.5f };

    private static FSprite Atlas(string name)
    {
        try
        {
            return new FSprite(name);
        }
        catch (Exception ex)
        {
            if (MissingAtlasWarnings.Add(name))
                Plugin.Logger?.LogWarning($"WorldLink gate graphics could not load atlas element '{name}': {ex.Message}. Falling back to pixel geometry.");
            return Pixel();
        }
    }

    private static void ApplyVanillaGateShader(MultiGatePortRuntime port, FSprite sprite, int index)
    {
        try
        {
            if (index >= VanillaDoorSprites || (index >= PoleSprite(0) && index <= PoleSprite(3))) return;
            if (port?.room?.game?.rainWorld?.Shaders != null && port.room.game.rainWorld.Shaders.ContainsKey("ColoredSprite2"))
                sprite.shader = port.room.game.rainWorld.Shaders["ColoredSprite2"];
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"WorldLink gate graphics shader setup failed: {ex.Message}");
        }
    }

    private static void Cross(MultiGatePortRuntime port, float oldValue, float newValue, float threshold, SoundID sound, float pitch)
    {
        if (oldValue < threshold && newValue >= threshold) port.room.PlaySound(sound, port.Placed.pos, 1f, pitch);
    }

    private static void CrossDown(MultiGatePortRuntime port, float oldValue, float newValue, float threshold, SoundID sound, float pitch)
    {
        if (oldValue > threshold && newValue <= threshold) port.room.PlaySound(sound, port.Placed.pos, 1f, pitch);
    }
}
