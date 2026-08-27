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

    private int _leakDripCounter;
    private int _drinkPoseFrames;
    private Vector2 _drinkPoseTarget;

    public AbstractDewPod AbstrPod => abstractPhysicalObject as AbstractDewPod;

    public float WaterWV => AbstrPod?.WaterWV ?? 0f;
    public bool Broken => AbstrPod?.Broken ?? true;
    public float Fill => Mathf.Clamp01(WaterWV / AbstractDewPod.MaxWaterWV);

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

        if (!AbstrPod.Broken && firstChunk.submersion > 0f && AbstrPod.WaterWV < AbstractDewPod.MaxWaterWV)
        {
            AbstrPod.WaterWV = Mathf.Min(
                AbstractDewPod.MaxWaterWV,
                AbstrPod.WaterWV + RefillPerTickWV);
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

    private void BreakOpen()
    {
        if (AbstrPod == null || AbstrPod.Broken)
        {
            return;
        }

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
            room.AddObject(new WaterDrip(firstChunk.pos + Custom.RNV() * 2f, velocity, waterColor: true));
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

        room.AddObject(new WaterDrip(origin, velocity, waterColor: true));
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
        float height = Mathf.Lerp(0.72f, 1.15f, fullness);
        float width = Mathf.Lerp(0.58f, 0.86f, fullness);

        FSprite shell = sLeaser.sprites[0];
        shell.x = drawPos.x;
        shell.y = drawPos.y;
        shell.scaleX = width;
        shell.scaleY = height;

        FSprite liquid = sLeaser.sprites[1];
        liquid.isVisible = fill > 0.005f;
        liquid.x = drawPos.x;
        liquid.y = drawPos.y - Mathf.Lerp(3.5f, 0.4f, fill);
        liquid.scaleX = width * Mathf.Lerp(0.62f, 0.82f, fill);
        liquid.scaleY = height * Mathf.Lerp(0.16f, 0.78f, fill);

        FSprite window = sLeaser.sprites[2];
        window.x = drawPos.x;
        window.y = drawPos.y + height * 5.2f;
        window.scaleX = width * 0.48f;
        window.scaleY = Mathf.Lerp(0.12f, 0.28f, fullness);
        window.alpha = Mathf.Lerp(0.22f, 0.72f, fill);

        FSprite highlight = sLeaser.sprites[3];
        highlight.isVisible = fill > 0.02f;
        highlight.x = drawPos.x - width * 2.5f;
        highlight.y = drawPos.y + height * 2.2f;
        highlight.scaleX = width * 0.12f;
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
        sLeaser.sprites[1].color = Color.Lerp(
            new Color(0.14f, 0.48f, 0.50f),
            new Color(0.50f, 0.92f, 0.78f),
            Fill);
        sLeaser.sprites[2].color = Color.Lerp(Color.white, new Color(0.62f, 0.96f, 0.82f), 0.45f);
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
