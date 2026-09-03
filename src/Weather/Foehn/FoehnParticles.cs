using System;
using UnityEngine;

namespace DryCycle.Weather.Foehn;

/// <summary>
/// Strong visible carrier for otherwise invisible hot wind. Particles are sparse
/// elongated mineral/dust streaks, not a sandstorm veil: they expose direction,
/// gust timing, wakes and nozzle acceleration while leaving the scene readable.
/// </summary>
internal sealed class FoehnParticleField
{
    internal const int ParticleCount = 92;

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
        internal bool Active;
    }

    private readonly Particle[] _particles = new Particle[ParticleCount];
    private readonly Random _random;
    private readonly float _roomWidth;
    private readonly float _roomHeight;

    internal FoehnParticleField(Room room)
    {
        _roomWidth = Mathf.Max(20f, (room?.TileWidth ?? 1) * 20f);
        _roomHeight = Mathf.Max(20f, (room?.TileHeight ?? 1) * 20f);
        _random = new Random(BuildSeed(room));

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
        float visualTime)
    {
        float drive = Mathf.Clamp01(intensity);
        if (drive <= 0.0001f)
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                _particles[i].Active = false;
            }
            return;
        }

        Vector2 forward = SafeNormalize(windDirection);
        Vector2 cross = new(-forward.y, forward.x);
        int targetActive = Mathf.RoundToInt(Mathf.Lerp(24f, ParticleCount, Mathf.Pow(drive, 0.72f)));
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

            FoehnTerrainSample terrain = terrainField?.Sample(particle.Position) ?? FoehnTerrainSample.OpenAir;
            float localExposure = Mathf.Lerp(0.30f, 1f, terrain.Exposure);
            float nozzle = terrain.Nozzle;
            float wake = terrain.Wake;
            float edge = terrain.Edge;

            float waveA = Mathf.Sin(visualTime * 3.7f + particle.Phase * 17.13f);
            float waveB = Mathf.Sin(visualTime * 7.9f + particle.Phase * 31.71f);
            float gust = Mathf.Clamp01(0.64f + waveA * 0.23f + waveB * 0.13f + nozzle * 0.46f);
            float speed = Mathf.Lerp(6.8f, 15.6f, drive) *
                          Mathf.Lerp(0.55f, 1.24f, gust) *
                          Mathf.Lerp(0.72f, 1.34f, nozzle) *
                          localExposure;

            float wakeCurl = wake * (waveA * 0.85f + waveB * 0.35f);
            float edgeFlutter = edge * waveB * 0.52f;
            Vector2 targetVelocity = forward * speed + cross * (wakeCurl + edgeFlutter);
            particle.Velocity = Vector2.Lerp(particle.Velocity, targetVelocity, 0.16f + drive * 0.08f);
            particle.Position += particle.Velocity;

            if (particle.Life <= 0f || IsOutside(particle.Position, forward))
            {
                particle.Active = false;
                activeCount--;
            }
        }

        int spawnBudget = Mathf.Min(7, targetActive - activeCount);
        for (int spawn = 0; spawn < spawnBudget; spawn++)
        {
            int slot = FindInactiveSlot();
            if (slot < 0)
            {
                break;
            }

            Spawn(_particles[slot], room, drive, forward, terrainField);
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

        Vector2 forward = SafeNormalize(windDirection);
        float baseRotation = Mathf.Atan2(forward.y, forward.x) * Mathf.Rad2Deg;
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

            Vector2 position = Vector2.Lerp(particle.LastPosition, particle.Position, timeStacker);
            FoehnTerrainSample terrain = terrainField?.Sample(position) ?? FoehnTerrainSample.OpenAir;
            float lifeFade = Mathf.Clamp01(Mathf.Min(
                particle.Life / 15f,
                (particle.MaxLife - particle.Life) / 10f));
            float visibility = lifeFade * particle.Alpha * drive *
                               Mathf.Lerp(0.42f, 1f, terrain.Exposure);

            float speed = particle.Velocity.magnitude;
            float length = particle.BaseLength *
                           Mathf.Lerp(0.75f, 1.48f, Mathf.InverseLerp(5f, 18f, speed));

            sprite.SetPosition(position - camPos);
            sprite.rotation = baseRotation +
                              Mathf.Atan2(particle.Velocity.y, particle.Velocity.x) * Mathf.Rad2Deg * 0.08f;
            sprite.scaleX = Mathf.Max(2f, length);
            sprite.scaleY = particle.Width;
            sprite.alpha = Mathf.Clamp01(visibility);

            // Warm mineral dust. Near particles are brighter and longer; far particles
            // are dimmer so the system reads as depth, not a flat screen overlay.
            float near = 1f - particle.Depth;
            sprite.color = Color.Lerp(
                new Color(0.63f, 0.46f, 0.25f),
                new Color(0.96f, 0.79f, 0.46f),
                Mathf.Lerp(0.28f, 0.82f, near));
            sprite.isVisible = sprite.alpha > 0.012f;
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
        FoehnTerrainField terrainField)
    {
        Vector2 position = Vector2.zero;
        FoehnTerrainSample terrain = FoehnTerrainSample.OpenAir;

        for (int attempt = 0; attempt < 8; attempt++)
        {
            float x = (float)_random.NextDouble() * _roomWidth;
            float y = (float)_random.NextDouble() * _roomHeight;
            position = new Vector2(x, y);
            terrain = terrainField?.Sample(position) ?? FoehnTerrainSample.OpenAir;

            float preference = terrain.Exposure * 0.68f + terrain.Nozzle * 0.42f + 0.12f;
            if (_random.NextDouble() <= Mathf.Clamp01(preference))
            {
                break;
            }
        }

        float speed = Mathf.Lerp(6.5f, 15.0f, intensity) *
                      Mathf.Lerp(0.72f, 1.22f, (float)_random.NextDouble()) *
                      Mathf.Lerp(0.72f, 1.32f, terrain.Nozzle);
        Vector2 cross = new(-forward.y, forward.x);
        float lateral = ((float)_random.NextDouble() * 2f - 1f) *
                        Mathf.Lerp(0.25f, 1.6f, terrain.Wake + terrain.Edge * 0.5f);

        particle.Position = position;
        particle.LastPosition = position - forward * speed;
        particle.Velocity = forward * speed + cross * lateral;
        particle.MaxLife = Mathf.Lerp(45f, 150f, (float)_random.NextDouble());
        particle.Life = particle.MaxLife;
        particle.Width = Mathf.Lerp(0.45f, 1.35f, (float)_random.NextDouble());
        particle.BaseLength = Mathf.Lerp(6f, 27f, (float)_random.NextDouble());
        particle.Alpha = Mathf.Lerp(0.22f, 0.76f, (float)_random.NextDouble());
        particle.Depth = (float)_random.NextDouble();
        particle.Phase = (float)_random.NextDouble();
        particle.Active = true;
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

    private bool IsOutside(Vector2 position, Vector2 forward)
    {
        const float margin = 140f;
        if (position.x < -margin || position.x > _roomWidth + margin ||
            position.y < -margin || position.y > _roomHeight + margin)
        {
            return true;
        }

        // Keep particles from spending too long in the invisible upwind reserve.
        if (forward.x > 0f && position.x > _roomWidth + 40f)
        {
            return true;
        }
        if (forward.x < 0f && position.x < -40f)
        {
            return true;
        }

        return false;
    }

    private static Vector2 SafeNormalize(Vector2 value)
    {
        return value.sqrMagnitude > 0.0001f ? value.normalized : new Vector2(1f, -0.16f).normalized;
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
