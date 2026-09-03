using Noise;
using RWCustom;
using UnityEngine;
using Watcher;

namespace DryCycle.PlayerAbility.SlugCatKarmicArmor;

/// <summary>
/// Player-owned copy of Watcher's KarmicArmor presentation. The runtime state owns
/// the level countdown; this object mirrors the original shield rendering, ticks,
/// Templar circles, projectile feedback, and final karmic shockwave.
/// </summary>
internal sealed class SlugCatKarmicArmorVisual : UpdatableAndDeletable, IDrawable
{
    private readonly Player _sourcePlayer;
    private readonly PlayerKarmicArmorState _state;
    private readonly SoundID[] _tickSounds =
    {
        WatcherEnums.WatcherSoundID.Templar_Shield_Tick_1,
        WatcherEnums.WatcherSoundID.Templar_Shield_Tick_2,
        WatcherEnums.WatcherSoundID.Templar_Shield_Tick_3,
        WatcherEnums.WatcherSoundID.Templar_Shield_Tick_4,
        WatcherEnums.WatcherSoundID.Templar_Shield_Tick_5,
        WatcherEnums.WatcherSoundID.Templar_Shield_Tick_6,
        WatcherEnums.WatcherSoundID.Templar_Shield_Tick_7,
        WatcherEnums.WatcherSoundID.Templar_Shield_Tick_8,
        WatcherEnums.WatcherSoundID.Templar_Shield_Tick_9
    };

    private int _displayedKarma;
    private float _shakeAmount;
    private float _lastShakeAmount;
    private Vector2 _position;
    private Vector2 _lastPosition;
    private float _alpha;
    private float _lastAlpha;
    private bool _exploded;

    internal SlugCatKarmicArmorVisual(Player sourcePlayer, PlayerKarmicArmorState state)
    {
        _sourcePlayer = sourcePlayer;
        _state = state;
        _displayedKarma = state.KarmaLevels;
        Radius = 40f;
        _position = sourcePlayer.firstChunk.pos;
        _lastPosition = _position;
    }

    private bool ExplosionImminent => _displayedKarma == 0;

    private bool IsTicking => _state.Triggered;

    internal float Radius { get; }

