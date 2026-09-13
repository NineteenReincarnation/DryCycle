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

        // Collapse several strands at once so the completed transmutation reads as
        // the player's reinforced karma being physically pulled into the weapon.
        for (int i = 0; i < 5; i++)
        {
            player.room.AddObject(new KarmicTransferGlyph(player, target, karmaLevel));
        }

        SpawnChargePulse(player, 1f);
        SpawnSparks(player.room, player.mainBodyChunk.pos, 8, 3.2f);
        SpawnImpactPulse(target, target.firstChunk.pos, karmaLevel, 52f);
    }

    internal static void SpawnChargingConvergence(
        Player player,
        PhysicalObject target,
        int karmaLevel,
        float progress)
    {
        if (player?.room == null || target == null || target.room != player.room)
        {
            return;
        }

        progress = Mathf.Clamp01(progress);
        player.room.AddObject(new KarmicChargeAura(player, target, karmaLevel, progress));

        int particleCount = Mathf.Clamp(1 + Mathf.RoundToInt(progress * 2f), 1, 3);
        for (int i = 0; i < particleCount; i++)
        {
            player.room.AddObject(new KarmicConvergingParticle(player, target, progress));
        }

        if (Random.value < Mathf.Lerp(0.35f, 0.9f, progress))
        {
            player.room.AddObject(new KarmicConvergingGlyph(player, target, karmaLevel, progress));
        }
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
        return global::HUD.KarmaMeter.KarmaSymbolSprite(
            small: false,
            new IntVector2(value, value));
    }

    private sealed class KarmicChargeAura : CosmeticSprite
    {
        private const int Lifetime = 15;

        private readonly Player _player;
        private readonly PhysicalObject _target;
        private readonly int _karmaLevel;
        private readonly float _progress;
        private readonly Vector2 _orbitDir;
        private readonly float _orbitRadius;
        private readonly float _phase;
        private int _age;

        internal KarmicChargeAura(
            Player player,
            PhysicalObject target,
            int karmaLevel,
            float progress)
        {
            _player = player;
            _target = target;
            _karmaLevel = karmaLevel;
            _progress = Mathf.Clamp01(progress);
            _orbitDir = Custom.RNV();
            _orbitRadius = Mathf.Lerp(12f, 24f, _progress) + Random.value * 8f;
            _phase = Random.value * Mathf.PI * 2f;
            pos = player.mainBodyChunk.pos;
            lastPos = pos;
        }

        public override void Update(bool eu)
        {
            base.Update(eu);
            _age++;

            if (_player == null || _target == null || _player.room != room || _target.room != room ||
                _player.slatedForDeletetion || _target.slatedForDeletetion || _age >= Lifetime)
            {
                Destroy();
                return;
            }

            float t = Mathf.Clamp01((float)_age / Lifetime);
            Vector2 center = _player.mainBodyChunk.pos + Vector2.up * Mathf.Lerp(8f, 15f, _progress);
            float angle = _phase + t * Mathf.PI * 1.75f;
            Vector2 tangent = new(-_orbitDir.y, _orbitDir.x);
            Vector2 orbitOffset = (_orbitDir * Mathf.Cos(angle) + tangent * Mathf.Sin(angle)) * _orbitRadius;
            pos = center + orbitOffset;
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
            Vector2 drawPos = Vector2.Lerp(lastPos, pos, timeStacker) - camPos;
            float t = Mathf.Clamp01((_age + timeStacker) / Lifetime);
            float envelope = Mathf.Sin(t * Mathf.PI);

            sLeaser.sprites[0].x = drawPos.x;
            sLeaser.sprites[0].y = drawPos.y;
            sLeaser.sprites[0].scale = Mathf.Lerp(0.9f, 2.8f, _progress) * Mathf.Lerp(0.82f, 1.18f, envelope);
            sLeaser.sprites[0].alpha = envelope * Mathf.Lerp(0.08f, 0.18f, _progress);
            sLeaser.sprites[0].color = Gold;

            sLeaser.sprites[1].x = drawPos.x;
            sLeaser.sprites[1].y = drawPos.y;
            sLeaser.sprites[1].scale = Mathf.Lerp(0.12f, 0.25f, _progress);
            sLeaser.sprites[1].alpha = envelope * Mathf.Lerp(0.4f, 0.82f, _progress);
            sLeaser.sprites[1].color = Color.Lerp(Color.white, Gold, 0.58f);

            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
        {
        }
    }

    private sealed class KarmicConvergingParticle : CosmeticSprite
    {
        private readonly PhysicalObject _target;
        private readonly Vector2 _start;
        private readonly Vector2 _curve;
        private readonly float _progress;
        private readonly int _lifetime;
        private int _age;

        internal KarmicConvergingParticle(Player player, PhysicalObject target, float progress)
        {
            _target = target;
            _progress = Mathf.Clamp01(progress);
            _lifetime = Mathf.RoundToInt(Mathf.Lerp(18f, 10f, _progress));

            Vector2 center = player.mainBodyChunk.pos + Vector2.up * Mathf.Lerp(7f, 13f, _progress);
            Vector2 radial = Custom.RNV();
            Vector2 tangent = new(-radial.y, radial.x);
            _start = center + radial * Random.Range(Mathf.Lerp(15f, 22f, _progress), Mathf.Lerp(24f, 38f, _progress));
            _curve = tangent * Random.Range(-12f, 12f) + Vector2.up * Random.Range(3f, 12f);
            pos = _start;
            lastPos = pos;
        }

        public override void Update(bool eu)
        {
            base.Update(eu);
            _age++;

            if (_target == null || _target.room != room || _target.slatedForDeletetion || _age >= _lifetime)
            {
                Destroy();
                return;
            }

            float t = Mathf.Clamp01((float)_age / _lifetime);
            Vector2 end = _target.firstChunk.pos;
            Vector2 control = Vector2.Lerp(_start, end, 0.48f) + _curve;
            float oneMinus = 1f - t;
            pos = oneMinus * oneMinus * _start + 2f * oneMinus * t * control + t * t * end;
        }

        public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
        {
            sLeaser.sprites = new FSprite[2];
            sLeaser.sprites[0] = new FSprite("pixel");
            sLeaser.sprites[1] = new FSprite("pixel");
            AddToContainer(sLeaser, rCam, rCam.ReturnFContainer("GrabShaders"));
        }

        public override void DrawSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            float timeStacker,
            Vector2 camPos)
        {
            Vector2 drawPos = Vector2.Lerp(lastPos, pos, timeStacker) - camPos;
            float t = Mathf.Clamp01((_age + timeStacker) / _lifetime);
            float envelope = Mathf.Sin(t * Mathf.PI);

            FSprite glow = sLeaser.sprites[0];
            glow.x = drawPos.x;
            glow.y = drawPos.y;
            glow.scale = Mathf.Lerp(2.8f, 1.1f, t) * Mathf.Lerp(0.8f, 1.25f, _progress);
            glow.alpha = envelope * Mathf.Lerp(0.14f, 0.28f, _progress);
            glow.color = Gold;

            FSprite core = sLeaser.sprites[1];
            core.x = drawPos.x;
            core.y = drawPos.y;
            core.scale = Mathf.Lerp(1.25f, 0.45f, t);
            core.alpha = envelope * Mathf.Lerp(0.55f, 0.95f, _progress);
            core.color = Color.Lerp(Color.white, Gold, 0.45f);

            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
        {
        }
    }

    private sealed class KarmicConvergingGlyph : CosmeticSprite
    {
        private const int Lifetime = 16;

        private readonly PhysicalObject _target;
        private readonly Vector2 _start;
        private readonly int _karmaLevel;
        private readonly float _progress;
        private readonly Vector2 _swirl;
        private int _age;

        internal KarmicConvergingGlyph(Player player, PhysicalObject target, int karmaLevel, float progress)
        {
            _target = target;
            _karmaLevel = karmaLevel;
            _progress = Mathf.Clamp01(progress);

            Vector2 center = player.mainBodyChunk.pos + Vector2.up * Mathf.Lerp(6f, 12f, _progress);
            Vector2 radial = Custom.RNV();
            _start = center + radial * Random.Range(Mathf.Lerp(13f, 20f, _progress), Mathf.Lerp(20f, 30f, _progress));
            _swirl = new Vector2(-radial.y, radial.x) * Random.Range(-Mathf.Lerp(7f, 13f, _progress), Mathf.Lerp(7f, 13f, _progress));
            pos = _start;
            lastPos = pos;
        }

        public override void Update(bool eu)
        {
            base.Update(eu);
            _age++;

            if (_target == null || _target.room != room || _target.slatedForDeletetion || _age >= Lifetime)
            {
                Destroy();
                return;
            }

            float t = Mathf.Clamp01((float)_age / Lifetime);
            Vector2 end = _target.firstChunk.pos;
            Vector2 control = Vector2.Lerp(_start, end, 0.45f) + _swirl + Vector2.up * Mathf.Lerp(8f, 18f, _progress);
            float oneMinus = 1f - t;
            pos = oneMinus * oneMinus * _start +
                  2f * oneMinus * t * control +
                  t * t * end;
        }

        public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
        {
            sLeaser.sprites = new FSprite[1];
            sLeaser.sprites[0] = new FSprite(KarmaGlyph(_karmaLevel));
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
            sLeaser.sprites[0].scale = Mathf.Lerp(0.2f, 0.11f, t) * Mathf.Lerp(0.75f, 1.25f, _progress);
            sLeaser.sprites[0].alpha = envelope * Mathf.Lerp(0.45f, 0.88f, _progress);
            sLeaser.sprites[0].color = Color.Lerp(Color.white, Gold, 0.7f);

            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
        {
        }
    }

    private sealed class KarmicTransferGlyph : CosmeticSprite
    {
        private const int Lifetime = 26;

        private readonly PhysicalObject _target;
        private readonly Vector2 _start;
        private readonly Vector2 _bend;
        private readonly int _karmaLevel;
        private int _age;

        internal KarmicTransferGlyph(Player player, PhysicalObject target, int karmaLevel)
        {
            _target = target;
            _karmaLevel = karmaLevel;
            Vector2 radial = Custom.RNV();
            _start = player.mainBodyChunk.pos + Vector2.up * 15f + radial * Random.Range(12f, 28f);
            _bend = new Vector2(-radial.y, radial.x) * Random.Range(-14f, 14f) + Vector2.up * Random.Range(14f, 30f);
            pos = _start;
            lastPos = pos;
        }

        public override void Update(bool eu)
        {
            base.Update(eu);
            _age++;

            if (_target == null || _target.slatedForDeletetion || _target.room != room || _age >= Lifetime)
            {
                Destroy();
                return;
            }

            float t = Mathf.Clamp01((float)_age / Lifetime);
            Vector2 end = _target.firstChunk.pos;
            Vector2 control = Vector2.Lerp(_start, end, 0.5f) + _bend;
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
            sLeaser.sprites[0].scale = Mathf.Lerp(0.42f, 0.17f, t);
            sLeaser.sprites[0].alpha = envelope;
            sLeaser.sprites[0].color = Color.Lerp(Color.white, Gold, 0.65f);

            sLeaser.sprites[1].x = drawPos.x;
            sLeaser.sprites[1].y = drawPos.y;
            sLeaser.sprites[1].scale = Mathf.Lerp(1.0f, 3.2f, t);
            sLeaser.sprites[1].alpha = envelope * 0.13f;
            sLeaser.sprites[1].color = Gold;

            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
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

        internal KarmicGlyphPulse(PhysicalObject source, Vector2 position, int karmaLevel, float maxRadius)
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

        public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
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
            sLeaser.sprites = new FSprite[2];
            sLeaser.sprites[0] = new FSprite("SmallSpear");
            sLeaser.sprites[1] = new FSprite("pixel");
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
            float rot = Custom.AimFromOneVectorToAnother(Vector2.zero, _rotation);

            FSprite spear = sLeaser.sprites[0];
            spear.x = drawPos.x;
            spear.y = drawPos.y;
            spear.rotation = rot;
            spear.alpha = (1f - t) * 0.30f;
            spear.color = Color.Lerp(Gold, Color.white, _karmaLevel / 24f);

            FSprite streak = sLeaser.sprites[1];
            streak.x = drawPos.x - _rotation.x * 10f;
            streak.y = drawPos.y - _rotation.y * 10f;
            streak.rotation = rot;
            streak.scaleX = 1.3f;
            streak.scaleY = Mathf.Lerp(18f, 4f, t);
            streak.alpha = (1f - t) * 0.16f;
            streak.color = Gold;

            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
        {
        }
    }
}
