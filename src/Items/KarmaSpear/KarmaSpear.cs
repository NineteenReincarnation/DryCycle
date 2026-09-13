using System;
using DryCycle.Framework.KarmicManipulation;
using RWCustom;
using UnityEngine;
using Watcher;

namespace DryCycle.Items.KarmaSpear;

internal sealed class KarmaSpear : Spear
{
    private const int ExtraSpriteCount = 14;

    private KarmaSpearField _wallField;
    private bool _bindingStarted;
    private int _trailCounter;

    internal KarmaSpear(AbstractPhysicalObject abstractPhysicalObject, World world)
        : base(abstractPhysicalObject, world)
    {
    }

    private AbstractKarmaSpear KarmaAbstract => abstractPhysicalObject as AbstractKarmaSpear;

    internal int KarmaLevel => KarmaAbstract?.KarmaLevel ?? 1;
    internal bool IsSpent => KarmaAbstract?.Spent != false;

    public override void Update(bool eu)
    {
        base.Update(eu);

        if (room == null)
        {
            StopWallField();
            return;
        }

        if (IsSpent)
        {
            StopWallField();
            return;
        }

        if (mode == Mode.Thrown && firstChunk.vel.sqrMagnitude > 36f)
        {
            _trailCounter++;
            if (_trailCounter % 3 == 0)
            {
                Vector2 direction = rotation.sqrMagnitude > 0.001f
                    ? rotation.normalized
                    : firstChunk.vel.normalized;
                KarmicVisualEffects.SpawnSpearAfterimage(
                    room,
                    firstChunk.pos - direction * 5f,
                    direction,
                    KarmaLevel);
            }
        }
        else
        {
            _trailCounter = 0;
        }

        if (mode == Mode.StuckInWall)
        {
            StartWallField();
        }
        else if (_wallField != null)
        {
            StopWallField();
        }
    }

    public override void Thrown(
        Creature thrownBy,
        Vector2 thrownPos,
        Vector2? firstFrameTraceFromPos,
        IntVector2 throwDir,
        float frc,
        bool eu)
    {
        base.Thrown(thrownBy, thrownPos, firstFrameTraceFromPos, throwDir, frc, eu);

        if (!IsSpent && room != null)
        {
            room.PlaySound(WatcherEnums.WatcherSoundID.Templar_Shield_Tick_7, firstChunk);
            KarmicVisualEffects.SpawnChargePulse(this, 1f);
            KarmicVisualEffects.SpawnSparks(room, firstChunk.pos, 5, 3.4f);
        }
    }

    public override bool HitSomething(SharedPhysics.CollisionResult result, bool eu)
    {
        bool hit = base.HitSomething(result, eu);

        if (!hit || IsSpent || _bindingStarted || result.obj is not Creature creature)
        {
            return hit;
        }

        ActivateCreatureBinding(creature, result.chunk?.pos ?? firstChunk.pos);
        return hit;
    }

    public override void ChangeMode(Mode newMode)
    {
        Mode oldMode = mode;
        base.ChangeMode(newMode);

        if (IsSpent)
        {
            StopWallField();
            return;
        }

        if (newMode == Mode.StuckInWall)
        {
            StartWallField();
        }
        else if (oldMode == Mode.StuckInWall && newMode != Mode.StuckInWall)
        {
            // Pulling the spear back out is free. Wall anchoring is a reusable stance,
            // not the one-shot karmic discharge.
            StopWallField();
        }
    }

