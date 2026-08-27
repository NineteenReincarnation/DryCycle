using System;
using System.Collections.Generic;
using System.Globalization;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.DewPod;

internal sealed class DewPod : PlayerCarryableItem, IDrawable
{
    public const float RefillRateWVPerSecond = 100f;
    public const float DrinkRateWVPerSecond = 100f;
    public const float LeakRateWVPerSecond = 10f;
    public const float BreakBurstLossWV = 100f;

    private const float SimulationTicksPerSecond = 40f;
    private const float RefillPerTickWV = RefillRateWVPerSecond / SimulationTicksPerSecond;
    private const float LeakPerTickWV = LeakRateWVPerSecond / SimulationTicksPerSecond;

    private static readonly Color FallbackLiquidColor = new(0.50f, 0.92f, 0.78f);

    private int _leakDripCounter;
    private int _drinkPoseFrames;
    private Vector2 _drinkPoseTarget;

    public AbstractDewPod AbstrPod => abstractPhysicalObject as AbstractDewPod;

    public float WaterWV => AbstrPod?.WaterWV ?? 0f;
    public bool Broken => AbstrPod?.Broken ?? true;
    public float Fill => Mathf.Clamp01(WaterWV / AbstractDewPod.MaxWaterWV);
    public Color LiquidColor => AbstrPod != null && AbstrPod.HasLiquidColor
        ? AbstrPod.LiquidColor
        : FallbackLiquidColor;

    private sealed class DewPodWaterDrip : WaterDrip
    {
        private readonly Color _liquidColor;

        public DewPodWaterDrip(Vector2 pos, Vector2 vel, Color liquidColor)
            : base(pos, vel, waterColor: false)
        {
            _liquidColor = liquidColor;
            width = 1.5f;
        }

        public override void ApplyPalette(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            RoomPalette palette)
        {
            colors = new Color[3]
            {
                Color.Lerp(palette.blackColor, _liquidColor, 0.55f),
                _liquidColor,
                Color.Lerp(_liquidColor, Color.white, 0.72f)
            };
        }
    }

    public DewPod(AbstractPhysicalObject abstractPhysicalObject)
        : base(abstractPhysicalObject)
    {
        bodyChunks = new BodyChunk[1];
        bodyChunks[0] = new BodyChunk(this, 0, Vector2.zero, 7.5f, 0.16f);
        bodyChunkConnections = new BodyChunkConnection[0];

        airFriction = 0.995f;
        gravity = 0.9f;
        bounce = 0.18f;
        surfaceFriction = 0.72f;
        collisionLayer = 1;
        waterFriction = 0.93f;
        buoyancy = 1.15f;

        _leakDripCounter = Random.Range(4, 10);
        RestoreLiquidColorFromSavedAttributes();
    }

    public override void Update(bool eu)
    {
        base.Update(eu);

        if (_drinkPoseFrames > 0)
        {
            _drinkPoseFrames--;
        }

        if (AbstrPod == null || room == null)
        {
            return;
        }

        InitializeLiquidColorFromRoomIfNeeded();

        if (!AbstrPod.Broken &&
            firstChunk.submersion > 0f &&
            AbstrPod.WaterWV < AbstractDewPod.MaxWaterWV)
        {
            float addedWV = Mathf.Min(
                RefillPerTickWV,
                AbstractDewPod.MaxWaterWV - AbstrPod.WaterWV);

            Color sourceColor = LiquidColor;
            if (TryGetLocalWaterColor(room, out Color localWaterColor))
            {
                sourceColor = localWaterColor;
            }

            AddWater(addedWV, sourceColor);
        }

        if (!AbstrPod.Broken || AbstrPod.WaterWV <= 0f)
        {
            return;
        }

        AbstrPod.WaterWV = Mathf.Max(0f, AbstrPod.WaterWV - LeakPerTickWV);
        _leakDripCounter--;

        if (_leakDripCounter <= 0)
        {
            SpawnLeakDrip();
            _leakDripCounter = Random.Range(5, 12);
        }
    }

