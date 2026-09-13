using RWCustom;
using UnityEngine;

namespace DryCycle.Framework.KarmicManipulation;

/// <summary>
/// Shared presentation vocabulary for karmic abilities. Keep the language here:
/// saturated-gold vector rings, karma glyphs, short spatial afterimages and sparse sparks.
/// Gameplay systems decide when an effect happens; this class decides how karmic power reads.
/// </summary>
internal static class KarmicVisualEffects
{
    internal static Color Gold => RainWorld.SaturatedGold;

    internal static void SpawnChargePulse(PhysicalObject source, float progress)
    {
        if (source?.room == null)
        {
            return;
        }

        float radius = Mathf.Lerp(9f, 24f, Mathf.Clamp01(progress));
        TemplarCircle circle = new(
            source,
            source.firstChunk.pos,
            radius,
            Mathf.Lerp(1.2f, 3.8f, progress),
            0f,
            16,
            true)
        {
            radDamping = 0.12f
        };
        source.room.AddObject(circle);
    }

    internal static void SpawnTransfer(Player player, PhysicalObject target, int karmaLevel)
    {
        if (player?.room == null || target == null || target.room != player.room)
        {
            return;
        }

        player.room.AddObject(new KarmicTransferGlyph(player, target, karmaLevel));
        SpawnImpactPulse(target, target.firstChunk.pos, karmaLevel, 46f);
    }

    internal static void SpawnImpactPulse(
        PhysicalObject source,
        Vector2 position,
        int karmaLevel,
        float maxRadius = 80f)
    {
        if (source?.room == null)
        {
            return;
        }

        source.room.AddObject(new KarmicGlyphPulse(source, position, karmaLevel, maxRadius));
        source.room.AddObject(new ShockWave(position, maxRadius, 0.04f, 5));
        SpawnSparks(source.room, position, Mathf.Clamp(6 + karmaLevel, 7, 16), 5.5f);
    }

    internal static void SpawnFieldPulse(PhysicalObject source, float radius)
    {
        if (source?.room == null)
        {
            return;
        }

        TemplarCircle circle = new(
            source,
            source.firstChunk.pos,
            Mathf.Max(18f, radius * 0.55f),
            3.8f,
            0f,
            22,
            true)
        {
            radDamping = 0.085f,
            maxThickness = 4f
        };
        source.room.AddObject(circle);
    }

    internal static void SpawnSpearAfterimage(
        Room room,
        Vector2 position,
        Vector2 rotation,
        int karmaLevel)
    {
        if (room == null)
        {
            return;
        }

        room.AddObject(new KarmicSpearAfterimage(position, rotation, karmaLevel));
    }