    private void ActivateCreatureBinding(Creature creature, Vector2 hitPosition)
    {
        _bindingStarted = true;
        StopWallField();
        KarmicVisualEffects.SpawnImpactPulse(this, hitPosition, KarmaLevel, 82f + KarmaLevel * 3f);
        room?.PlaySound(WatcherEnums.WatcherSoundID.Templar_Shield_Deflect, firstChunk);

        bool small = IsSmallTarget(creature);
        bool large = IsLargeTarget(creature);

        if (small)
        {
            creature.Die();
            MarkSpent();
            return;
        }

        bool lodged = mode == Mode.StuckInCreature && ReferenceEquals(stuckInObject, creature);
        KarmicBindingEffect binding = new(this, creature, creature.mainBodyChunk.pos, large, lodged);
        room.AddObject(binding);

        if (!lodged)
        {
            // The target still receives the short pin, but a spear that bounced or was
            // rejected by armor cannot remain charged and strike a second target.
            MarkSpent();
        }
    }

    private static bool IsSmallTarget(Creature creature)
    {
        return creature.TotalMass <= 0.34f;
    }

    private static bool IsLargeTarget(Creature creature)
    {
        return creature.TotalMass >= 3.4f ||
               creature is Vulture ||
               creature is MirosBird ||
               creature is DaddyLongLegs ||
               creature is BigEel;
    }

    private void StartWallField()
    {
        if (IsSpent || room == null || mode != Mode.StuckInWall)
        {
            return;
        }

        if (_wallField != null && !_wallField.slatedForDeletetion)
        {
            return;
        }

        room.PlaySound(WatcherEnums.WatcherSoundID.Templar_Shield_Deflect, firstChunk);
        KarmicVisualEffects.SpawnImpactPulse(this, firstChunk.pos, KarmaLevel, 96f);
        KarmicVisualEffects.SpawnSparks(room, firstChunk.pos, 12, 4.2f);

        _wallField = new KarmaSpearField(this);
        room.AddObject(_wallField);
    }

    private void StopWallField()
    {
        if (_wallField == null)
        {
            return;
        }

        if (!_wallField.slatedForDeletetion)
        {
            _wallField.Destroy();
        }
        _wallField = null;
    }

    internal void MarkSpent()
    {
        if (KarmaAbstract == null || KarmaAbstract.Spent)
        {
            return;
        }

        StopWallField();
        KarmaAbstract.Spent = true;
        KarmicVisualEffects.SpawnImpactPulse(this, firstChunk.pos, KarmaLevel, 54f);
        KarmicVisualEffects.SpawnSparks(room, firstChunk.pos, 8, 4f);
    }

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        base.InitiateSprites(sLeaser, rCam);

        int baseCount = sLeaser.sprites.Length;
        FSprite[] sprites = new FSprite[baseCount + ExtraSpriteCount];
        Array.Copy(sLeaser.sprites, sprites, baseCount);
        sLeaser.sprites = sprites;

        // Layered silhouette: dark vanilla body + gold shell + pale inner core.
        sLeaser.sprites[baseCount] = new FSprite("SmallSpear");
        sLeaser.sprites[baseCount + 1] = new FSprite("SmallSpear");

        sLeaser.sprites[baseCount + 2] = MakeVectorCircle(rCam);
        sLeaser.sprites[baseCount + 3] = MakeVectorCircle(rCam);
        sLeaser.sprites[baseCount + 4] = new FSprite(
            global::HUD.KarmaMeter.KarmaSymbolSprite(
                small: false,
                new IntVector2(Mathf.Clamp(KarmaLevel - 1, 0, 9), Mathf.Clamp(KarmaLevel - 1, 0, 9))));

        for (int i = 5; i <= 12; i++)
        {
            sLeaser.sprites[baseCount + i] = new FSprite("pixel");
        }

