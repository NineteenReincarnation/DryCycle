using System;
using System.Runtime.CompilerServices;
using DryCycle.TemperatureSystem;
using UnityEngine;

namespace DryCycle.Weather.IntenseHeat;

/// <summary>
/// Persistent short-term solar exposure state for creatures during IntenseHeat.
/// Exposure is gameplay/world state, not inferred from shader output.
///
/// The same value drives a post-draw palette shift so creatures visibly dry, yellow
/// and scorch after sustained direct sun. Player body heat also receives an additional
/// hazard-grade solar input on top of the normal temperature model.
/// </summary>
internal static class IntenseHeatCreatureExposure
{
    private const float TickSeconds = 1f / 40f;
    private const float ExposureGainPerSecond = 0.070f;
    private const float ShadeRecoveryPerSecond = 0.018f;
    private const float DeepShadeRecoveryPerSecond = 0.032f;
    private const float PlayerHazardHeatingPerSecond = 0.030f;

    private sealed class CreatureState
    {
        internal float Exposure;
    }

    private sealed class VisualState
    {
        internal Color[] BaseColors;
        internal Color[] LastTintedColors;
        internal Color[][] BaseVertexColors;
        internal Color[][] LastTintedVertexColors;
    }

    private static ConditionalWeakTable<Creature, CreatureState> _states = new();
    private static ConditionalWeakTable<RoomCamera.SpriteLeaser, VisualState> _visualStates = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.RoomCamera.DrawUpdate += RoomCamera_DrawUpdate;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RoomCamera.DrawUpdate -= RoomCamera_DrawUpdate;
        _states = new ConditionalWeakTable<Creature, CreatureState>();
        _visualStates = new ConditionalWeakTable<RoomCamera.SpriteLeaser, VisualState>();
        _enabled = false;
    }

    internal static void UpdateRoom(Room room, float hazardIntensity)
    {
        if (!_enabled || room?.physicalObjects == null)
        {
            return;
        }

        float intensity = Mathf.Clamp01(hazardIntensity);

        for (int layer = 0; layer < room.physicalObjects.Length; layer++)
        {
            var objects = room.physicalObjects[layer];
            if (objects == null)
            {
                continue;
            }

            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is not Creature creature || creature.room != room)
                {
                    continue;
                }

                UpdateCreature(creature, intensity);
            }
        }
    }

    internal static float GetExposure(Creature creature)
    {
        return creature != null && _states.TryGetValue(creature, out CreatureState state)
            ? Mathf.Clamp01(state.Exposure)
            : 0f;
    }

    private static void UpdateCreature(Creature creature, float intensity)
    {
        CreatureState state = _states.GetOrCreateValue(creature);
        Vector2 center = GetCreatureCenter(creature);
        float directSun = intensity > 0.0001f
            ? IntenseHeatSolarField.SampleExposure(creature.room, center) * intensity
            : 0f;

        if (directSun > 0.08f)
        {
            float gain = ExposureGainPerSecond * Mathf.Lerp(0.32f, 1f, directSun);
            state.Exposure = Mathf.Clamp01(state.Exposure + gain * TickSeconds);
        }
        else
        {
            float roomShade = creature.room != null
                ? Mathf.Clamp01(SolarEnvironment.GetRoomShade(creature.room))
                : 1f;
            float recovery = roomShade > 0.65f
                ? DeepShadeRecoveryPerSecond
                : ShadeRecoveryPerSecond;
            state.Exposure = Mathf.Clamp01(state.Exposure - recovery * TickSeconds);
        }

        if (creature is Player player && intensity > 0.0001f && !player.inShortcut)
        {
            ApplyPlayerHazardHeat(player, intensity);
        }
    }

    private static void ApplyPlayerHazardHeat(Player player, float intensity)
    {
        PlayerThermalState thermal = PlayerThermalModel.For(player);
        if (thermal == null || player.bodyChunks == null || player.bodyChunks.Length == 0)
        {
            return;
        }

        float exposure0 = IntenseHeatSolarField.SampleExposure(
            player.room,
            player.bodyChunks[0].pos) * intensity;
        float exposure1 = player.bodyChunks.Length > 1
            ? IntenseHeatSolarField.SampleExposure(player.room, player.bodyChunks[1].pos) * intensity
            : exposure0;

        thermal.BodyHeat0 = Mathf.Clamp01(
            thermal.BodyHeat0 + exposure0 * PlayerHazardHeatingPerSecond * TickSeconds);
        thermal.BodyHeat1 = Mathf.Clamp01(
            thermal.BodyHeat1 + exposure1 * PlayerHazardHeatingPerSecond * TickSeconds);
    }

    private static Vector2 GetCreatureCenter(Creature creature)
    {
        if (creature?.bodyChunks == null || creature.bodyChunks.Length == 0)
        {
            return creature?.mainBodyChunk?.pos ?? Vector2.zero;
        }

        Vector2 total = Vector2.zero;
        int count = 0;
        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            if (creature.bodyChunks[i] == null)
            {
                continue;
            }

            total += creature.bodyChunks[i].pos;
            count++;
        }

        return count > 0 ? total / count : creature.mainBodyChunk?.pos ?? Vector2.zero;
    }

    private static void RoomCamera_DrawUpdate(
        On.RoomCamera.orig_DrawUpdate orig,
        RoomCamera self,
        float timeStacker,
        float timeSpeed)
    {
        orig(self, timeStacker, timeSpeed);

        if (!_enabled || self?.room == null || self.spriteLeasers == null)
        {
            return;
        }

        for (int i = 0; i < self.spriteLeasers.Count; i++)
        {
            RoomCamera.SpriteLeaser leaser = self.spriteLeasers[i];
            if (leaser?.drawableObject is not GraphicsModule graphics ||
                graphics.owner is not Creature creature ||
                creature.room != self.room ||
                leaser.sprites == null)
            {
                continue;
            }

            float exposure = GetExposure(creature);
            if (exposure <= 0.001f)
            {
                continue;
            }

            ApplyTint(leaser, exposure);
        }
    }

    private static void ApplyTint(RoomCamera.SpriteLeaser leaser, float exposure)
    {
        VisualState visual = _visualStates.GetOrCreateValue(leaser);
        EnsureVisualArrays(leaser, visual);

        float tint = Smooth01(Mathf.InverseLerp(0.08f, 0.92f, exposure));
        float scorch = Smooth01(Mathf.InverseLerp(0.58f, 1f, exposure));

        for (int i = 0; i < leaser.sprites.Length; i++)
        {
            FSprite sprite = leaser.sprites[i];
            if (sprite == null)
            {
                continue;
            }

            Color current = sprite.color;
            if (!ApproximatelyColor(current, visual.LastTintedColors[i]))
            {
                visual.BaseColors[i] = current;
            }

            Color output = TintColor(visual.BaseColors[i], tint, scorch);
            sprite.color = output;
            visual.LastTintedColors[i] = output;

            if (sprite is TriangleMesh mesh && mesh.verticeColors != null)
            {
                EnsureVertexArrays(visual, i, mesh.verticeColors.Length);
                for (int v = 0; v < mesh.verticeColors.Length; v++)
                {
                    Color vertex = mesh.verticeColors[v];
                    if (!ApproximatelyColor(vertex, visual.LastTintedVertexColors[i][v]))
                    {
                        visual.BaseVertexColors[i][v] = vertex;
                    }

                    Color vertexOutput = TintColor(
                        visual.BaseVertexColors[i][v],
                        tint,
                        scorch);
                    mesh.verticeColors[v] = vertexOutput;
                    visual.LastTintedVertexColors[i][v] = vertexOutput;
                }
            }
        }
    }

    private static Color TintColor(Color source, float tint, float scorch)
    {
        float luma = source.r * 0.299f + source.g * 0.587f + source.b * 0.114f;
        float chroma = Mathf.Max(source.r, Mathf.Max(source.g, source.b)) -
                       Mathf.Min(source.r, Mathf.Min(source.g, source.b));

        Color dried = new(
            Mathf.Clamp01(source.r * 1.12f + luma * 0.18f),
            Mathf.Clamp01(source.g * 0.90f + luma * 0.095f),
            Mathf.Clamp01(source.b * 0.56f + luma * 0.025f),
            source.a);

        Color scorched = new(
            Mathf.Clamp01(dried.r * 0.92f + 0.105f),
            Mathf.Clamp01(dried.g * 0.70f + 0.045f),
            Mathf.Clamp01(dried.b * 0.42f + 0.010f),
            source.a);

        // Highly saturated biological markings remain readable longer than neutral
        // body surfaces; severe exposure eventually drags both toward the same dry range.
        float biologicalProtection = Mathf.Lerp(1f, 0.72f, Mathf.Clamp01(chroma * 1.5f));
        Color result = Color.Lerp(source, dried, tint * biologicalProtection);
        result = Color.Lerp(result, scorched, scorch * 0.72f);
        result.a = source.a;
        return result;
    }

    private static void EnsureVisualArrays(
        RoomCamera.SpriteLeaser leaser,
        VisualState visual)
    {
        int count = leaser.sprites?.Length ?? 0;
        if (visual.BaseColors != null && visual.BaseColors.Length == count)
        {
            return;
        }

        visual.BaseColors = new Color[count];
        visual.LastTintedColors = new Color[count];
        visual.BaseVertexColors = new Color[count][];
        visual.LastTintedVertexColors = new Color[count][];

        for (int i = 0; i < count; i++)
        {
            Color color = leaser.sprites[i]?.color ?? Color.white;
            visual.BaseColors[i] = color;
            visual.LastTintedColors[i] = color;
        }
    }

    private static void EnsureVertexArrays(VisualState visual, int spriteIndex, int count)
    {
        if (visual.BaseVertexColors[spriteIndex] != null &&
            visual.BaseVertexColors[spriteIndex].Length == count)
        {
            return;
        }

        visual.BaseVertexColors[spriteIndex] = new Color[count];
        visual.LastTintedVertexColors[spriteIndex] = new Color[count];
        for (int i = 0; i < count; i++)
        {
            visual.BaseVertexColors[spriteIndex][i] = Color.white;
            visual.LastTintedVertexColors[spriteIndex][i] = Color.white;
        }
    }

    private static bool ApproximatelyColor(Color a, Color b)
    {
        float difference = Mathf.Abs(a.r - b.r) +
                           Mathf.Abs(a.g - b.g) +
                           Mathf.Abs(a.b - b.b) +
                           Mathf.Abs(a.a - b.a);
        return difference < 0.012f;
    }

    private static float Smooth01(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - 2f * t);
    }
}