    internal static void SpawnSparks(
        Room room,
        Vector2 position,
        int count,
        float speed)
    {
        if (room == null)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            Vector2 direction = Custom.RNV();
            Spark spark = new(
                position + direction * Random.Range(2f, 7f),
                direction * Random.Range(speed * 0.35f, speed) + Random.insideUnitCircle,
                Color.Lerp(Gold, Color.white, Random.value * 0.65f),
                null,
                Random.Range(10, 18),
                Random.Range(18, 30));
            room.AddObject(spark);
        }
    }

    private static string KarmaGlyph(int karmaLevel)
    {
        int value = Mathf.Clamp(karmaLevel - 1, 0, 9);
        return HUD.KarmaMeter.KarmaSymbolSprite(
            small: false,
            new IntVector2(value, value));
    }

    private sealed class KarmicTransferGlyph : CosmeticSprite
    {
        private const int Lifetime = 26;

        private readonly PhysicalObject _target;
        private readonly Vector2 _start;
        private readonly int _karmaLevel;
        private int _age;

        internal KarmicTransferGlyph(Player player, PhysicalObject target, int karmaLevel)
        {
            _target = target;
            _karmaLevel = karmaLevel;
            _start = player.mainBodyChunk.pos + Vector2.up * 20f;
            pos = _start;
            lastPos = pos;
        }

        public override void Update(bool eu)
        {
            base.Update(eu);
            _age++;

            if (_target.slatedForDeletetion || _target.room != room || _age >= Lifetime)
            {
                Destroy();
                return;
            }

            float t = Mathf.Clamp01((float)_age / Lifetime);
            Vector2 end = _target.firstChunk.pos;
            Vector2 control = Vector2.Lerp(_start, end, 0.5f) + Vector2.up * (22f + _karmaLevel);
            float oneMinus = 1f - t;
            pos = oneMinus * oneMinus * _start +
                  2f * oneMinus * t * control +
                  t * t * end;
        }

        public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
        {
            sLeaser.sprites = new FSprite[2];
            sLeaser.sprites[0] = new FSprite(KarmaGlyph(_karmaLevel));
            sLeaser.sprites[1] = new FSprite("Futile_White")
            {
                shader = rCam.room.game.rainWorld.Shaders["VectorCircle"]
            };
            AddToContainer(sLeaser, rCam, rCam.ReturnFContainer("GrabShaders"));
        }

        public override void DrawSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            float timeStacker,
            Vector2 camPos)
        {
            Vector2 drawPos = Vector2.Lerp(lastPos, pos, timeStacker) - camPos;
            float t = Mathf.Clamp01((_age + timeStacker) / Lifetime);
            float envelope = Mathf.Sin(t * Mathf.PI);

            sLeaser.sprites[0].x = drawPos.x;
            sLeaser.sprites[0].y = drawPos.y;
            sLeaser.sprites[0].scale = Mathf.Lerp(0.55f, 0.24f, t);
            sLeaser.sprites[0].alpha = envelope;
            sLeaser.sprites[0].color = Color.Lerp(Color.white, Gold, 0.65f);

            sLeaser.sprites[1].x = drawPos.x;
            sLeaser.sprites[1].y = drawPos.y;
            sLeaser.sprites[1].scale = Mathf.Lerp(1.1f, 3.8f, t);
            sLeaser.sprites[1].alpha = envelope * 0.14f;
            sLeaser.sprites[1].color = Gold;

            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void ApplyPalette(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            RoomPalette palette)
        {
        }
    }

    private sealed class KarmicGlyphPulse : CosmeticSprite
    {
        private const int Lifetime = 24;

        private readonly PhysicalObject _source;
        private readonly int _karmaLevel;
        private readonly float _maxRadius;
        private int _age;

        internal KarmicGlyphPulse(
            PhysicalObject source,
            Vector2 position,
            int karmaLevel,
            float maxRadius)
        {
            _source = source;
            _karmaLevel = karmaLevel;
            _maxRadius = maxRadius;
            pos = position;
            lastPos = position;
        }

        public override void Update(bool eu)
        {
            base.Update(eu);
            _age++;
            if (_source != null && !_source.slatedForDeletetion && _source.room == room)
            {
                pos = Vector2.Lerp(pos, _source.firstChunk.pos, 0.12f);
            }

            if (_age >= Lifetime)
            {
                Destroy();
            }
        }

        public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
        {
            sLeaser.sprites = new FSprite[2];
            sLeaser.sprites[0] = new FSprite("Futile_White")
            {
                shader = rCam.room.game.rainWorld.Shaders["VectorCircle"]
            };
            sLeaser.sprites[1] = new FSprite(KarmaGlyph(_karmaLevel));
            AddToContainer(sLeaser, rCam, rCam.ReturnFContainer("GrabShaders"));
        }

        public override void DrawSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            float timeStacker,
            Vector2 camPos)
        {
            float t = Mathf.Clamp01((_age + timeStacker) / Lifetime);
            float fade = 1f - t;
            Vector2 drawPos = Vector2.Lerp(lastPos, pos, timeStacker) - camPos;
            float radius = Mathf.Lerp(12f, _maxRadius, Custom.SCurve(t, 0.6f));

            sLeaser.sprites[0].x = drawPos.x;
            sLeaser.sprites[0].y = drawPos.y;
            sLeaser.sprites[0].scale = radius / 8f;
            sLeaser.sprites[0].alpha = Mathf.Lerp(0.16f, 0f, t);
            sLeaser.sprites[0].color = Gold;

            sLeaser.sprites[1].x = drawPos.x;
            sLeaser.sprites[1].y = drawPos.y;
            sLeaser.sprites[1].scale = Mathf.Lerp(0.7f, 1.2f, t);
            sLeaser.sprites[1].alpha = fade * fade * 0.8f;
            sLeaser.sprites[1].color = Color.Lerp(Color.white, Gold, 0.55f);

            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void ApplyPalette(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            RoomPalette palette)
        {
        }
    }

    private sealed class KarmicSpearAfterimage : CosmeticSprite
    {
        private const int Lifetime = 14;

        private readonly Vector2 _rotation;
        private readonly int _karmaLevel;
        private int _age;

        internal KarmicSpearAfterimage(Vector2 position, Vector2 rotation, int karmaLevel)
        {
            pos = position + Random.insideUnitCircle * 0.8f;
            lastPos = pos;
            _rotation = rotation.sqrMagnitude > 0.001f ? rotation.normalized : Vector2.right;
            _karmaLevel = karmaLevel;
        }

        public override void Update(bool eu)
        {
            base.Update(eu);
            _age++;
            if (_age >= Lifetime)
            {
                Destroy();
            }
        }

        public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
        {
            sLeaser.sprites = new FSprite[1];
            sLeaser.sprites[0] = new FSprite("SmallSpear");
            AddToContainer(sLeaser, rCam, rCam.ReturnFContainer("GrabShaders"));
        }

        public override void DrawSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            float timeStacker,
            Vector2 camPos)
        {
            float t = Mathf.Clamp01((_age + timeStacker) / Lifetime);
            Vector2 drawPos = Vector2.Lerp(lastPos, pos, timeStacker) - camPos;
            FSprite sprite = sLeaser.sprites[0];
            sprite.x = drawPos.x;
            sprite.y = drawPos.y;
            sprite.rotation = Custom.AimFromOneVectorToAnother(Vector2.zero, _rotation);
            sprite.alpha = (1f - t) * 0.28f;
            sprite.color = Color.Lerp(Gold, Color.white, _karmaLevel / 24f);
            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void ApplyPalette(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            RoomPalette palette)
        {
        }
    }
}
