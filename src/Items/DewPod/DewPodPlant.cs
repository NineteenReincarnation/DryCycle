using System;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.DewPod;

internal sealed class DewPodPlant : UpdatableAndDeletable, IDrawable
{
    internal const int SlotCount = 4;

    private const int StemSegments = 3;
    private const int RootSpriteCount = 3;
    private const int SpritesPerSlot = 6;

    private static readonly Vector2[] StemRootOffsets =
    {
        new(-7.5f, 2.5f),
        new(-2.5f, 3.5f),
        new(3.5f, 3f),
        new(8f, 2f)
    };

    private static readonly Vector2[] RestTipOffsets =
    {
        new(-15f, 20f),
        new(-6f, 29f),
        new(7f, 27f),
        new(16f, 19f)
    };

    private static readonly float[] SlotScales = { 0.94f, 1.04f, 1f, 0.92f };
    private static readonly float[] PhaseOffsets = { 0.2f, 1.7f, 3.1f, 4.6f };

    private readonly PlacedObject _placedObject;
    private readonly PlacedObject.ConsumableObjectData _consumableData;
    private readonly DewPodPlantHooks.PlantRuntimeState _runtimeState;
    private readonly Vector2[] _tipPos = new Vector2[SlotCount];
    private readonly Vector2[] _lastTipPos = new Vector2[SlotCount];
    private readonly Vector2[] _tipVel = new Vector2[SlotCount];

    private int _age;
    private int _pullSlot = -1;
    private Vector2 _pullTarget;
    private float _pullStrength;
    private Color _liquidColor = new(0.50f, 0.92f, 0.78f);
    private bool _hasLiquidColor;

    internal int OriginRoom { get; }
    internal int PlacedObjectIndex { get; }
    internal Vector2 RootPos { get; private set; }

    internal DewPodPlant(
        Room room,
        PlacedObject placedObject,
        int originRoom,
        int placedObjectIndex,
        DewPodPlantHooks.PlantRuntimeState runtimeState)
    {
        this.room = room;
        _placedObject = placedObject;
        _consumableData = placedObject?.data as PlacedObject.ConsumableObjectData;
        _runtimeState = runtimeState;
        OriginRoom = originRoom;
        PlacedObjectIndex = placedObjectIndex;
        RootPos = ResolveRootPosition(room, placedObject?.pos ?? Vector2.zero);

        for (int i = 0; i < SlotCount; i++)
        {
            Vector2 rest = GetRestTip(i, 0f);
            _tipPos[i] = rest;
            _lastTipPos[i] = rest;
            _tipVel[i] = Vector2.zero;
        }

        TryRefreshLiquidColor();
    }

    internal bool IsMatureSlot(int slot)
    {
        return slot >= 0 &&
               slot < SlotCount &&
               !_runtimeState.Dormant &&
               (_runtimeState.InitialMask & (1 << slot)) != 0 &&
               (_runtimeState.HarvestedMask & (1 << slot)) == 0;
    }

    internal bool IsHarvestedSlot(int slot)
    {
        return slot >= 0 &&
               slot < SlotCount &&
               ((_runtimeState.InitialMask & (1 << slot)) != 0 || _runtimeState.Dormant) &&
               (_runtimeState.HarvestedMask & (1 << slot)) != 0;
    }

    internal Vector2 GetPodPosition(int slot)
    {
        if (slot < 0 || slot >= SlotCount)
        {
            return RootPos;
        }

        return _tipPos[slot];
    }

    internal void SetPullInfluence(int slot, Vector2 target, float strength)
    {
        if (!IsMatureSlot(slot))
        {
            return;
        }

        strength = Mathf.Clamp01(strength);
        if (_pullSlot != slot || strength >= _pullStrength)
        {
            _pullSlot = slot;
            _pullTarget = target;
            _pullStrength = strength;
        }
    }

