using System;
using System.Collections.Generic;
using RWCustom;
using UnityEngine;

namespace DryCycle.WorldLink;

/// <summary>
/// RegionGate-inspired mechanical assembly authored entirely in the port-local
/// Tangent/Normal frame. MechanicalFactor drives lock/rail choreography; OpenFactor is
/// the only source of truth for the visible physical leaves and their colliders.
/// </summary>
internal static class WorldLinkGateGraphics
{
    internal const int TotalSprites = 67;

    private const int LeafBody0 = 0;
    private const int LeafBody1 = 1;
    private const int LeafPlate0 = 2;
    private const int LeafPlate1 = 3;
    private const int TrackStart = 4;
    private const int CenterTrackStart = 8;
    private const int BlockStart = 10;
    private const int HandStart = 14;
    private const int ArmStart = 18;
    private const int CogStart = 22;
    private const int ClampStart = 30;
    private const int BoltStart = 42;
    private const int PansarSegmentStart = 46;
    private const int BigScrewStart = 55;
    private const int PoleStart = 57;
    private const int JambStart = 61;
    private const int GlyphBack = 63;
    private const int GlyphGear = 64;
    private const int GlyphGlow = 65;
    private const int Glyph = 66;

    private static readonly HashSet<string> MissingAtlasWarnings = new(StringComparer.Ordinal);