    public void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        sLeaser.sprites = new FSprite[1];
        Texture2D texture = HeavyTexturesCache.LoadMiscTexture(
            AssetManager.ResolveFilePath("illustrations/bigkarma.png"),
            TextureWrapMode.Clamp,
            FilterMode.Bilinear);
        Shader.SetGlobalTexture("_BigKarmaTex", texture);
        sLeaser.sprites[0] = new FSprite("Futile_White")
        {
            shader = rCam.room.game.rainWorld.Shaders["KarmicShield"]
        };
        AddToContainer(sLeaser, rCam, null);
    }

    public void AddToContainer(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        FContainer container)
    {
        container ??= rCam.ReturnFContainer("GrabShaders");
        foreach (FSprite sprite in sLeaser.sprites)
        {
            container.AddChild(sprite);
        }
    }

    public void DrawSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        float shake = Mathf.Lerp(_lastShakeAmount, _shakeAmount, timeStacker);
        sLeaser.sprites[0].x = Mathf.Lerp(_lastPosition.x, _position.x, timeStacker) - camPos.x;
        sLeaser.sprites[0].y = Mathf.Lerp(_lastPosition.y, _position.y, timeStacker) - camPos.y;
        sLeaser.sprites[0].scale = Radius / 8f * 1.2f;
        sLeaser.sprites[0].color = new Color(
            _displayedKarma / 10f,
            shake,
            0f,
            Mathf.Lerp(_lastAlpha, _alpha, timeStacker));

        if (!sLeaser.deleteMeNextFrame && (slatedForDeletetion || room != rCam.room))
        {
            sLeaser.CleanSpritesAndRemove();
        }
    }

    public void ApplyPalette(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        RoomPalette palette)
    {
    }

    public override void Update(bool eu)
    {
        _lastPosition = _position;
        _lastShakeAmount = _shakeAmount;
        _lastAlpha = _alpha;

        if (IsTicking)
        {
            _alpha = 1f;
        }
        else
        {
            _alpha = Custom.LerpAndTick(_alpha, 0.05f, 0.01f, 0.01f);
        }

        if (_shakeAmount > 0f && !ExplosionImminent)
        {
            _shakeAmount = Mathf.Max(_shakeAmount - 0.01f, 0f);
        }
        else if (ExplosionImminent)
        {
            _shakeAmount = Mathf.Min(_shakeAmount + 0.015f, 1f);
            if (_shakeAmount >= 1f)
            {
                Explode();
            }
        }

        if (_displayedKarma != _state.KarmaLevels)
        {
            room.PlaySound(_tickSounds[Custom.IntClamp(0, 8, _displayedKarma - 1)]);
            _displayedKarma = _state.KarmaLevels;
            _shakeAmount = Mathf.Clamp01(
                _shakeAmount + (ExplosionImminent ? 0.5f : 0.6f));

            if (_displayedKarma > 0)
            {
                room.AddObject(new TemplarCircle(
                    _sourcePlayer,
                    _position,
                    Radius,
                    7f,
                    0f,
                    25,
                    true)
                {
                    radDamping = 0.1f
                });
            }
        }

        if (_sourcePlayer.slatedForDeletetion || _sourcePlayer.room != room)
        {
            DestroyAndDetach();
            return;
        }

        _position = _sourcePlayer.firstChunk.pos;
        base.Update(eu);
    }

    internal void DeflectedProjectile(Vector2 hitPosition)
    {
        if (room == null)
        {
            return;
        }

        room.PlaySound(WatcherEnums.WatcherSoundID.Templar_Shield_Deflect);
        Vector2 normal = (hitPosition - _position).normalized;
        if (normal.sqrMagnitude < 0.001f)
        {
            normal = Custom.RNV();
        }

        Vector2 tangent = Custom.PerpendicularVector(normal);
        hitPosition = _position + normal * Radius;
        int sparkCount = Random.Range(10, 20);
        for (int i = 0; i < sparkCount; i++)
        {
            SpawnSpark(
                hitPosition,
                Custom.RNV() * 6f +
                tangent * Random.value * 10f +
                normal * Random.value * 5f);
        }

        SpawnStrand(normal, Random.Range(30f, 45f), 1f);
    }

    private void Explode()
    {
        if (_exploded || room == null)
        {
            return;
        }

        _exploded = true;
        room.PlaySound(WatcherEnums.WatcherSoundID.Templar_Shield_Explode);
        room.InGameNoise(new InGameNoise(_position, 800f, _sourcePlayer, 1f));
        room.ScreenMovement(_position, Vector2.zero, 0.9f);
        room.AddObject(new KarmicShockwave(_sourcePlayer, _position, 20, 15f, 300f));

        for (int i = 0; i < 30; i++)
        {
            Vector2 normal = Custom.RNV();
            Vector2 tangent = Custom.PerpendicularVector(normal);
            SpawnSpark(
                normal * Radius + _position,
                Custom.RNV() * 6f +
                tangent * Random.value * 5f +
                normal * Random.value * 5f);
        }

        int strandCount = Random.Range(8, 12);
        for (int i = 0; i < strandCount; i++)
        {
            SpawnStrand(
                Custom.RNV(),
                Random.Range(45f, 100f),
                Random.value * 2f + 0.5f);
        }

        room.AddObject(new Explosion.ExplosionLight(
            _position,
            200f,
            1f,
            5,
            RainWorld.SaturatedGold));
        room.AddObject(new ExplosionSpikes(
            room,
            _position,
            12,
            Radius * 0.4f,
            9f,
            6f,
            100f,
            RainWorld.SaturatedGold));

        DestroyAndDetach();
    }

    private Spark SpawnSpark(Vector2 position, Vector2 velocity)
    {
        if (room.GetTile(position).Solid)
        {
            return null;
        }

        Spark spark = new(
            position,
            velocity,
            Color.Lerp(RainWorld.SaturatedGold, Color.white, Random.value),
            null,
            20,
            30);
        room.AddObject(spark);
        return spark;
    }

    private EnergyStrand SpawnStrand(Vector2 direction, float degrees, float force)
    {
        float startAngle = Custom.VecToDeg(direction) - degrees / 2f;
        float endAngle = startAngle + degrees;
        EnergyStrand strand = new(
            Mathf.Max(Mathf.RoundToInt(degrees / 3f), 5),
            Random.Range(1f, 2f));

        for (int i = 0; i < strand.Segments.Length; i++)
        {
            float progress = (float)i / (strand.Segments.Length - 1);
            Vector2 radialDirection = Custom.DegToVec(
                Mathf.Lerp(startAngle, endAngle, progress));
            strand.Segments[i].Reset(radialDirection * Radius + _position);
            strand.Segments[i].vel =
                _position - _lastPosition +
                radialDirection * (2f + Mathf.Sin(progress * Mathf.PI)) * force +
                Random.insideUnitCircle * 2f;
        }

        strand.UpdateConnectionLengths(1.1f);
        room.AddObject(strand);
        return strand;
    }

    private void DestroyAndDetach()
    {
        _state.DetachArmor(this);
        SlugCatKarmicArmorRuntime.NotifyVisualDestroyed(this);
        Destroy();
    }

    internal void DestroyFromRuntime()
    {
        DestroyAndDetach();
    }

    private sealed class EnergyStrand : CosmeticSprite
    {
        private readonly float[] _connectionLengths;
        private readonly float _thickness;
        private readonly Color _color;
        private readonly int _lifetime;
        private int _age;

        internal EnergyStrand(int segmentCount, float thickness)
        {
            Segments = new SimpleSegment[segmentCount];
            _thickness = thickness;
            _lifetime = Random.Range(30, 80);
            _connectionLengths = new float[segmentCount - 1];
            for (int i = 0; i < _connectionLengths.Length; i++)
            {
                _connectionLengths[i] = 5f;
            }

            _color = Color.Lerp(RainWorld.SaturatedGold, Color.white, Random.value);
        }

        internal SimpleSegment[] Segments { get; }

        internal void UpdateConnectionLengths(float factor)
        {
            for (int i = 0; i < _connectionLengths.Length; i++)
            {
                _connectionLengths[i] =
                    Vector2.Distance(Segments[i].pos, Segments[i + 1].pos) * factor;
            }
        }

        public override void Update(bool eu)
        {
            base.Update(eu);
            _age++;
            if (_age >= _lifetime)
            {
                Destroy();
            }

            for (int i = 0; i < Segments.Length; i++)
            {
                Segments[i].lastPos = Segments[i].pos;
            }

            for (int i = 0; i < Segments.Length; i++)
            {
                Segments[i].vel *= 0.97f;
                Segments[i].vel += 0.1f * GetWind(Segments[i].pos, room.game.clock);
                if (i > 0)
                {
                    ConnectSegments(i - 1, i);
                }

                if (i < Segments.Length - 1)
                {
                    ConnectSegments(i, i + 1);
                }
            }

            for (int i = 0; i < Segments.Length; i++)
            {
                Segments[i].pos += Segments[i].vel;
            }
        }

        public override void InitiateSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam)
        {
            sLeaser.sprites = new FSprite[1];
            TriangleMesh mesh = TriangleMesh.MakeLongMesh(
                Segments.Length,
                pointyTip: false,
                customColor: false);
            mesh.shader = rCam.game.rainWorld.Shaders["FlatLight"];

            for (int i = 0; i < Segments.Length - 1; i++)
            {
                float start = (float)i / (Segments.Length - 1);
                float end = (float)(i + 1) / (Segments.Length - 1);
                mesh.UVvertices[i * 4] = new Vector2(start, 0f);
                mesh.UVvertices[i * 4 + 1] = new Vector2(start, 1f);
                mesh.UVvertices[i * 4 + 2] = new Vector2(end, 0f);
                mesh.UVvertices[i * 4 + 3] = new Vector2(end, 1f);
            }

            sLeaser.sprites[0] = mesh;
            AddToContainer(sLeaser, rCam, rCam.ReturnFContainer("GrabShaders"));
        }

        public override void DrawSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            float timeStacker,
            Vector2 camPos)
        {
            TriangleMesh mesh = (TriangleMesh)sLeaser.sprites[0];
            Vector2 previous = Segments[0].DrawPos(timeStacker);
            float previousWidth = 0f;
            float life = Mathf.Clamp01(((_lifetime - _age) - timeStacker) / _lifetime);

            for (int i = 0; i < Segments.Length; i++)
            {
                float progress = (float)i / (Segments.Length - 1);
                Vector2 current = Segments[i].DrawPos(timeStacker);
                Vector2 perpendicular = Custom.PerpendicularVector(
                    (current - previous).normalized);
                float width = _thickness / 2f * life * Mathf.Sin(progress * Mathf.PI);

                mesh.MoveVertice(i * 4, previous + perpendicular * previousWidth - camPos);
                mesh.MoveVertice(i * 4 + 1, previous - perpendicular * previousWidth - camPos);
                mesh.MoveVertice(i * 4 + 2, current + perpendicular * width - camPos);
                mesh.MoveVertice(i * 4 + 3, current - perpendicular * width - camPos);
                previous = current;
                previousWidth = width;
            }

            mesh.alpha = life * Mathf.Sqrt(Random.value);
            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void ApplyPalette(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            RoomPalette palette)
        {
            sLeaser.sprites[0].color = _color;
        }

        private static Vector2 GetWind(Vector2 position, int time)
        {
            float offset = time / 40f * 0.5f;
            return new Vector2(
                (Mathf.PerlinNoise(position.x * 0.02f + offset, position.y * 0.02f) -
                 0.46535593f) * 2f,
                (Mathf.PerlinNoise(
                     position.x * 0.02f + offset,
                     position.y * 0.02f + 200f) - 0.46535593f) * 2f);
        }

        private void ConnectSegments(int a, int b)
        {
            Vector2 delta = Segments[b].pos - Segments[a].pos;
            float distance = delta.magnitude;
            if (distance <= 0.01f)
            {
                return;
            }

            Vector2 correction =
                delta / distance * ((distance - _connectionLengths[a]) * 0.1f);
            Segments[a].vel += correction;
            Segments[b].vel -= correction;
        }
    }
}