    internal bool TryHarvest(Player player, int slot, int freeHand)
    {
        if (player?.room != room ||
            room?.abstractRoom == null ||
            room.world == null ||
            room.game == null ||
            freeHand < 0 ||
            !IsMatureSlot(slot))
        {
            return false;
        }

        Vector2 spawnPos = _tipPos[slot];
        AbstractDewPod abstractPod = new(
            room.world,
            room.GetWorldCoordinate(spawnPos),
            room.game.GetNewID(),
            OriginRoom,
            PlacedObjectIndex,
            _consumableData,
            AbstractDewPod.MaxWaterWV,
            broken: false)
        {
            isConsumed = _runtimeState.ConsumptionReported,
            LiquidColor = LiquidColor,
            HasLiquidColor = true
        };

        room.abstractRoom.AddEntity(abstractPod);
        abstractPod.placedObjectOrigin = room.SetAbstractRoomAndPlacedObjectNumber(
            room.abstractRoom.name,
            PlacedObjectIndex);
        abstractPod.RealizeInRoom();

        if (abstractPod.realizedObject is not DewPod pod)
        {
            room.abstractRoom.RemoveEntity(abstractPod);
            return false;
        }

        pod.firstChunk.HardSetPosition(spawnPos);
        pod.firstChunk.lastPos = spawnPos;
        pod.firstChunk.vel = _tipVel[slot] + Custom.RNV() * 0.35f;

        _runtimeState.HarvestedMask |= 1 << slot;

        if (!_runtimeState.ConsumptionReported)
        {
            abstractPod.isConsumed = false;
            abstractPod.Consume();
            _runtimeState.ConsumptionReported = true;
        }
        else
        {
            abstractPod.isConsumed = true;
        }

        player.SlugcatGrab(pod, freeHand);
        room.PlaySound(SoundID.Seed_Cob_Pick, pod.firstChunk);

        Vector2 recoil = Custom.DirVec(spawnPos, player.mainBodyChunk.pos);
        player.mainBodyChunk.vel += recoil * 0.25f;
        _tipVel[slot] -= recoil * 2.4f;

        for (int i = 0; i < SlotCount; i++)
        {
            if (i != slot)
            {
                _tipVel[i] += Custom.DirVec(spawnPos, _tipPos[i]) * 0.45f;
            }
        }

        return true;
    }

    private Color LiquidColor => _hasLiquidColor
        ? _liquidColor
        : new Color(0.50f, 0.92f, 0.78f);

    public override void Update(bool eu)
    {
        base.Update(eu);

        if (room == null)
        {
            return;
        }

        _age++;
        TryRefreshLiquidColor();

        for (int i = 0; i < SlotCount; i++)
        {
            _lastTipPos[i] = _tipPos[i];

            Vector2 rest = GetRestTip(i, _age);
            Vector2 stemRoot = RootPos + StemRootOffsets[i];

            _tipVel[i] += (rest - _tipPos[i]) * 0.075f;
            _tipVel[i] *= 0.80f;
            _tipVel[i].y -= 0.012f;

            ApplyNearbyPlayerPush(i);

            if (_pullSlot == i && _pullStrength > 0f && IsMatureSlot(i))
            {
                float pull = Mathf.Lerp(0.09f, 0.24f, _pullStrength);
                _tipVel[i] += (_pullTarget - _tipPos[i]) * pull;
            }

            _tipPos[i] += _tipVel[i];

            float restLength = Vector2.Distance(stemRoot, rest);
            float maxLength = restLength * 1.20f;
            float distance = Vector2.Distance(stemRoot, _tipPos[i]);
            if (distance > maxLength && distance > 0.001f)
            {
                Vector2 corrected = stemRoot + Custom.DirVec(stemRoot, _tipPos[i]) * maxLength;
                _tipPos[i] = Vector2.Lerp(_tipPos[i], corrected, 0.72f);
                _tipVel[i] *= 0.68f;
            }
        }

        _pullSlot = -1;
        _pullStrength = 0f;
    }

    private void ApplyNearbyPlayerPush(int slot)
    {
        if (room?.game?.Players == null)
        {
            return;
        }

        foreach (AbstractCreature abstractPlayer in room.game.Players)
        {
            if (abstractPlayer?.realizedCreature is not Player player ||
                player.room != room ||
                player.bodyChunks == null)
            {
                continue;
            }

            for (int j = 0; j < player.bodyChunks.Length; j++)
            {
                BodyChunk chunk = player.bodyChunks[j];
                if (chunk == null)
                {
                    continue;
                }

                Vector2 away = _tipPos[slot] - chunk.pos;
                float distance = away.magnitude;
                float radius = chunk.rad + 10f;
                if (distance <= 0.001f || distance >= radius)
                {
                    continue;
                }

                away /= distance;
                float strength = Mathf.InverseLerp(radius, 0f, distance);
                _tipVel[slot] += away * (0.35f + 1.4f * strength);
            }
        }
    }

    private Vector2 GetRestTip(int slot, float age)
    {
        float sway = Mathf.Sin(age * 0.028f + PhaseOffsets[slot]) * (0.75f + slot * 0.12f);
        float bob = Mathf.Sin(age * 0.019f + PhaseOffsets[slot] * 1.3f) * 0.35f;
        return RootPos + RestTipOffsets[slot] + new Vector2(sway, bob);
    }

    private static Vector2 ResolveRootPosition(Room sourceRoom, Vector2 placedPos)
    {
        if (sourceRoom == null)
        {
            return placedPos;
        }

        IntVector2 tile = sourceRoom.GetTilePosition(placedPos);
        int minY = Math.Max(0, tile.y - 6);

        for (int y = tile.y; y >= minY; y--)
        {
            if (!sourceRoom.GetTile(tile.x, y).Solid)
            {
                continue;
            }

            return sourceRoom.MiddleOfTile(tile.x, y) + new Vector2(0f, 10f);
        }

        return placedPos;
    }