    internal static void InitiateSprites(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        sLeaser.sprites = new FSprite[TotalSprites];
        sLeaser.sprites[LeafBody0] = Pixel();
        sLeaser.sprites[LeafBody1] = Pixel();
        sLeaser.sprites[LeafPlate0] = Atlas("RegionGate_Pansar1");
        sLeaser.sprites[LeafPlate1] = Atlas("RegionGate_Pansar2");

        for (int i = 0; i < 4; i++)
            sLeaser.sprites[TrackStart + i] = Atlas(i < 2 ? "RegionGate_TrackA" : "RegionGate_TrackB");

        sLeaser.sprites[CenterTrackStart] = Atlas("RegionGate_CenterTrackA");
        sLeaser.sprites[CenterTrackStart + 1] = Atlas("RegionGate_CenterTrackB");

        for (int i = 0; i < 4; i++)
        {
            sLeaser.sprites[BlockStart + i] = Atlas("RegionGate_Block" + (i + 1));
            sLeaser.sprites[HandStart + i] = Atlas("RegionGate_Hand");
            sLeaser.sprites[ArmStart + i] = Atlas("RegionGate_Pixel");
            sLeaser.sprites[ArmStart + i].anchorY = 0f;
        }

        for (int i = 0; i < 8; i++) sLeaser.sprites[CogStart + i] = Atlas("RegionGate_Cog");

        string[] clampNames = { "RegionGate_ClampA1", "RegionGate_ClampB1", "RegionGate_ClampA2", "RegionGate_ClampB2" };
        for (int i = 0; i < 12; i++) sLeaser.sprites[ClampStart + i] = Atlas(clampNames[i % clampNames.Length]);
        for (int i = 0; i < 4; i++) sLeaser.sprites[BoltStart + i] = Atlas("RegionGate_Bolt");
        for (int i = 0; i < 9; i++) sLeaser.sprites[PansarSegmentStart + i] = Atlas((i & 1) == 0 ? "RegionGate_PansarSegment" : "RegionGate_PansarLock");

        sLeaser.sprites[BigScrewStart] = Atlas("RegionGate_BigScrew");
        sLeaser.sprites[BigScrewStart + 1] = Atlas("RegionGate_BigScrew");
        for (int i = 0; i < 4; i++) sLeaser.sprites[PoleStart + i] = Pixel();
        sLeaser.sprites[JambStart] = Pixel();
        sLeaser.sprites[JambStart + 1] = Pixel();
        sLeaser.sprites[GlyphBack] = Pixel();
        sLeaser.sprites[GlyphGear] = Atlas("RegionGate_Cog");
        sLeaser.sprites[GlyphGlow] = Pixel();
        sLeaser.sprites[Glyph] = WorldLinkGlyphs.Create(port.Address);

        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            sLeaser.sprites[i] ??= Pixel();
            sLeaser.sprites[i].anchorX = 0.5f;
            sLeaser.sprites[i].anchorY = 0.5f;
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

        float mechanical = Mathf.Clamp01(Mathf.Lerp(port.LastMechanicalFactor, port.MechanicalFactor, timeStacker));
        float open = Mathf.Clamp01(Mathf.Lerp(port.LastOpenFactor, port.OpenFactor, timeStacker));
        float leafHalf = port.Data.PassageWidth * 0.5f;
        float leafInner = leafHalf * open;
        float leafLength = Mathf.Max(0f, leafHalf - leafInner);
        float thickness = port.Data.PanelThickness;
        float detailScale = Mathf.Clamp(port.Data.PassageWidth / 180f, 0.68f, 1.55f);
        float axisRotation = GateAxisRotation(port);

        float unlock = Smooth01(Mathf.InverseLerp(0.00f, 0.18f, mechanical));
        float unclamp = Smooth01(Mathf.InverseLerp(0.08f, 0.30f, mechanical));
        float armorRelease = Smooth01(Mathf.InverseLerp(0.18f, 0.50f, mechanical));
        float railRelease = Smooth01(Mathf.InverseLerp(0.68f, 0.94f, mechanical));
        float gearMotion = Smooth01(Mathf.InverseLerp(0.00f, 0.96f, mechanical));

        DrawLeaf(port, sLeaser.sprites[LeafBody0], sLeaser.sprites[LeafPlate0], -1, leafInner, leafLength, thickness, detailScale, armorRelease, camPos);
        DrawLeaf(port, sLeaser.sprites[LeafBody1], sLeaser.sprites[LeafPlate1], 1, leafInner, leafLength, thickness, detailScale, armorRelease, camPos);
        DrawBackRails(port, sLeaser, thickness, detailScale, railRelease, axisRotation, camPos);
        DrawEndpointHousings(port, sLeaser, leafHalf, thickness, detailScale, open, unlock, gearMotion, axisRotation, camPos);
        DrawActuators(port, sLeaser, leafHalf, leafInner, leafLength, thickness, detailScale, unclamp, axisRotation, camPos);
        DrawClamps(port, sLeaser, leafInner, leafLength, thickness, detailScale, unclamp, axisRotation, camPos);
        DrawBolts(port, sLeaser, leafInner, leafLength, thickness, detailScale, unlock, axisRotation, camPos);
        DrawPansarSegments(port, sLeaser, leafInner, leafLength, detailScale, armorRelease, axisRotation, camPos);
        DrawPoles(port, sLeaser, leafHalf, thickness, detailScale, railRelease, camPos);
        DrawJambs(port, sLeaser, leafHalf, thickness, camPos);
        DrawGlyphAssembly(port, sLeaser, detailScale, gearMotion, timeStacker, camPos);
        CleanupIfNeeded(port, sLeaser, rCam);
    }

    internal static void ApplyPalette(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
    {
        Color black = palette.blackColor;
        Color fog = palette.fogColor;
        Color metal = Color.Lerp(black, fog, 0.10f);
        Color edge = Color.Lerp(black, fog, 0.22f);
        Color armor = Color.Lerp(new Color(0.05f, 0.05f, 0f), Color.Lerp(palette.texture.GetPixel(6, 4), fog, 0.5f), 0.25f);

        sLeaser.sprites[LeafBody0].color = black;
        sLeaser.sprites[LeafBody1].color = black;
        sLeaser.sprites[LeafPlate0].color = armor;
        sLeaser.sprites[LeafPlate1].color = armor;
        for (int i = TrackStart; i < BlockStart; i++) sLeaser.sprites[i].color = metal;
        for (int i = BlockStart; i < HandStart; i++) sLeaser.sprites[i].color = armor;
        for (int i = HandStart; i < ClampStart; i++) sLeaser.sprites[i].color = edge;
        for (int i = ClampStart; i < PansarSegmentStart; i++) sLeaser.sprites[i].color = metal;
        for (int i = PansarSegmentStart; i < BigScrewStart; i++) sLeaser.sprites[i].color = armor;
        for (int i = BigScrewStart; i < JambStart; i++) sLeaser.sprites[i].color = edge;
        sLeaser.sprites[JambStart].color = black;
        sLeaser.sprites[JambStart + 1].color = black;
        sLeaser.sprites[GlyphBack].color = Color.Lerp(black, fog, 0.08f);
        sLeaser.sprites[GlyphGear].color = edge;
        sLeaser.sprites[GlyphGlow].color = Color.white;
    }

    internal static void AddToContainer(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer newContainer)
    {
        FContainer front = newContainer ?? rCam.ReturnFContainer("Items");
        FContainer back = rCam.ReturnFContainer("Midground");
        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            if (IsBackLayer(i)) back.AddChild(sLeaser.sprites[i]);
            else front.AddChild(sLeaser.sprites[i]);
        }
    }

