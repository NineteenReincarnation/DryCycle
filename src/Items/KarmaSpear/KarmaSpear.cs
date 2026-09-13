using System;
using DryCycle.Framework.KarmicManipulation;
using RWCustom;
using UnityEngine;
using Watcher;

namespace DryCycle.Items.KarmaSpear;

internal sealed class KarmaSpear : Spear
{
    private const int ExtraSpriteCount = 6;

    private bool _wallFieldStarted;
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

        if (IsSpent || room == null)
        {
            return;
        }

        if (mode == Mode.Thrown && firstChunk.vel.sqrMagnitude > 36f)
        {
            _trailCounter++;
            if (_trailCounter % 4 == 0)
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

        if (mode == Mode.StuckInWall && !_wallFieldStarted)
        {
            StartWallField();
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

        if (!IsSpent && newMode == Mode.StuckInWall && !_wallFieldStarted)
        {
            StartWallField();
        }
        else if (!IsSpent &&
                 oldMode == Mode.StuckInWall &&
                 newMode != Mode.StuckInWall &&
                 _wallFieldStarted)
        {
            MarkSpent();
        }
    }

    private void ActivateCreatureBinding(Creature creature, Vector2 hitPosition)
    {
        _bindingStarted = true;
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
        if (_wallFieldStarted || IsSpent || room == null)
        {
            return;
        }

        _wallFieldStarted = true;
        room.PlaySound(WatcherEnums.WatcherSoundID.Templar_Shield_Deflect, firstChunk);
        KarmicVisualEffects.SpawnImpactPulse(this, firstChunk.pos, KarmaLevel, 72f);
        room.AddObject(new KarmaSpearField(this));
    }

    internal void MarkSpent()
    {
        if (KarmaAbstract == null || KarmaAbstract.Spent)
        {
            return;
        }

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

        sLeaser.sprites[baseCount] = new FSprite("SmallSpear");
        sLeaser.sprites[baseCount + 1] = new FSprite("Futile_White")
        {
            shader = rCam.room.game.rainWorld.Shaders["VectorCircle"]
        };
        sLeaser.sprites[baseCount + 2] = new FSprite(
            HUD.KarmaMeter.KarmaSymbolSprite(
                small: false,
                new IntVector2(Mathf.Clamp(KarmaLevel - 1, 0, 9), Mathf.Clamp(KarmaLevel - 1, 0, 9))));

        for (int i = 0; i < 3; i++)
        {
            sLeaser.sprites[baseCount + 3 + i] = new FSprite("pixel");
        }

        AddToContainer(sLeaser, rCam, null);
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
        Vector2 direction = Vector2.Lerp(lastRotation, rotation, timeStacker);
        if (direction.sqrMagnitude < 0.001f)
        {
            direction = Vector2.right;
        }
        direction.Normalize();

        FSprite baseSprite = sLeaser.sprites[0];
        FSprite overlay = sLeaser.sprites[baseCount];
        FSprite ring = sLeaser.sprites[baseCount + 1];
        FSprite glyph = sLeaser.sprites[baseCount + 2];

        overlay.x = baseSprite.x;
        overlay.y = baseSprite.y;
        overlay.rotation = baseSprite.rotation;
        overlay.anchorY = baseSprite.anchorY;
        overlay.color = KarmicVisualEffects.Gold;
        overlay.alpha = IsSpent ? 0f : 0.26f;

        float pulse = 0.5f + 0.5f * Mathf.Sin((room?.game?.clock ?? 0) * 0.13f + KarmaLevel);
        Vector2 tip = center + direction * (pivotAtTip ? 7f : 25f);
        ring.x = tip.x - camPos.x;
        ring.y = tip.y - camPos.y;
        ring.scale = Mathf.Lerp(1.5f, 2.25f, pulse);
        ring.alpha = IsSpent ? 0f : Mathf.Lerp(0.08f, 0.16f, pulse);
        ring.color = KarmicVisualEffects.Gold;

        glyph.x = tip.x - camPos.x;
        glyph.y = tip.y - camPos.y;
        glyph.rotation = -(room?.game?.clock ?? 0) * 0.35f;
        glyph.scale = Mathf.Lerp(0.20f, 0.27f, pulse);
        glyph.alpha = IsSpent ? 0f : Mathf.Lerp(0.45f, 0.78f, pulse);
        glyph.color = Color.Lerp(Color.white, KarmicVisualEffects.Gold, 0.62f);

        for (int i = 0; i < 3; i++)
        {
            FSprite rune = sLeaser.sprites[baseCount + 3 + i];
            float along = Mathf.Lerp(-13f, 9f, i / 2f);
            Vector2 runePos = center + direction * along;
            rune.x = runePos.x - camPos.x;
            rune.y = runePos.y - camPos.y;
            rune.rotation = baseSprite.rotation;
            rune.scaleX = 1.1f;
            rune.scaleY = 3.2f + i * 0.8f;
            rune.color = IsSpent
                ? Color.Lerp(Color.black, baseSprite.color, 0.25f)
                : Color.Lerp(KarmicVisualEffects.Gold, Color.white, 0.22f);
            rune.alpha = IsSpent ? 0.28f : Mathf.Lerp(0.28f, 0.55f, pulse);
        }
    }
}