    private void TryRefreshLiquidColor()
    {
        if (_hasLiquidColor || room == null)
        {
            return;
        }

        if (DewPod.TryGetLocalWaterColor(room, out Color color))
        {
            _liquidColor = color;
            _liquidColor.a = 1f;
            _hasLiquidColor = true;
        }
    }

    private int StemSprite(int slot) => RootSpriteCount + slot * SpritesPerSlot;
    private int ShellSprite(int slot) => StemSprite(slot) + 1;
    private int LiquidSprite(int slot) => StemSprite(slot) + 2;
    private int WindowSprite(int slot) => StemSprite(slot) + 3;
    private int HighlightSprite(int slot) => StemSprite(slot) + 4;
    private int BudSprite(int slot) => StemSprite(slot) + 5;

    public void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        sLeaser.sprites = new FSprite[RootSpriteCount + SlotCount * SpritesPerSlot];

        for (int i = 0; i < RootSpriteCount; i++)
        {
            sLeaser.sprites[i] = new FSprite("Circle20");
        }

        for (int i = 0; i < SlotCount; i++)
        {
            sLeaser.sprites[StemSprite(i)] = TriangleMesh.MakeLongMesh(
                StemSegments,
                pointyTip: false,
                customColor: false);
            sLeaser.sprites[ShellSprite(i)] = new FSprite("Circle20");
            sLeaser.sprites[LiquidSprite(i)] = new FSprite("Circle20");
            sLeaser.sprites[WindowSprite(i)] = new FSprite("Circle20");
            sLeaser.sprites[HighlightSprite(i)] = new FSprite("Circle20");
            sLeaser.sprites[BudSprite(i)] = new FSprite("Circle20");
        }