    internal static void OnMechanicalFactorChanged(MultiGatePortRuntime port, float oldMechanical, float newMechanical)
    {
        if (port?.room == null || port.Placed == null || Mathf.Abs(newMechanical - oldMechanical) < 0.00001f) return;
        if (newMechanical > oldMechanical)
        {
            Cross(port, oldMechanical, newMechanical, 0.04f, SoundID.Gate_Secure_Rail_Up, 0.95f);
            Cross(port, oldMechanical, newMechanical, 0.22f, SoundID.Gate_Panser_Off, 1.00f);
            Cross(port, oldMechanical, newMechanical, 0.52f, SoundID.Gate_Pillows_Move_Out, 0.98f);
            Cross(port, oldMechanical, newMechanical, 0.92f, SoundID.Gate_Poles_Out, 1.00f);
        }
        else
        {
            CrossDown(port, oldMechanical, newMechanical, 0.92f, SoundID.Gate_Poles_And_Rails_In, 1.00f);
            CrossDown(port, oldMechanical, newMechanical, 0.52f, SoundID.Gate_Pillows_Move_In, 0.98f);
            CrossDown(port, oldMechanical, newMechanical, 0.22f, SoundID.Gate_Panser_On, 1.00f);
            if (oldMechanical > 0.04f && newMechanical <= 0.04f)
            {
                port.room.PlaySound(SoundID.Gate_Secure_Rail_Down, port.Placed.pos, 1f, 1f);
                port.room.ScreenMovement(port.Placed.pos, Vector2.zero, 0.35f);
            }
        }
    }

    private static void DrawLeaf(MultiGatePortRuntime port, FSprite body, FSprite plate, int side, float inner, float length, float thickness, float detailScale, float armorRelease, Vector2 camPos)
    {
        float sign = side < 0 ? -1f : 1f;
        float centerU = sign * (inner + length * 0.5f);
        Vector2 center = Local(port, centerU, 0f);
        body.x = center.x - camPos.x;
        body.y = center.y - camPos.y;
        body.rotation = Custom.VecToDeg(port.Data.Tangent);
        body.scaleX = length;
        body.scaleY = thickness;
        body.isVisible = length > 0.1f;

        float plateU = sign * (inner + Mathf.Min(length * 0.55f, Mathf.Max(12f, 30f * detailScale)));
        Vector2 platePos = Local(port, plateU, 0f);
        plate.x = platePos.x - camPos.x;
        plate.y = platePos.y - camPos.y;
        plate.rotation = GateAxisRotation(port) + (side < 0 ? 180f : 0f);
        plate.scale = detailScale * Mathf.Lerp(1f, 0.82f, armorRelease);
        plate.alpha = Mathf.Lerp(1f, 0.72f, armorRelease);
        plate.isVisible = length > 12f;
    }

    private static void DrawBackRails(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float thickness, float detailScale, float release, float axisRotation, Vector2 camPos)
    {
        float depth = thickness * 0.5f + 7f + release * 4f;
        for (int i = 0; i < 4; i++)
        {
            int face = (i & 1) == 0 ? -1 : 1;
            float u = i < 2 ? -4f * detailScale : 4f * detailScale;
            FSprite track = sLeaser.sprites[TrackStart + i];
            Place(port, track, u, face * depth, axisRotation, camPos);
            Size(track, Mathf.Max(6f, 8f * detailScale), port.Data.PassageWidth + 24f * detailScale);
            track.alpha = Mathf.Lerp(0.95f, 0.70f, release);
        }
        for (int i = 0; i < 2; i++)
        {
            FSprite center = sLeaser.sprites[CenterTrackStart + i];
            float face = i == 0 ? -1f : 1f;
            Place(port, center, 0f, face * (depth + 3f), axisRotation + (i == 0 ? 0f : 180f), camPos);
            Size(center, Mathf.Max(8f, 10f * detailScale), port.Data.PassageWidth * 0.96f);
            center.alpha = Mathf.Lerp(0.88f, 0.62f, release);
        }
    }

