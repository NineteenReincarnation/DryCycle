using System;
using DryCycle.Items.ScavengerLance;
using MoreSlugcats;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceScavengerGraphics : ScavengerGraphics
{
    private const int EquipmentSprites = 25;
    private readonly LanceScavenger _owner;
    private readonly VultureMaskGraphics _mask;
    private readonly int _start;
    private readonly int _shoulderSide;
    private readonly LanceAdornment[] _cords = { new(3, 5f), new(2, 6f), new(2, 5f) };
    private readonly LanceAdornment[] _ribbons = { new(5, 4.2f), new(4, 4f) };
    private bool _maskAvailable;

    internal LanceScavengerGraphics(LanceScavenger owner) : base(owner)
    {
        _owner = owner;
        _start = TotalSprites;
        _shoulderSide = (owner.abstractCreature.ID.RandomSeed & 1) == 0 ? -1 : 1;
        _mask = new VultureMaskGraphics(owner, VultureMask.MaskType.NORMAL, _start + EquipmentSprites, LanceScavengerAssets.Prefix)
        {
            ColorA = new HSLColor(0.115f, 0.42f, 0.62f), ColorB = new HSLColor(0.105f, 0.43f, 0.43f),
            rotationA = Vector2.up, lastRotationA = Vector2.up,
            rotationB = Vector2.up, lastRotationB = Vector2.up
        };
        // Preserve normal procedural branching while making it subordinate to the mask horn.
        foreach (Eartlers.Vertex[] branch in eartlers.points)
            for (int i = 0; i < branch.Length; i++)
            { Eartlers.Vertex vertex = branch[i]; vertex.pos *= 0.58f; vertex.rad *= 0.78f; branch[i] = vertex; }
    }

    public override void Reset()
    {
        base.Reset();
        if (_cords == null) return;
        foreach (LanceAdornment cord in _cords) cord.Reset(_owner.mainBodyChunk.pos);
        foreach (LanceAdornment ribbon in _ribbons) ribbon.Reset(_owner.mainBodyChunk.pos);
    }

    public override void Update()
    {
        base.Update();
        if (_owner.room == null) return;
        Vector2 head = DrawPosition(headDrawPos, 1f);
        Vector2 chest = DrawPosition(chestDrawPos, 1f);
        Vector2 hips = DrawPosition(hipsDrawPos, 1f);
        Vector2 axis = (head - hips).normalized;
        Vector2 across = Custom.PerpendicularVector(axis);
        Vector2 shoulder = chest + across * (_shoulderSide * 8f) + axis * 3f;
        _cords[0].Update(head - across * 7f - axis * 3f, _owner.mainBodyChunk.vel, _owner.gravity);
        _cords[1].Update(head + across * 7f - axis * 2f, _owner.mainBodyChunk.vel, _owner.gravity);
        _cords[2].Update(shoulder - axis * 4f, _owner.mainBodyChunk.vel, _owner.gravity);
        _ribbons[0].Update(chest - axis * 4f + across * _shoulderSide * 6f, _owner.mainBodyChunk.vel, _owner.gravity);
        _ribbons[1].Update(hips + across * _shoulderSide * 5f, _owner.mainBodyChunk.vel, _owner.gravity);

        ScavengerLance lance = _owner.Lance;
        if (lance == null || !_owner.Consious) return;
        bool twoHands = _owner.Combat.State == LanceState.Brace || _owner.Combat.State == LanceState.Charge ||
            _owner.Combat.State == LanceState.CloseDefense || _owner.Combat.State == LanceState.Threaten;
        if (_owner.movMode == Scavenger.MovementMode.Climb) return;
        for (int i = 0; i < (twoHands ? 2 : 1); i++)
        {
            Vector2 desired = lance.firstChunk.pos + lance.rotation * (i == 0 ? 10f : -9f);
            hands[i].absoluteHuntPos = desired;
            hands[i].pos = Vector2.Lerp(hands[i].pos, desired, twoHands ? 0.8f : 0.55f);
            hands[i].vel *= 0.35f;
            hands[i].spearPosAdd = new Unity.Mathematics.float2(0f, 0f);
        }
    }

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        base.InitiateSprites(sLeaser, rCam);
        Array.Resize(ref sLeaser.sprites, _start + EquipmentSprites + _mask.TotalSprites);
        sLeaser.sprites[_start] = TriangleMesh.MakeLongMesh(6, false, false);
        sLeaser.sprites[_start + 1] = TriangleMesh.MakeLongMesh(6, false, false);
        sLeaser.sprites[_start + 2] = new TriangleMesh("Futile_White", new[] {
            new TriangleMesh.Triangle(0,1,2), new TriangleMesh.Triangle(0,2,3), new TriangleMesh.Triangle(0,3,4) }, false);
        sLeaser.sprites[_start + 3] = new FSprite("pixel");
        for (int i = 4; i <= 8; i++) sLeaser.sprites[_start + i] = new FSprite("pixel");
        sLeaser.sprites[_start + 9] = TriangleMesh.MakeLongMesh(4, false, false);
        sLeaser.sprites[_start + 10] = TriangleMesh.MakeLongMesh(3, false, false);
        sLeaser.sprites[_start + 11] = new FSprite("Circle20");
        sLeaser.sprites[_start + 12] = new FSprite("Circle20");
        for (int i = 13; i < EquipmentSprites; i++)
            sLeaser.sprites[_start + i] = new FSprite((i - 13) % 3 == 1 ? "Circle20" : "pixel");
        _mask.InitiateSprites(sLeaser, rCam);
        for (int i = 0; i < _mask.BaseTotalSprites; i++)
            sLeaser.sprites[_mask.firstSprite + i].shader = rCam.game.rainWorld.Shaders["Basic"];
        _maskAvailable = LanceScavengerAssets.EnsureLoaded();
        _mask.overrideSprite = LanceScavengerAssets.ActivePrefix;
        if (_mask.overrideSprite == LanceScavengerAssets.ColorPrefix)
        {
            // Preserve the atlas's ivory, gold and dark details. Vanilla mask
            // graphics still apply room darkness and the two shadow layers.
            _mask.ColorA = _mask.ColorB = new HSLColor(0f, 0f, 1f);
        }
        ApplyPalette(sLeaser, rCam, rCam.currentPalette);
        AddToContainer(sLeaser, rCam, null);
    }

    public override void AddToContainer(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer container)
    {
        base.AddToContainer(sLeaser, rCam, container);
        if (sLeaser.sprites.Length <= _start) return;
        container ??= rCam.ReturnFContainer("Midground");
        for (int i = _start; i < _start + EquipmentSprites; i++)
        {
            sLeaser.sprites[i].RemoveFromContainer();
            container.AddChild(sLeaser.sprites[i]);
        }
        sLeaser.sprites[_start + 9].MoveBehindOtherNode(sLeaser.sprites[ChestSprite]);
        sLeaser.sprites[_start + 10].MoveBehindOtherNode(sLeaser.sprites[HipSprite]);
        for (int i = 0; i < 5; i++) sLeaser.sprites[_start + i].MoveBehindOtherNode(sLeaser.sprites[FirstInFrontLimbSprite + 2]);
        _mask.AddToContainer(sLeaser, rCam, container);
    }

    public override void DrawSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float t, Vector2 cam)
    {
        base.DrawSprites(sLeaser, rCam, t, cam);
        if (_owner.room != rCam.room || _owner.slatedForDeletetion) { sLeaser.CleanSpritesAndRemove(); return; }
        Vector2 head = DrawPosition(headDrawPos, t), chest = DrawPosition(chestDrawPos, t), hips = DrawPosition(hipsDrawPos, t);
        Vector2 axis = (head - hips).normalized, across = Custom.PerpendicularVector(axis);
        Vector2 shoulder = chest + across * (_shoulderSide * 8f) + axis * 3f;
        Vector2 waist = Vector2.Lerp(chest, hips, 0.7f) - across * (_shoulderSide * 6f);
        DrawSash((TriangleMesh)sLeaser.sprites[_start], shoulder, waist, across, cam, 4.3f);
        DrawSash((TriangleMesh)sLeaser.sprites[_start + 1], shoulder, waist, across, cam, 3.2f);
        TriangleMesh plate = (TriangleMesh)sLeaser.sprites[_start + 2];
        Vector2[] rim = { new(-7f, 1f), new(-4f, 5f), new(4f, 4f), new(8f, -2f), new(2f, -5f) };
        for (int i = 0; i < rim.Length; i++) plate.MoveVertice(i, shoulder + across * rim[i].x + axis * rim[i].y - cam);
        Line(sLeaser.sprites[_start + 3], shoulder - across * 6f - axis * 2f, shoulder + across * 5f + axis * 2f, cam, 1.4f);
        Line(sLeaser.sprites[_start + 4], waist - across * 3f, waist + across * 3f, cam, 3.3f);
        for (int i = 0; i < 4; i++)
        {
            Vector2 end = i < 2 ? Vector2.Lerp(hands[i].lastPos, hands[i].pos, t) : Vector2.Lerp(legs[i - 2].lastPos, legs[i - 2].pos, t);
            Vector2 anchor = Vector2.Lerp(i < 2 ? chest : hips, end, 0.83f);
            Vector2 perpendicular = Custom.PerpendicularVector((end - chest).normalized);
            Line(sLeaser.sprites[_start + 5 + i], anchor - perpendicular * 2.4f, anchor + perpendicular * 2.4f, cam, 2f);
        }
        _ribbons[0].DrawRibbon((TriangleMesh)sLeaser.sprites[_start + 9], t, cam, 2.3f);
        _ribbons[1].DrawRibbon((TriangleMesh)sLeaser.sprites[_start + 10], t, cam, 1.7f);
        sLeaser.sprites[_start + 11].SetPosition(shoulder - axis * 4f - cam);
        sLeaser.sprites[_start + 11].scale = 0.22f;
        sLeaser.sprites[_start + 12].SetPosition(shoulder - axis * 4f - cam);
        sLeaser.sprites[_start + 12].scale = 0.13f;
        int index = _start + 13;
        foreach (LanceAdornment cord in _cords)
            for (int i = 1; i < cord.Positions.Length; i++)
            {
                Vector2 position = cord.At(i, t);
                Line(sLeaser.sprites[index++], cord.At(i - 1, t), position, cam, 0.7f);
                FSprite pearl = sLeaser.sprites[index++];
                pearl.SetPosition(position - cam); pearl.scale = i == 1 ? 0.2f : 0.15f;
                FSprite shine = sLeaser.sprites[index++];
                shine.SetPosition(position - cam + new Vector2(-0.6f, 0.6f)); shine.scale = 0.65f;
            }
        ApplyEquipmentPalette(sLeaser, rCam.currentPalette, _owner.room.Darkness(chest) * (1f - _owner.room.LightSourceExposure(chest)));
        if (_maskAvailable)
        {
            // These atlas frames are authored upright and facing right. Feeding the
            // negated Kraken-mask face vector both mirrors and rolls them backwards.
            // Separate head roll from local gaze; native graphics still choose all
            // nine frames, mirror, anchor and shade the three layers.
            Vector2 headUp = (head - chest).normalized;
            if (headUp.sqrMagnitude < 0.01f) headUp = Vector2.up;
            Vector2 headRight = new(headUp.y, -headUp.x);
            Vector2 gaze = Vector2.Lerp(ToVector(lastLookPoint), ToVector(lookPoint), t) - head;
            Vector2 localGaze = new(Vector2.Dot(gaze, headRight), Vector2.Dot(gaze, headUp));
            float neutral = Mathf.Lerp(lastNeutralFace, neutralFace, t);
            Vector2 facing = Vector2.Lerp(localGaze.normalized, Vector2.up, neutral).normalized;
            _mask.overrideRotationVector = headUp;
            _mask.overrideAnchorVector = facing.sqrMagnitude > 0.01f ? facing : Vector2.up;
            _mask.overrideDrawVector = head + headUp;
            _mask.ApplyPalette(sLeaser, rCam, rCam.currentPalette);
            _mask.DrawSprites(sLeaser, rCam, t, cam);
        }
        else _mask.SetVisible(sLeaser, false);
    }

    private Vector2 DrawPosition(int index, float t) => Vector2.Lerp(ToVector(drawPositions[index, 1]), ToVector(drawPositions[index, 0]), t);
    private static Vector2 ToVector(Unity.Mathematics.float2 value) => new(value.x, value.y);
    internal static void DrawSash(TriangleMesh mesh, Vector2 start, Vector2 end, Vector2 across, Vector2 cam, float width)
    {
        Vector2 perp = Custom.PerpendicularVector((end - start).normalized);
        for (int i = 0; i < 6; i++)
        {
            float a = i / 6f, b = (i + 1) / 6f;
            Vector2 p = Vector2.Lerp(start, end, a) + Vector2.down * Mathf.Sin(a * Mathf.PI) * 2f;
            Vector2 q = Vector2.Lerp(start, end, b) + Vector2.down * Mathf.Sin(b * Mathf.PI) * 2f;
            float wa = width * (0.9f + (i % 2) * 0.14f);
            mesh.MoveVertice(i * 4, p - perp * wa - cam);
            mesh.MoveVertice(i * 4 + 1, p + perp * width - cam);
            mesh.MoveVertice(i * 4 + 2, q - perp * width - cam);
            mesh.MoveVertice(i * 4 + 3, q + perp * wa - cam);
        }
    }
    private static void Line(FSprite sprite, Vector2 a, Vector2 b, Vector2 cam, float width)
    { sprite.SetPosition((a + b) * 0.5f - cam); sprite.rotation = Custom.AimFromOneVectorToAnother(a, b); sprite.scaleX = width; sprite.scaleY = Vector2.Distance(a, b); }

    public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
    {
        base.ApplyPalette(sLeaser, rCam, palette);
        if (sLeaser.sprites.Length <= _start) return;
        ApplyEquipmentPalette(sLeaser, palette, 0f);
        _mask.ApplyPalette(sLeaser, rCam, palette);
    }
    private void ApplyEquipmentPalette(RoomCamera.SpriteLeaser sLeaser, RoomPalette palette, float darkness)
    {
        Color ochre = Color.Lerp(new Color(0.59f, 0.43f, 0.18f), palette.blackColor, darkness);
        Color leather = Color.Lerp(new Color(0.24f, 0.19f, 0.12f), palette.blackColor, darkness);
        Color bone = Color.Lerp(new Color(0.7f, 0.67f, 0.54f), palette.blackColor, darkness);
        for (int i = 0; i < EquipmentSprites; i++) sLeaser.sprites[_start + i].color = ochre;
        sLeaser.sprites[_start].color = leather;
        sLeaser.sprites[_start + 2].color = bone;
        sLeaser.sprites[_start + 3].color = sLeaser.sprites[_start + 4].color = leather;
        sLeaser.sprites[_start + 11].color = bone;
        sLeaser.sprites[_start + 12].color = leather;
        for (int i = 13; i < EquipmentSprites; i++)
            sLeaser.sprites[_start + i].color = (i - 13) % 3 == 0 ? leather : bone;
    }
}
