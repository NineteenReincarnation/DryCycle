using System;
using UnityEngine;

namespace DryCycle.Weather.Foehn;

/// <summary>
/// Camera-local visual carrier for otherwise invisible hot wind. The pool is dense on
/// purpose: far hairline streaks establish direction, mid particles expose gust/wake
/// motion, and a small near layer provides occasional large foreground sweeps. Particles
/// are recycled around the camera rather than diluted across an entire multi-screen room.
/// </summary>
internal sealed class FoehnParticleField
{
    internal const int ParticleCount = 360;

    private const int FarLayer = 0;
    private const int MidLayer = 1;
    private const int NearLayer = 2;

    private sealed class Particle
    {
        internal Vector2 Position;
        internal Vector2 LastPosition;
        internal Vector2 Velocity;
        internal float Life;
        internal float MaxLife;
        internal float Width;
        internal float BaseLength;
        internal float Alpha;
        internal float Depth;
        internal float Phase;
        internal float Gust;
        internal int Layer;
        internal bool Active;
    }

    private readonly Particle[] _particles = new Particle[ParticleCount];
    private readonly System.Random _random;
    private readonly float _roomWidth;
    private readonly float _roomHeight;
    private bool _primed;

    internal FoehnParticleField(Room room)
    {
        _roomWidth = Mathf.Max(20f, (room?.TileWidth ?? 1) * 20f);
        _roomHeight = Mathf.Max(20f, (room?.TileHeight ?? 1) * 20f);
        _random = new System.Random(BuildSeed(room));

        for (int i = 0; i < _particles.Length; i++)
        {
            _particles[i] = new Particle();
        }
    }