    private static void DrawEndpointHousings(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float half, float thickness, float detailScale, float open, float unlock, float gearMotion, float axisRotation, Vector2 camPos)
    {
        float housingU = half + Mathf.Max(12f, 18f * detailScale);
        float normalSpread = thickness * 0.5f + 11f * detailScale;
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            float side = sideIndex == 0 ? -1f : 1f;
            for (int faceIndex = 0; faceIndex < 2; faceIndex++)
            {
                float face = faceIndex == 0 ? -1f : 1f;
                int idx = sideIndex * 2 + faceIndex;
                FSprite block = sLeaser.sprites[BlockStart + idx];
                Place(port, block, side * housingU, face * normalSpread, axisRotation + side * face * 7f * (1f - open), camPos);
                block.scale = detailScale;
                block.alpha = 0.96f;
                for (int cog = 0; cog < 2; cog++)
                {
                    FSprite sprite = sLeaser.sprites[CogStart + idx * 2 + cog];
                    float cogU = side * (housingU + (cog == 0 ? -8f : 8f) * detailScale);
                    float cogV = face * (normalSpread + (cog == 0 ? 7f : 19f) * detailScale);
                    Place(port, sprite, cogU, cogV, axisRotation + side * face * gearMotion * (cog == 0 ? 230f : -150f), camPos);
                    sprite.scale = detailScale * (cog == 0 ? 0.88f : 0.68f);
                    sprite.alpha = cog == 0 ? 0.76f : 0.58f;
                }
            }
            FSprite screw = sLeaser.sprites[BigScrewStart + sideIndex];
            Place(port, screw, side * (half + 6f * detailScale), 0f, axisRotation + side * gearMotion * 720f, camPos);
            screw.scale = detailScale * 0.85f;
            screw.alpha = Mathf.Lerp(1f, 0.72f, unlock);
        }
    }

    private static void DrawActuators(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float half, float inner, float length, float thickness, float detailScale, float unclamp, float axisRotation, Vector2 camPos)
    {
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            float side = sideIndex == 0 ? -1f : 1f;
            for (int faceIndex = 0; faceIndex < 2; faceIndex++)
            {
                float face = faceIndex == 0 ? -1f : 1f;
                int idx = sideIndex * 2 + faceIndex;
                Vector2 anchor = Local(port, side * (half + 15f * detailScale), face * (thickness * 0.5f + 25f * detailScale));
                float targetU = side * (inner + Mathf.Min(length * 0.70f, 44f * detailScale));
                float targetV = face * (thickness * 0.5f + Mathf.Lerp(4f, 13f, unclamp) * detailScale);
                Vector2 target = Local(port, targetU, targetV);
                FSprite arm = sLeaser.sprites[ArmStart + idx];
                SetLine(arm, anchor, target, 3f * detailScale, camPos);
                arm.alpha = 0.78f;
                arm.isVisible = length > 4f;
                FSprite hand = sLeaser.sprites[HandStart + idx];
                hand.x = target.x - camPos.x;
                hand.y = target.y - camPos.y;
                hand.rotation = axisRotation + side * face * Mathf.Lerp(2f, 32f, unclamp);
                hand.scale = detailScale * 0.78f;
                hand.alpha = 0.90f;
                hand.isVisible = length > 4f;
            }
        }
    }

    private static void DrawClamps(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float inner, float length, float thickness, float detailScale, float release, float axisRotation, Vector2 camPos)
    {
        int clampsPerLeaf = Mathf.Clamp(Mathf.RoundToInt(port.Data.PassageWidth / 65f), 2, 6);
        for (int i = 0; i < 12; i++)
        {
            int sideIndex = i / 6;
            int localIndex = i % 6;
            FSprite clamp = sLeaser.sprites[ClampStart + i];
            if (localIndex >= clampsPerLeaf || length < 5f)
            {
                clamp.isVisible = false;
                continue;
            }
            float side = sideIndex == 0 ? -1f : 1f;
            float t = (localIndex + 0.65f) / (clampsPerLeaf + 0.3f);
            float u = side * (inner + length * t);
            float face = (localIndex & 1) == 0 ? -1f : 1f;
            float v = face * (thickness * 0.5f + Mathf.Lerp(1f, 18f, release) * detailScale);
            Place(port, clamp, u, v, axisRotation + side * face * Mathf.Lerp(0f, 48f, release), camPos);
            clamp.scale = detailScale * Mathf.Lerp(0.84f, 0.72f, release);
            clamp.alpha = Mathf.Lerp(1f, 0.38f, release);
            clamp.isVisible = release < 0.96f;
        }
    }

    private static void DrawBolts(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float inner, float length, float thickness, float detailScale, float unlock, float axisRotation, Vector2 camPos)
    {
        for (int i = 0; i < 4; i++)
        {
            int sideIndex = i / 2;
            int faceIndex = i % 2;
            float side = sideIndex == 0 ? -1f : 1f;
            float face = faceIndex == 0 ? -1f : 1f;
            float u = side * (inner + Mathf.Min(length * 0.20f + 4f, 18f * detailScale));
            float v = face * (thickness * 0.5f + Mathf.Lerp(1f, 12f, unlock) * detailScale);
            FSprite bolt = sLeaser.sprites[BoltStart + i];
            Place(port, bolt, u, v, axisRotation + side * 90f * unlock, camPos);
            bolt.scale = detailScale * 0.82f;
            bolt.alpha = 1f - unlock;
            bolt.isVisible = length > 4f && unlock < 0.98f;
        }
    }

    private static void DrawPansarSegments(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float inner, float length, float detailScale, float release, float axisRotation, Vector2 camPos)
    {
        int wanted = Mathf.Clamp(Mathf.RoundToInt(port.Data.PassageWidth / 45f), 3, 9);
        if ((wanted & 1) == 0) wanted = Mathf.Min(9, wanted + 1);
        int first = (9 - wanted) / 2;
        int last = first + wanted;
        for (int i = 0; i < 9; i++)
        {
            FSprite segment = sLeaser.sprites[PansarSegmentStart + i];
            if (i < first || i >= last || length < 5f)
            {
                segment.isVisible = false;
                continue;
            }
            float normalized = ((i - first) / (float)Mathf.Max(1, wanted - 1)) * 2f - 1f;
            float side = normalized < 0f ? -1f : 1f;
            if (Mathf.Abs(normalized) < 0.001f) side = (i & 1) == 0 ? -1f : 1f;
            float s = Mathf.Clamp01(Mathf.Abs(normalized));
            float u = side * (inner + length * s);
            float v = Mathf.Sin((i + 1) * 1.7f) * 1.5f * detailScale;
            float flip = ((i & 1) == 0 ? 1f : -1f) * side;
            Place(port, segment, u, v, axisRotation + flip * 90f * release, camPos);
            segment.scale = detailScale * Mathf.Lerp(0.92f, 0.76f, release);
            segment.alpha = Mathf.Lerp(0.96f, 0.50f, release);
            segment.isVisible = release < 0.98f;
        }
    }

    private static void DrawPoles(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float half, float thickness, float detailScale, float release, Vector2 camPos)
    {
        for (int i = 0; i < 4; i++)
        {
            int sideIndex = i / 2;
            int faceIndex = i % 2;
            float side = sideIndex == 0 ? -1f : 1f;
            float face = faceIndex == 0 ? -1f : 1f;
            float u = side * (half + Mathf.Lerp(7f, 18f, release) * detailScale);
            float v0 = face * (thickness * 0.5f + 4f * detailScale);
            float v1 = face * (thickness * 0.5f + Mathf.Lerp(34f, 16f, release) * detailScale);
            FSprite pole = sLeaser.sprites[PoleStart + i];
            SetLine(pole, Local(port, u, v0), Local(port, u, v1), 3f * detailScale, camPos);
            pole.alpha = Mathf.Lerp(0.78f, 0.35f, release);
        }
    }

    private static void DrawJambs(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float half, float thickness, Vector2 camPos)
    {
        float jambThickness = Mathf.Max(18f, thickness * 2.4f);
        float jambDepth = Mathf.Max(10f, thickness * 1.25f);
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            FSprite jamb = sLeaser.sprites[JambStart + i];
            Vector2 center = Local(port, side * (half + jambThickness * 0.22f), 0f);
            jamb.x = center.x - camPos.x;
            jamb.y = center.y - camPos.y;
            jamb.rotation = Custom.VecToDeg(port.Data.Tangent);
            jamb.scaleX = jambThickness;
            jamb.scaleY = jambDepth;
        }
    }

    private static void DrawGlyphAssembly(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, float detailScale, float gearMotion, float timeStacker, Vector2 camPos)
    {
        Vector2 gp = port.Placed.pos + port.Data.GlyphWorldOffset;
        float clock = (port.room?.game?.clock ?? 0) + timeStacker;
        float pulse = 0.5f + 0.5f * Mathf.Sin(clock / (port.Denied ? 7f : 14f));
        float housingScale = Mathf.Clamp(detailScale, 0.78f, 1.35f);

        FSprite back = sLeaser.sprites[GlyphBack];
        back.x = gp.x - camPos.x;
        back.y = gp.y - camPos.y;
        back.rotation = GateAxisRotation(port);
        back.scaleX = 36f * housingScale;
        back.scaleY = 36f * housingScale;
        back.alpha = 0.88f;

        FSprite gear = sLeaser.sprites[GlyphGear];
        gear.x = gp.x - camPos.x;
        gear.y = gp.y - camPos.y;
        gear.rotation = gearMotion * 180f + clock * 0.12f;
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

    private static bool IsBackLayer(int index)
    {
        // Gameplay-facing silhouette contains only geometry that is either actually
        // collidable (leaf bodies) or explicitly authored as a Tile-supported frame /
        // non-contact indicator. All decorative armor, clamps, hands, bolts, rails and
        // cogs live in Midground so they never imply a collision surface that does not
        // exist in OrientedGateCollision.
        return index != LeafBody0 && index != LeafBody1 &&
               index != JambStart && index != JambStart + 1 &&
               index != GlyphBack && index != GlyphGear &&
               index != GlyphGlow && index != Glyph;
    }

    private static void CleanupIfNeeded(MultiGatePortRuntime port, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        if (port.slatedForDeletetion || port.room != rCam.room) sLeaser.CleanSpritesAndRemove();
    }

    private static Vector2 Local(MultiGatePortRuntime port, float tangent, float normal) =>
        port.Placed.pos + port.Data.Tangent * tangent + port.Data.Normal * normal;

    private static float GateAxisRotation(MultiGatePortRuntime port) => Custom.VecToDeg(port.Data.Tangent) - 90f;

    private static void Place(MultiGatePortRuntime port, FSprite sprite, float u, float v, float rotation, Vector2 camPos)
    {
        Vector2 p = Local(port, u, v);
        sprite.x = p.x - camPos.x;
        sprite.y = p.y - camPos.y;
        sprite.rotation = rotation;
        sprite.isVisible = true;
    }

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

    private static void Size(FSprite sprite, float width, float height)
    {
        float sourceWidth = Mathf.Max(1f, sprite.element.sourceSize.x);
        float sourceHeight = Mathf.Max(1f, sprite.element.sourceSize.y);
        sprite.scaleX = width / sourceWidth;
        sprite.scaleY = height / sourceHeight;
    }

    private static float Smooth01(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    private static FSprite Pixel() => new("pixel") { anchorX = 0.5f, anchorY = 0.5f };

    private static FSprite Atlas(string name)
    {
        try
        {
            return new FSprite(name) { anchorX = 0.5f, anchorY = 0.5f };
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
            if (index == Glyph || index == LeafBody0 || index == LeafBody1 ||
                index == JambStart || index == JambStart + 1 || index == GlyphBack || index == GlyphGlow) return;
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