    public override void PlaceInRoom(Room placeRoom)
    {
        base.PlaceInRoom(placeRoom);

        if (AbstrPod != null &&
            !AbstrPod.isConsumed &&
            AbstrPod.placedObjectIndex >= 0 &&
            AbstrPod.placedObjectIndex < placeRoom.roomSettings.placedObjects.Count)
        {
            firstChunk.HardSetPosition(
                placeRoom.roomSettings.placedObjects[AbstrPod.placedObjectIndex].pos);
        }
        else
        {
            firstChunk.HardSetPosition(placeRoom.MiddleOfTile(abstractPhysicalObject.pos));
        }

        InitializeLiquidColorFromRoomIfNeeded();
    }

    public override void Grabbed(Creature.Grasp grasp)
    {
        base.Grabbed(grasp);
        AbstrPod?.Consume();
    }

    public override void HitByWeapon(Weapon weapon)
    {
        base.HitByWeapon(weapon);
        BreakOpen();
    }

    public override void TerrainImpact(int chunk, IntVector2 direction, float speed, bool firstContact)
    {
        base.TerrainImpact(chunk, direction, speed, firstContact);

        if (firstContact && speed >= 8.5f)
        {
            BreakOpen();
        }
    }

    public override void HitByExplosion(float hitFac, Explosion explosion, int hitChunk)
    {
        base.HitByExplosion(hitFac, explosion, hitChunk);

        if (hitFac >= 0.15f)
        {
            BreakOpen();
        }
    }

    internal float RemoveWater(float requestedWV)
    {
        if (AbstrPod == null || requestedWV <= 0f || AbstrPod.WaterWV <= 0f)
        {
            return 0f;
        }

        float removed = Mathf.Min(requestedWV, AbstrPod.WaterWV);
        AbstrPod.WaterWV -= removed;
        return removed;
    }

    internal void MarkDrinking(Vector2 mouthPosition)
    {
        _drinkPoseTarget = mouthPosition;
        _drinkPoseFrames = 2;
    }

    private void AddWater(float addedWV, Color sourceColor)
    {
        if (AbstrPod == null || addedWV <= 0f)
        {
            return;
        }

        float beforeWV = Mathf.Clamp(AbstrPod.WaterWV, 0f, AbstractDewPod.MaxWaterWV);
        float actualAddedWV = Mathf.Min(
            addedWV,
            AbstractDewPod.MaxWaterWV - beforeWV);

        if (actualAddedWV <= 0f)
        {
            return;
        }

        sourceColor.a = 1f;
        float afterWV = beforeWV + actualAddedWV;

        if (!AbstrPod.HasLiquidColor || beforeWV <= 0.0001f)
        {
            AbstrPod.LiquidColor = sourceColor;
            AbstrPod.HasLiquidColor = true;
        }
        else
        {
            // Volume-weighted mixing: only the newly added WV contributes the
            // source palette color. Existing liquid keeps its previous share.
            float sourceShare = actualAddedWV / afterWV;
            AbstrPod.LiquidColor = Color.Lerp(
                AbstrPod.LiquidColor,
                sourceColor,
                sourceShare);
            AbstrPod.LiquidColor.a = 1f;
        }

        AbstrPod.WaterWV = afterWV;
    }

    private void InitializeLiquidColorFromRoomIfNeeded()
    {
        if (AbstrPod == null ||
            AbstrPod.HasLiquidColor ||
            AbstrPod.WaterWV <= 0f ||
            room == null)
        {
            return;
        }

        if (TryGetLocalWaterColor(room, out Color localWaterColor))
        {
            AbstrPod.LiquidColor = localWaterColor;
            AbstrPod.HasLiquidColor = true;
        }
    }