    internal void Update(
        Room room,
        float intensity,
        Vector2 windDirection,
        FoehnTerrainField terrainField,
        float visualTime,
        float gustSeed,
        Vector2 cameraPos,
        Vector2 cameraSize)
    {
        float drive = Mathf.Clamp01(intensity);
        if (drive <= 0.0001f)
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                _particles[i].Active = false;
            }
            _primed = false;
            return;
        }

        Vector2 forward = SafeNormalize(windDirection);
        Vector2 cross = new(-forward.y, forward.x);
        float buffer = Mathf.Lerp(185f, 265f, drive);
        Rect viewBounds = BuildViewBounds(cameraPos, cameraSize, buffer);

        Vector2 cameraCenter = cameraPos + cameraSize * 0.5f;
        FoehnGustSample cameraGust = FoehnGustField.Sample(
            cameraCenter,
            visualTime,
            drive,
            forward,
            gustSeed);
        int targetActive = Mathf.Clamp(
            Mathf.RoundToInt(
                Mathf.Lerp(118f, 326f, Mathf.Pow(drive, 0.62f)) +
                cameraGust.Body * 13f +
                cameraGust.Front * 28f),
            0,
            ParticleCount);

        int activeCount = 0;
        for (int i = 0; i < _particles.Length; i++)
        {
            Particle particle = _particles[i];
            if (!particle.Active)
            {
                continue;
            }

            activeCount++;
            particle.LastPosition = particle.Position;
            particle.Life -= 1f;

            FoehnTerrainSample terrain =
                terrainField?.Sample(particle.Position) ?? FoehnTerrainSample.OpenAir;
            FoehnGustSample gust = FoehnGustField.Sample(
                particle.Position,
                visualTime,
                drive,
                forward,
                gustSeed);

            float localExposure = Mathf.Lerp(0.34f, 1f, terrain.Exposure);
            float nozzle = terrain.Nozzle;
            float wake = terrain.Wake;
            float edge = terrain.Edge;
            float layerSpeed = particle.Layer switch
            {
                FarLayer => 1.08f,
                MidLayer => 0.98f,
                _ => 1.18f
            };

            float speed = Mathf.Lerp(8.4f, 18.8f, drive) *
                          layerSpeed *
                          (0.70f + gust.Body * 0.48f + gust.Front * 0.66f) *
                          Mathf.Lerp(0.82f, 1.38f, nozzle) *
                          localExposure;

            float waveA = Mathf.Sin(visualTime * 4.1f + particle.Phase * 17.13f);
            float waveB = Mathf.Sin(visualTime * 8.7f + particle.Phase * 31.71f);
            float wakeCurl = wake *
                             (waveA * 1.35f + waveB * 0.58f) *
                             (0.48f + gust.Turbulence * 0.74f);
            float edgeFlutter = edge * waveB * (0.36f + gust.Front * 0.54f);
            float frontKick = gust.Front * waveA * 0.72f;

            Vector2 targetVelocity =
                forward * speed +
                cross * (wakeCurl + edgeFlutter + frontKick);
            particle.Velocity = Vector2.Lerp(
                particle.Velocity,
                targetVelocity,
                0.18f + drive * 0.07f + gust.Front * 0.08f);
            particle.Position += particle.Velocity;
            particle.Gust = Mathf.Clamp01(gust.Body * 0.66f + gust.Front * 0.92f);

            if (particle.Life <= 0f || !Contains(viewBounds, particle.Position, 18f))
            {
                particle.Active = false;
                activeCount--;
            }
        }

        if (!_primed && activeCount >= targetActive * 0.82f)
        {
            _primed = true;
        }

        int deficit = Mathf.Max(0, targetActive - activeCount);
        int spawnBudget = Mathf.Min(_primed ? 28 : 112, deficit);
        bool fillInsideView = !_primed || activeCount < targetActive * 0.56f;
        for (int spawn = 0; spawn < spawnBudget; spawn++)
        {
            int slot = FindInactiveSlot();
            if (slot < 0)
            {
                break;
            }

            Spawn(
                _particles[slot],
                room,
                drive,
                forward,
                cross,
                terrainField,
                visualTime,
                gustSeed,
                viewBounds,
                fillInsideView);
        }
    }

    internal void Draw(
        FSprite[] sprites,
        int spriteOffset,
        float timeStacker,
        Vector2 camPos,
        float intensity,
        Vector2 windDirection,
        FoehnTerrainField terrainField)
    {
        if (sprites == null)
        {
            return;
        }

        float drive = Mathf.Clamp01(intensity);

        for (int i = 0; i < _particles.Length; i++)
        {
            int spriteIndex = spriteOffset + i;
            if (spriteIndex < 0 || spriteIndex >= sprites.Length)
            {
                break;
            }

            FSprite sprite = sprites[spriteIndex];
            Particle particle = _particles[i];
            if (sprite == null || !particle.Active || drive <= 0.0001f)
            {
                if (sprite != null)
                {
                    sprite.isVisible = false;
                }
                continue;
            }

            Vector2 position = Vector2.Lerp(
                particle.LastPosition,
                particle.Position,
                timeStacker);
            FoehnTerrainSample terrain =
                terrainField?.Sample(position) ?? FoehnTerrainSample.OpenAir;

            float spawnFade = Mathf.Clamp01(
                (particle.MaxLife - particle.Life) /
                (particle.Layer == FarLayer ? 5f : 8f));
            float deathFade = Mathf.Clamp01(particle.Life / 13f);
            float lifeFade = Mathf.Min(spawnFade, deathFade);
            float visibility =
                lifeFade *
                particle.Alpha *
                drive *
                Mathf.Lerp(0.54f, 1f, terrain.Exposure) *
                Mathf.Lerp(0.82f, 1.34f, particle.Gust);

            float speed = particle.Velocity.magnitude;
            float lengthFactor = Mathf.Lerp(
                0.82f,
                1.62f,
                Mathf.InverseLerp(6f, 24f, speed));
            float length = particle.BaseLength *
                           lengthFactor *
                           Mathf.Lerp(0.94f, 1.30f, particle.Gust);

            sprite.SetPosition(position - camPos);
            sprite.rotation = Mathf.Atan2(
                particle.Velocity.y,
                particle.Velocity.x) * Mathf.Rad2Deg;
            sprite.scaleX = Mathf.Max(2f, length);
            sprite.scaleY = particle.Width;
            sprite.alpha = Mathf.Clamp01(visibility);

            float near = 1f - particle.Depth;
            Color farColor = new(0.58f, 0.43f, 0.25f);
            Color nearColor = new(0.98f, 0.81f, 0.47f);
            sprite.color = Color.Lerp(
                farColor,
                nearColor,
                Mathf.Lerp(0.22f, 0.90f, near));
            sprite.isVisible = sprite.alpha > 0.010f;
        }
    }

    internal static void Hide(FSprite[] sprites, int spriteOffset)
    {
        if (sprites == null)
        {
            return;
        }

        for (int i = 0; i < ParticleCount; i++)
        {
            int index = spriteOffset + i;
            if (index >= 0 && index < sprites.Length && sprites[index] != null)
            {
                sprites[index].isVisible = false;
            }
        }
    }

    private void Spawn(
        Particle particle,
        Room room,
        float intensity,
        Vector2 forward,
        Vector2 cross,
        FoehnTerrainField terrainField,
        float visualTime,
        float gustSeed,
        Rect viewBounds,
        bool fillInsideView)
    {
        int layer = ChooseLayer();
        Vector2 position = Vector2.zero;
        FoehnTerrainSample terrain = FoehnTerrainSample.OpenAir;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            position = fillInsideView
                ? RandomPointInView(viewBounds)
                : RandomPointOnUpwindEdge(viewBounds, forward, cross);
            position = ClampToRoom(position, 55f);
            terrain = terrainField?.Sample(position) ?? FoehnTerrainSample.OpenAir;

            float preference =
                0.16f +
                terrain.Exposure * 0.62f +
                terrain.Nozzle * 0.36f +
                terrain.Wake * 0.14f;
            if (_random.NextDouble() <= Mathf.Clamp01(preference))
            {
                break;
            }
        }

        FoehnGustSample gust = FoehnGustField.Sample(
            position,
            visualTime,
            intensity,
            forward,
            gustSeed);

        float layerSpeed = layer switch
        {
            FarLayer => 1.08f,
            MidLayer => 0.98f,
            _ => 1.18f
        };
        float speed = Mathf.Lerp(8.2f, 18.4f, intensity) *
                      layerSpeed *
                      Mathf.Lerp(0.82f, 1.24f, (float)_random.NextDouble()) *
                      (0.72f + gust.Body * 0.46f + gust.Front * 0.58f) *
                      Mathf.Lerp(0.82f, 1.34f, terrain.Nozzle);
        float lateral = ((float)_random.NextDouble() * 2f - 1f) *
                        Mathf.Lerp(
                            0.18f,
                            1.95f,
                            Mathf.Clamp01(terrain.Wake + terrain.Edge * 0.62f));

        particle.Position = position;
        particle.LastPosition = position - forward * speed;
        particle.Velocity = forward * speed + cross * lateral;
        particle.MaxLife = Mathf.Lerp(54f, 170f, (float)_random.NextDouble());
        particle.Life = particle.MaxLife;
        particle.Phase = (float)_random.NextDouble();
        particle.Layer = layer;
        particle.Gust = Mathf.Clamp01(gust.Body * 0.66f + gust.Front * 0.92f);

        switch (layer)
        {
            case FarLayer:
                particle.Width = Mathf.Lerp(0.26f, 0.62f, (float)_random.NextDouble());
                particle.BaseLength = Mathf.Lerp(8f, 24f, (float)_random.NextDouble());
                particle.Alpha = Mathf.Lerp(0.12f, 0.34f, (float)_random.NextDouble());
                particle.Depth = Mathf.Lerp(0.72f, 1f, (float)_random.NextDouble());
                break;

            case MidLayer:
                particle.Width = Mathf.Lerp(0.52f, 1.16f, (float)_random.NextDouble());
                particle.BaseLength = Mathf.Lerp(10f, 34f, (float)_random.NextDouble());
                particle.Alpha = Mathf.Lerp(0.22f, 0.60f, (float)_random.NextDouble());
                particle.Depth = Mathf.Lerp(0.30f, 0.74f, (float)_random.NextDouble());
                break;

            default:
                particle.Width = Mathf.Lerp(1.05f, 2.05f, (float)_random.NextDouble());
                particle.BaseLength = Mathf.Lerp(17f, 48f, (float)_random.NextDouble());
                particle.Alpha = Mathf.Lerp(0.30f, 0.78f, (float)_random.NextDouble());
                particle.Depth = Mathf.Lerp(0f, 0.30f, (float)_random.NextDouble());
                break;
        }

        particle.Active = true;
    }

    private int ChooseLayer()
    {
        double roll = _random.NextDouble();
        if (roll < 0.57)
        {
            return FarLayer;
        }

        if (roll < 0.91)
        {
            return MidLayer;
        }

        return NearLayer;
    }

    private Vector2 RandomPointInView(Rect viewBounds)
    {
        return new Vector2(
            Mathf.Lerp(viewBounds.xMin, viewBounds.xMax, (float)_random.NextDouble()),
            Mathf.Lerp(viewBounds.yMin, viewBounds.yMax, (float)_random.NextDouble()));
    }

    private Vector2 RandomPointOnUpwindEdge(
        Rect viewBounds,
        Vector2 forward,
        Vector2 cross)
    {
        Vector2 center = viewBounds.center;
        float halfAlong =
            Mathf.Abs(forward.x) * viewBounds.width * 0.5f +
            Mathf.Abs(forward.y) * viewBounds.height * 0.5f;
        float halfCross =
            Mathf.Abs(cross.x) * viewBounds.width * 0.5f +
            Mathf.Abs(cross.y) * viewBounds.height * 0.5f;
        float lane = Mathf.Lerp(-halfCross, halfCross, (float)_random.NextDouble());
        Vector2 position =
            center - forward * Mathf.Max(20f, halfAlong - 14f) + cross * lane;
        position.x = Mathf.Clamp(position.x, viewBounds.xMin, viewBounds.xMax);
        position.y = Mathf.Clamp(position.y, viewBounds.yMin, viewBounds.yMax);
        return position;
    }

    private Rect BuildViewBounds(Vector2 cameraPos, Vector2 cameraSize, float buffer)
    {
        float minX = Mathf.Max(-55f, cameraPos.x - buffer);
        float minY = Mathf.Max(-55f, cameraPos.y - buffer);
        float maxX = Mathf.Min(_roomWidth + 55f, cameraPos.x + cameraSize.x + buffer);
        float maxY = Mathf.Min(_roomHeight + 55f, cameraPos.y + cameraSize.y + buffer);

        if (maxX <= minX)
        {
            maxX = minX + 20f;
        }
        if (maxY <= minY)
        {
            maxY = minY + 20f;
        }

        return Rect.MinMaxRect(minX, minY, maxX, maxY);
    }

    private Vector2 ClampToRoom(Vector2 position, float margin)
    {
        return new Vector2(
            Mathf.Clamp(position.x, -margin, _roomWidth + margin),
            Mathf.Clamp(position.y, -margin, _roomHeight + margin));
    }

    private int FindInactiveSlot()
    {
        int start = _random.Next(_particles.Length);
        for (int i = 0; i < _particles.Length; i++)
        {
            int index = (start + i) % _particles.Length;
            if (!_particles[index].Active)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool Contains(Rect bounds, Vector2 position, float margin)
    {
        return position.x >= bounds.xMin - margin &&
               position.x <= bounds.xMax + margin &&
               position.y >= bounds.yMin - margin &&
               position.y <= bounds.yMax + margin;
    }

    private static Vector2 SafeNormalize(Vector2 value)
    {
        return value.sqrMagnitude > 0.0001f
            ? value.normalized
            : new Vector2(1f, -0.16f).normalized;
    }

    private static int BuildSeed(Room room)
    {
        unchecked
        {
            uint hash = 2166136261u;
            string name = room?.abstractRoom?.name ?? room?.world?.region?.name ?? "Foehn";
            for (int i = 0; i < name.Length; i++)
            {
                hash ^= char.ToUpperInvariant(name[i]);
                hash *= 16777619u;
            }

            hash ^= (uint)(room?.TileWidth ?? 0);
            hash *= 16777619u;
            hash ^= (uint)(room?.TileHeight ?? 0);
            return (int)hash;
        }
    }
}