        sLeaser.sprites[baseCount + 13] = MakeVectorCircle(rCam);
        AddToContainer(sLeaser, rCam, null);
    }

    private static FSprite MakeVectorCircle(RoomCamera rCam)
    {
        return new FSprite("Futile_White")
        {
            shader = rCam.room.game.rainWorld.Shaders["VectorCircle"]
        };
    }

    public override void DrawSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        base.DrawSprites(sLeaser, rCam, timeStacker, camPos);

        int baseCount = sLeaser.sprites.Length - ExtraSpriteCount;
        if (baseCount <= 0)
        {
            return;
        }

        Vector2 center = Vector2.Lerp(firstChunk.lastPos, firstChunk.pos, timeStacker);
        Vector3 slerped = Vector3.Slerp(lastRotation, rotation, timeStacker);
        Vector2 direction = new(slerped.x, slerped.y);
        if (direction.sqrMagnitude < 0.001f)
        {
            direction = Vector2.right;
        }
        direction.Normalize();
        Vector2 perpendicular = new(-direction.y, direction.x);

        FSprite baseSprite = sLeaser.sprites[0];
        float rotationDeg = baseSprite.rotation;
        float clock = room?.game?.clock ?? 0;
        float pulse = 0.5f + 0.5f * Mathf.Sin(clock * 0.13f + KarmaLevel * 0.73f);
        float secondaryPulse = 0.5f + 0.5f * Mathf.Sin(clock * 0.075f + 1.7f);
        bool wallAnchored = !IsSpent && mode == Mode.StuckInWall;

        float tipDistance = Mathf.Lerp(lastPivotAtTip ? 7f : 26f, pivotAtTip ? 7f : 26f, timeStacker);
        Vector2 tip = center + direction * tipDistance;
        Vector2 tail = center - direction * 18f;

        FSprite shell = sLeaser.sprites[baseCount];
        FSprite core = sLeaser.sprites[baseCount + 1];
        CopySpearTransform(baseSprite, shell);
        CopySpearTransform(baseSprite, core);

        if (!IsSpent)
        {
            baseSprite.color = Color.Lerp(baseSprite.color, KarmicVisualEffects.Gold, 0.12f + pulse * 0.05f);
        }

        shell.scaleX = 1.08f;
        shell.scaleY = 1.025f;
        shell.color = Color.Lerp(KarmicVisualEffects.Gold, Color.white, 0.12f);
        shell.alpha = IsSpent ? 0f : Mathf.Lerp(0.24f, wallAnchored ? 0.46f : 0.36f, pulse);

        core.scaleX = 0.62f;
        core.scaleY = 0.98f;
        core.color = Color.Lerp(KarmicVisualEffects.Gold, Color.white, 0.72f);
        core.alpha = IsSpent ? 0f : Mathf.Lerp(0.07f, 0.17f, secondaryPulse);

        FSprite outerRing = sLeaser.sprites[baseCount + 2];
        SetSpritePosition(outerRing, tip, camPos);
        outerRing.scale = Mathf.Lerp(1.65f, wallAnchored ? 2.85f : 2.35f, pulse);
        outerRing.alpha = IsSpent ? 0f : Mathf.Lerp(0.07f, wallAnchored ? 0.22f : 0.15f, pulse);
        outerRing.color = KarmicVisualEffects.Gold;

        FSprite innerRing = sLeaser.sprites[baseCount + 3];
        SetSpritePosition(innerRing, tip, camPos);
        innerRing.scale = Mathf.Lerp(0.68f, wallAnchored ? 1.18f : 0.96f, secondaryPulse);
        innerRing.alpha = IsSpent ? 0f : Mathf.Lerp(0.13f, wallAnchored ? 0.31f : 0.24f, secondaryPulse);
        innerRing.color = Color.Lerp(KarmicVisualEffects.Gold, Color.white, 0.38f);

        FSprite glyph = sLeaser.sprites[baseCount + 4];
        SetSpritePosition(glyph, tip - direction * 2.5f, camPos);
        glyph.rotation = -clock * (wallAnchored ? 0.22f : 0.38f);
        glyph.scale = Mathf.Lerp(0.21f, wallAnchored ? 0.34f : 0.29f, pulse);
        glyph.alpha = IsSpent ? 0f : Mathf.Lerp(0.50f, wallAnchored ? 0.92f : 0.80f, pulse);
        glyph.color = Color.Lerp(Color.white, KarmicVisualEffects.Gold, 0.58f);

        FSprite spine = sLeaser.sprites[baseCount + 5];
        SetSpritePosition(spine, center - direction * 1.5f, camPos);
        spine.rotation = rotationDeg;
        spine.scaleX = 1.15f;
        spine.scaleY = 31f;
        spine.color = KarmicVisualEffects.Gold;
        spine.alpha = IsSpent ? 0f : Mathf.Lerp(0.055f, 0.12f, pulse);

        float[] runeOffsets = { -12f, -4f, 4f, 12f };
        for (int i = 0; i < 4; i++)
        {
            FSprite rune = sLeaser.sprites[baseCount + 6 + i];
            Vector2 runePos = center + direction * runeOffsets[i];
            SetSpritePosition(rune, runePos, camPos);
            rune.rotation = rotationDeg + (i % 2 == 0 ? 54f : -54f);
            rune.scaleX = 1.05f;
            rune.scaleY = 4.0f + i * 0.55f;
            rune.color = IsSpent
                ? Color.Lerp(Color.black, baseSprite.color, 0.30f)
                : Color.Lerp(KarmicVisualEffects.Gold, Color.white, 0.24f + i * 0.05f);
            rune.alpha = IsSpent
                ? 0.34f
                : Mathf.Lerp(0.30f, wallAnchored ? 0.72f : 0.57f, 0.5f + 0.5f * Mathf.Sin(clock * 0.11f + i));
        }

        // Two small side prongs make the powered spearhead read as a distinct relic
        // without replacing the recognizable vanilla spear silhouette.
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            FSprite prong = sLeaser.sprites[baseCount + 10 + i];
            Vector2 prongPos = tip - direction * 5.5f + perpendicular * side * 1.8f;
            SetSpritePosition(prong, prongPos, camPos);
            prong.rotation = rotationDeg + side * 27f;
            prong.scaleX = 1.1f;
            prong.scaleY = 8.2f;
            prong.color = IsSpent
                ? Color.Lerp(Color.black, baseSprite.color, 0.35f)
                : Color.Lerp(KarmicVisualEffects.Gold, Color.white, 0.34f);
            prong.alpha = IsSpent ? 0.48f : Mathf.Lerp(0.50f, 0.82f, pulse);
        }

        FSprite collar = sLeaser.sprites[baseCount + 12];
        SetSpritePosition(collar, center - direction * 9.5f, camPos);
        collar.rotation = rotationDeg + 90f;
        collar.scaleX = 1.15f;
        collar.scaleY = 7f;
        collar.color = IsSpent
            ? Color.Lerp(Color.black, baseSprite.color, 0.32f)
            : Color.Lerp(KarmicVisualEffects.Gold, Color.white, 0.18f);
        collar.alpha = IsSpent ? 0.30f : Mathf.Lerp(0.32f, wallAnchored ? 0.68f : 0.52f, secondaryPulse);

        FSprite tailSeal = sLeaser.sprites[baseCount + 13];
        SetSpritePosition(tailSeal, tail, camPos);
        tailSeal.scale = Mathf.Lerp(0.48f, wallAnchored ? 0.90f : 0.72f, secondaryPulse);
        tailSeal.alpha = IsSpent ? 0f : Mathf.Lerp(0.04f, wallAnchored ? 0.16f : 0.10f, secondaryPulse);
        tailSeal.color = KarmicVisualEffects.Gold;
    }

    private static void CopySpearTransform(FSprite source, FSprite target)
    {
        target.x = source.x;
        target.y = source.y;
        target.rotation = source.rotation;
        target.anchorX = source.anchorX;
        target.anchorY = source.anchorY;
    }

    private static void SetSpritePosition(FSprite sprite, Vector2 worldPos, Vector2 camPos)
    {
        sprite.x = worldPos.x - camPos.x;
        sprite.y = worldPos.y - camPos.y;
    }
}
