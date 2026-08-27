using System;
using System.Collections.Generic;
using System.Globalization;
using RWCustom;
using UnityEngine;
using Random = UnityEngine.Random;

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

    internal static bool TryGetLocalWaterColor(Room sourceRoom, out Color color)
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

        // Dew Pod water should visually match the exposed surface of the room's
        // water, not the deeper body color. This is also the color a player sees
        // when deciding what water they are collecting.
        if (palette.waterSurfaceColor1.a <= 0.001f &&
            palette.waterSurfaceColor2.a <= 0.001f)
        {
            return false;
        }

        color = Color.Lerp(
            palette.waterSurfaceColor2,
            palette.waterSurfaceColor1,
            0.5f);
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
        // Shell, liquid, one crack stroke, and a shared accent sprite. The accent
        // is a restrained surface highlight while intact and becomes the secondary
        // crack after rupture. The old translucent top-window stays removed.
        sLeaser.sprites = new FSprite[4];

        sLeaser.sprites[0] = new FSprite("Circle20");
        sLeaser.sprites[1] = new FSprite("Circle20");
        sLeaser.sprites[2] = new FSprite("Futile_White");
        sLeaser.sprites[3] = new FSprite("Circle20");

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

        // Slightly more columnar than the earlier egg-like silhouette while still
        // keeping soft rounded caps. Empty pods shrink, but preserve the same form.
        float height = Mathf.Lerp(0.76f, 1.24f, fullness);
        float width = Mathf.Lerp(0.57f, 0.82f, fullness);

        FSprite shell = sLeaser.sprites[0];
        shell.x = drawPos.x;
        shell.y = drawPos.y;
        shell.scaleX = width;
        shell.scaleY = height;

        FSprite liquid = sLeaser.sprites[1];
        liquid.isVisible = fill > 0.005f;
        liquid.x = drawPos.x;
        liquid.y = drawPos.y - Mathf.Lerp(3.35f, 0.30f, fill);

        // The visible water occupies most of the fleshy chamber, especially in the
        // vertical axis, while retaining a readable dark-green wall around it.
        liquid.scaleX = width * Mathf.Lerp(0.60f, 0.78f, fill);
        liquid.scaleY = height * Mathf.Lerp(0.22f, 0.88f, fill);
        liquid.color = LiquidColor;

        bool broken = Broken;

        FSprite primaryCrack = sLeaser.sprites[2];
        primaryCrack.isVisible = broken;
        primaryCrack.x = drawPos.x + 1.3f;
        primaryCrack.y = drawPos.y + 2.4f;
        primaryCrack.scaleX = 0.12f;
        primaryCrack.scaleY = 0.42f;
        primaryCrack.rotation = 32f;
        primaryCrack.alpha = 1f;
        primaryCrack.color = rCam.currentPalette.blackColor;

        FSprite accent = sLeaser.sprites[3];
        if (!broken)
        {
            // A narrow wet sheen rather than the old bright white patch. It sits
            // close to the shell edge and grows only slightly with the water level.
            accent.isVisible = fill > 0.02f;
            accent.x = drawPos.x - width * 3.15f;
            accent.y = drawPos.y + height * 1.75f;
            accent.scaleX = width * 0.075f;
            accent.scaleY = height * Mathf.Lerp(0.20f, 0.31f, fill);
            accent.rotation = -7f;
            accent.alpha = Mathf.Lerp(0.08f, 0.22f, fill);
            accent.color = Color.Lerp(shell.color, Color.white, 0.52f);
        }
        else
        {
            accent.isVisible = true;
            accent.x = drawPos.x + 2.2f;
            accent.y = drawPos.y + 0.5f;
            accent.scaleX = 0.10f;
            accent.scaleY = 0.28f;
            accent.rotation = -38f;
            accent.alpha = 1f;
            accent.color = rCam.currentPalette.blackColor;
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
        sLeaser.sprites[2].color = palette.blackColor;
        sLeaser.sprites[3].color = palette.blackColor;
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