        AddToContainer(sLeaser, rCam, null);
    }

    public void DrawSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        Vector2 root = RootPos - camPos;

        FSprite rootBack = sLeaser.sprites[0];
        rootBack.x = root.x - 5f;
        rootBack.y = root.y + 1f;
        rootBack.scaleX = 0.74f;
        rootBack.scaleY = 0.34f;
        rootBack.rotation = -14f;

        FSprite rootCenter = sLeaser.sprites[1];
        rootCenter.x = root.x;
        rootCenter.y = root.y + 2.2f;
        rootCenter.scaleX = 0.92f;
        rootCenter.scaleY = 0.39f;

        FSprite rootFront = sLeaser.sprites[2];
        rootFront.x = root.x + 6f;
        rootFront.y = root.y + 1.1f;
        rootFront.scaleX = 0.68f;
        rootFront.scaleY = 0.31f;
        rootFront.rotation = 12f;

        Color displayedLiquid = Color.Lerp(
            rCam.currentPalette.blackColor,
            LiquidColor,
            0.88f);

        for (int i = 0; i < SlotCount; i++)
        {
            Vector2 tip = Vector2.Lerp(_lastTipPos[i], _tipPos[i], timeStacker);
            Vector2 stemRoot = RootPos + StemRootOffsets[i];
            bool mature = IsMatureSlot(i);
            bool harvested = IsHarvestedSlot(i);
            bool immature = !mature && !harvested;

            Vector2 stemEnd = mature
                ? tip
                : Vector2.Lerp(stemRoot, tip, harvested ? 0.58f : 0.70f);

            DrawStem(
                sLeaser.sprites[StemSprite(i)] as TriangleMesh,
                stemRoot,
                stemEnd,
                i,
                camPos);

            FSprite shell = sLeaser.sprites[ShellSprite(i)];
            FSprite liquid = sLeaser.sprites[LiquidSprite(i)];
            FSprite window = sLeaser.sprites[WindowSprite(i)];
            FSprite highlight = sLeaser.sprites[HighlightSprite(i)];
            FSprite bud = sLeaser.sprites[BudSprite(i)];

            shell.isVisible = mature;
            liquid.isVisible = mature;
            window.isVisible = mature;
            highlight.isVisible = mature;
            bud.isVisible = !mature;

            if (mature)
            {
                Vector2 direction = Custom.DirVec(stemRoot, tip);
                if (direction.sqrMagnitude < 0.001f)
                {
                    direction = Vector2.up;
                }

                Vector2 perpendicular = Custom.PerpendicularVector(direction);
                float rotation = Custom.VecToDeg(direction) - 90f;
                float scale = SlotScales[i];
                Vector2 podPos = tip - camPos;

                shell.x = podPos.x;
                shell.y = podPos.y;
                shell.scaleX = 0.79f * scale;
                shell.scaleY = 1.18f * scale;
                shell.rotation = rotation;

                liquid.x = podPos.x;
                liquid.y = podPos.y;
                liquid.scaleX = shell.scaleX * 0.76f;
                liquid.scaleY = shell.scaleY * 0.86f;
                liquid.rotation = rotation;
                liquid.color = displayedLiquid;

                Vector2 windowPos = podPos + direction * (5.35f * scale);
                window.x = windowPos.x;
                window.y = windowPos.y;
                window.scaleX = shell.scaleX * 0.44f;
                window.scaleY = shell.scaleY * 0.23f;
                window.rotation = rotation;
                window.alpha = 0.70f;
                window.color = Color.Lerp(displayedLiquid, Color.white, 0.48f);

                Vector2 highlightPos = podPos - perpendicular * (2.2f * scale) + direction * (1.8f * scale);
                highlight.x = highlightPos.x;
                highlight.y = highlightPos.y;
                highlight.scaleX = shell.scaleX * 0.10f;
                highlight.scaleY = shell.scaleY * 0.35f;
                highlight.rotation = rotation;
                highlight.alpha = 0.28f;
            }
            else
            {
                Vector2 budPos = stemEnd - camPos;
                bud.x = budPos.x;
                bud.y = budPos.y;
                bud.scaleX = immature ? 0.27f : 0.18f;
                bud.scaleY = immature ? 0.42f : 0.16f;
                bud.rotation = Custom.VecToDeg(Custom.DirVec(stemRoot, stemEnd)) - 90f;
                bud.alpha = harvested ? 0.74f : 1f;
            }
        }

        if (slatedForDeletetion || room != rCam.room)
        {
            sLeaser.CleanSpritesAndRemove();
        }
    }

    private void DrawStem(
        TriangleMesh mesh,
        Vector2 start,
        Vector2 end,
        int slot,
        Vector2 camPos)
    {
        if (mesh == null)
        {
            return;
        }

        Vector2 overall = end - start;
        if (overall.sqrMagnitude < 0.001f)
        {
            overall = Vector2.up;
        }

        Vector2 perpendicular = Custom.PerpendicularVector(overall.normalized);
        float bend = (slot - 1.5f) * 1.8f;
        Vector2 controlA = start + overall * 0.32f + perpendicular * bend;
        Vector2 controlB = end - overall * 0.24f - perpendicular * bend * 0.45f;

        for (int segment = 0; segment < StemSegments; segment++)
        {
            float t0 = segment / (float)StemSegments;
            float t1 = (segment + 1f) / StemSegments;
            Vector2 p0 = Custom.Bezier(start, controlA, controlB, end, t0);
            Vector2 p1 = Custom.Bezier(start, controlA, controlB, end, t1);
            Vector2 direction = Custom.DirVec(p0, p1);
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector2.up;
            }

            Vector2 normal = Custom.PerpendicularVector(direction);
            float width0 = Mathf.Lerp(1.65f, 0.82f, t0);
            float width1 = Mathf.Lerp(1.65f, 0.82f, t1);
            float cap = Vector2.Distance(p0, p1) / 5f;

            mesh.MoveVertice(segment * 4, p0 - direction * cap - normal * width0 - camPos);
            mesh.MoveVertice(segment * 4 + 1, p0 - direction * cap + normal * width0 - camPos);
            mesh.MoveVertice(segment * 4 + 2, p1 + direction * cap - normal * width1 - camPos);
            mesh.MoveVertice(segment * 4 + 3, p1 + direction * cap + normal * width1 - camPos);
        }
    }

    public void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
    {
        Color rootColor = Color.Lerp(
            palette.blackColor,
            new Color(0.14f, 0.34f, 0.22f),
            0.78f);
        Color stemColor = Color.Lerp(
            palette.blackColor,
            new Color(0.20f, 0.46f, 0.30f),
            0.82f);
        Color shellColor = Color.Lerp(
            palette.blackColor,
            new Color(0.18f, 0.48f, 0.36f),
            0.78f);
        Color budColor = Color.Lerp(
            palette.blackColor,
            new Color(0.23f, 0.43f, 0.29f),
            0.80f);

        for (int i = 0; i < RootSpriteCount; i++)
        {
            sLeaser.sprites[i].color = rootColor;
        }

        for (int i = 0; i < SlotCount; i++)
        {
            sLeaser.sprites[StemSprite(i)].color = stemColor;
            sLeaser.sprites[ShellSprite(i)].color = shellColor;
            sLeaser.sprites[HighlightSprite(i)].color = Color.white;
            sLeaser.sprites[BudSprite(i)].color = budColor;
        }
    }

    public void AddToContainer(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer newContainer)
    {
        newContainer ??= rCam.ReturnFContainer("Items");

        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            sLeaser.sprites[i].RemoveFromContainer();
            newContainer.AddChild(sLeaser.sprites[i]);
        }
    }
}