    private static bool TryGetLocalWaterColor(Room sourceRoom, out Color color)
    {
        color = FallbackLiquidColor;

        if (sourceRoom == null)
        {
            return false;
        }

        if (sourceRoom.waterObject != null &&
            TryGetPaletteWaterColor(sourceRoom.waterObject.palette, out color))
        {
            return true;
        }

        if (sourceRoom.game?.cameras != null)
        {
            for (int i = 0; i < sourceRoom.game.cameras.Length; i++)
            {
                RoomCamera camera = sourceRoom.game.cameras[i];
                if (camera?.room == sourceRoom &&
                    TryGetPaletteWaterColor(camera.currentPalette, out color))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetPaletteWaterColor(RoomPalette palette, out Color color)
    {
        color = FallbackLiquidColor;

        // An untouched/default RoomPalette has transparent-zero colors. A real
        // palette can legitimately contain very dark water, so alpha is the safe
        // initialization check rather than RGB brightness.
        if (palette.waterColor1.a <= 0.001f && palette.waterColor2.a <= 0.001f)
        {
            return false;
        }

        color = Color.Lerp(palette.waterColor2, palette.waterColor1, 0.5f);
        color.a = 1f;
        return true;
    }

    private void RestoreLiquidColorFromSavedAttributes()
    {
        if (AbstrPod?.unrecognizedAttributes == null ||
            AbstrPod.unrecognizedAttributes.Length == 0)
        {
            return;
        }

        List<string> remaining = new();

        foreach (string attribute in AbstrPod.unrecognizedAttributes)
        {
            if (attribute != null &&
                attribute.StartsWith(
                    AbstractDewPod.LiquidColorAttributePrefix,
                    StringComparison.Ordinal) &&
                TryParseSavedColor(
                    attribute.Substring(AbstractDewPod.LiquidColorAttributePrefix.Length),
                    out Color parsedColor))
            {
                AbstrPod.LiquidColor = parsedColor;
                AbstrPod.HasLiquidColor = true;
                continue;
            }

            if (!string.IsNullOrEmpty(attribute))
            {
                remaining.Add(attribute);
            }
        }

        AbstrPod.unrecognizedAttributes = remaining.ToArray();
    }

    private static bool TryParseSavedColor(string value, out Color color)
    {
        color = Color.clear;
        string[] parts = (value ?? string.Empty).Split(',');

        if (parts.Length != 3 ||
            !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) ||
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g) ||
            !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
        {
            return false;
        }

        color = new Color(
            Mathf.Clamp01(r),
            Mathf.Clamp01(g),
            Mathf.Clamp01(b),
            1f);
        return true;
    }

    private void BreakOpen()
    {
        if (AbstrPod == null || AbstrPod.Broken)
        {
            return;
        }

        InitializeLiquidColorFromRoomIfNeeded();
        AbstrPod.Broken = true;
        AbstrPod.WaterWV = Mathf.Max(0f, AbstrPod.WaterWV - BreakBurstLossWV);

        if (room == null)
        {
            return;
        }

        int burstCount = Mathf.Clamp(Mathf.CeilToInt(AbstrPod.WaterWV / 70f), 3, 8);
        for (int i = 0; i < burstCount; i++)
        {
            Vector2 velocity = Custom.RNV() * Mathf.Lerp(2f, 7f, Random.value);
            velocity.y += Random.Range(0.5f, 3f);
            room.AddObject(new DewPodWaterDrip(
                firstChunk.pos + Custom.RNV() * 2f,
                velocity,
                LiquidColor));
        }
    }

    private void SpawnLeakDrip()
    {
        if (room == null || AbstrPod == null || AbstrPod.WaterWV <= 0f)
        {
            return;
        }

        float pressure = Mathf.Lerp(0.25f, 1f, Fill);
        Vector2 origin = firstChunk.pos + new Vector2(Random.Range(-2.5f, 2.5f), Random.Range(-2f, 2f));
        Vector2 velocity = firstChunk.vel * 0.15f +
                           new Vector2(Random.Range(-0.8f, 0.8f), Random.Range(-2.4f, -0.5f)) * pressure;

        room.AddObject(new DewPodWaterDrip(origin, velocity, LiquidColor));
    }

    public void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        sLeaser.sprites = new FSprite[6];

        sLeaser.sprites[0] = new FSprite("Circle20");
        sLeaser.sprites[1] = new FSprite("Circle20");
        sLeaser.sprites[2] = new FSprite("Circle20");
        sLeaser.sprites[3] = new FSprite("Circle20");
        sLeaser.sprites[4] = new FSprite("Futile_White");
        sLeaser.sprites[5] = new FSprite("Futile_White");

        AddToContainer(sLeaser, rCam, null);
    }

    public void DrawSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        Vector2 drawPos = Vector2.Lerp(firstChunk.lastPos, firstChunk.pos, timeStacker);

        if (_drinkPoseFrames > 0)
        {
            drawPos = Vector2.Lerp(drawPos, _drinkPoseTarget, 0.72f);
        }

        drawPos -= camPos;

        float fill = Fill;
        float fullness = Mathf.Lerp(0.62f, 1f, Mathf.Sqrt(fill));

        // Keep the pod rounded, but stretch it just enough toward a short fleshy
        // cylinder instead of a near-circular fruit. The change is intentionally
        // small so it still reads as a soft, water-filled organic structure.
        float height = Mathf.Lerp(0.74f, 1.18f, fullness);
        float width = Mathf.Lerp(0.58f, 0.84f, fullness);

        FSprite shell = sLeaser.sprites[0];
        shell.x = drawPos.x;
        shell.y = drawPos.y;
        shell.scaleX = width;
        shell.scaleY = height;

        FSprite liquid = sLeaser.sprites[1];
        liquid.isVisible = fill > 0.005f;
        liquid.x = drawPos.x;
        liquid.y = drawPos.y - Mathf.Lerp(3.4f, 0.45f, fill);

        // Pull the liquid inward relative to the shell. This leaves a thicker
        // dark-green fleshy wall around the visible water without increasing the
        // overall item size.
        liquid.scaleX = width * Mathf.Lerp(0.54f, 0.72f, fill);
        liquid.scaleY = height * Mathf.Lerp(0.14f, 0.70f, fill);

        Color displayedLiquid = Color.Lerp(
            rCam.currentPalette.blackColor,
            LiquidColor,
            0.88f);
        liquid.color = displayedLiquid;

        FSprite window = sLeaser.sprites[2];
        window.x = drawPos.x;
        window.y = drawPos.y + height * 5.15f;
        window.scaleX = width * 0.42f;
        window.scaleY = Mathf.Lerp(0.12f, 0.27f, fullness);
        window.alpha = Mathf.Lerp(0.22f, 0.72f, fill);
        window.color = Color.Lerp(displayedLiquid, Color.white, 0.48f);

        FSprite highlight = sLeaser.sprites[3];
        highlight.isVisible = fill > 0.02f;
        highlight.x = drawPos.x - width * 2.45f;
        highlight.y = drawPos.y + height * 2.25f;
        highlight.scaleX = width * 0.11f;
        highlight.scaleY = height * Mathf.Lerp(0.18f, 0.42f, fill);
        highlight.alpha = Mathf.Lerp(0.08f, 0.38f, fill);

        bool broken = Broken;
        for (int i = 4; i <= 5; i++)
        {
            FSprite crack = sLeaser.sprites[i];
            crack.isVisible = broken;
            crack.x = drawPos.x + (i == 4 ? 1.3f : 2.2f);
            crack.y = drawPos.y + (i == 4 ? 2.4f : 0.5f);
            crack.scaleX = 0.12f;
            crack.scaleY = i == 4 ? 0.42f : 0.30f;
            crack.rotation = i == 4 ? 32f : -38f;
        }

        if (slatedForDeletetion || room != rCam.room)
        {
            sLeaser.CleanSpritesAndRemove();
        }
    }

    public void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
    {
        Color shellBase = Color.Lerp(palette.blackColor, new Color(0.18f, 0.48f, 0.36f), 0.78f);
        Color dryShell = Color.Lerp(palette.blackColor, new Color(0.27f, 0.36f, 0.28f), 0.62f);
        Color shellColor = Color.Lerp(dryShell, shellBase, Mathf.Lerp(0.2f, 1f, Fill));

        if (Broken)
        {
            shellColor = Color.Lerp(shellColor, palette.blackColor, 0.22f);
        }

        sLeaser.sprites[0].color = shellColor;
        sLeaser.sprites[3].color = Color.white;
        sLeaser.sprites[4].color = palette.blackColor;
        sLeaser.sprites[5].color = palette.blackColor;
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
