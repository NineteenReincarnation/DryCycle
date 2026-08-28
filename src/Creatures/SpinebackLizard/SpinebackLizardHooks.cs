using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LizardCosmetics;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures;

/// <summary>
/// First playable Spineback Lizard prototype.
///
/// Baseline locomotion, AI and combat stats are cloned from Blue Lizard. The custom
/// layer adds a sand-brown thorny silhouette and an inflate/curl defensive response:
/// after taking damage the three lizard body chunks are pulled into a swollen ball,
/// nearby creatures are pushed off the spikes, and repeated hits are partially
/// absorbed while the defense is fully raised.
/// </summary>
internal static class SpinebackLizardHooks
{
    private const int DefenseHoldFrames = 170;
    private const float InflateRate = 0.075f;
    private const float DeflateRate = 0.025f;
    private const float FullDefenseThreshold = 0.65f;
    private const int ContactCooldownFrames = 24;

    private sealed class DefenseState
    {
        internal bool Initialized;
        internal int HoldFrames;
        internal float Progress;
        internal float[] BaseRadii;
        internal float[] BaseConnectionDistances;
        internal readonly Dictionary<Creature, int> ContactCooldowns = new();
    }

    private static readonly ConditionalWeakTable<Lizard, DefenseState> DefenseStates = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        SpinebackLizardEnums.Register();

        _enabled = true;
        On.StaticWorld.InitCustomTemplates += StaticWorld_InitCustomTemplates;
        On.StaticWorld.InitStaticWorld += StaticWorld_InitStaticWorld;
        On.Lizard.Update += Lizard_Update;
        On.Lizard.Violence += Lizard_Violence;
        On.LizardGraphics.ctor += LizardGraphics_ctor;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.StaticWorld.InitCustomTemplates -= StaticWorld_InitCustomTemplates;
        On.StaticWorld.InitStaticWorld -= StaticWorld_InitStaticWorld;
        On.Lizard.Update -= Lizard_Update;
        On.Lizard.Violence -= Lizard_Violence;
        On.LizardGraphics.ctor -= LizardGraphics_ctor;
    }

    internal static float GetDefenseProgress(Lizard lizard)
    {
        return lizard != null && DefenseStates.TryGetValue(lizard, out DefenseState state)
            ? state.Progress
            : 0f;
    }

    private static bool IsSpineback(Lizard lizard)
    {
        return lizard != null &&
               SpinebackLizardEnums.Type != null &&
               lizard.Template?.type == SpinebackLizardEnums.Type;
    }

    private static void StaticWorld_InitCustomTemplates(On.StaticWorld.orig_InitCustomTemplates orig)
    {
        orig();
        InstallTemplateFromBlueLizard();
    }

    private static void StaticWorld_InitStaticWorld(On.StaticWorld.orig_InitStaticWorld orig)
    {
        orig();
        SyncRelationshipsFromBlueLizard();
    }

    private static void InstallTemplateFromBlueLizard()
    {
        CreatureTemplate.Type spinebackType = SpinebackLizardEnums.Type;
        if (spinebackType == null || spinebackType.Index < 0 || StaticWorld.creatureTemplates == null)
        {
            return;
        }

        int blueIndex = CreatureTemplate.Type.BlueLizard.Index;
        if (blueIndex < 0 || blueIndex >= StaticWorld.creatureTemplates.Length)
        {
            return;
        }

        CreatureTemplate blue = StaticWorld.creatureTemplates[blueIndex];
        if (blue == null)
        {
            return;
        }

        int requiredLength = Math.Max(
            spinebackType.Index + 1,
            ExtEnum<CreatureTemplate.Type>.values.Count);

        if (StaticWorld.creatureTemplates.Length < requiredLength)
        {
            Array.Resize(ref StaticWorld.creatureTemplates, requiredLength);
        }

        CreatureTemplate template = new CreatureTemplate(blue)
        {
            type = spinebackType,
            name = "Spineback Lizard",
            index = spinebackType.Index,
            // Reuse Blue Lizard's generated pathing instead of requiring a new
            // prebaked pathing dataset for the prototype creature.
            doPreBakedPathing = false,
            preBakedPathingAncestor = blue,
            shortcutColor = new Color(0.68f, 0.46f, 0.22f)
        };

        StaticWorld.creatureTemplates[spinebackType.Index] = template;
    }

    private static void SyncRelationshipsFromBlueLizard()
    {
        CreatureTemplate.Type spinebackType = SpinebackLizardEnums.Type;
        CreatureTemplate[] templates = StaticWorld.creatureTemplates;

        if (spinebackType == null ||
            templates == null ||
            spinebackType.Index < 0 ||
            spinebackType.Index >= templates.Length ||
            CreatureTemplate.Type.BlueLizard.Index < 0 ||
            CreatureTemplate.Type.BlueLizard.Index >= templates.Length)
        {
            return;
        }

        int spineIndex = spinebackType.Index;
        int blueIndex = CreatureTemplate.Type.BlueLizard.Index;
        CreatureTemplate spineback = templates[spineIndex];
        CreatureTemplate blue = templates[blueIndex];

        if (spineback?.relationships == null || blue?.relationships == null)
        {
            return;
        }

        int outboundCount = Math.Min(spineback.relationships.Length, blue.relationships.Length);
        for (int i = 0; i < outboundCount; i++)
        {
            if (blue.relationships[i] != null)
            {
                spineback.relationships[i] = blue.relationships[i].Duplicate();
            }
        }

        // A Spineback should relate to another Spineback the same way a Blue
        // Lizard relates to another Blue Lizard, rather than inheriting the unused
        // custom slot from the blue relationship table.
        if (spineIndex < spineback.relationships.Length && blueIndex < blue.relationships.Length)
        {
            spineback.relationships[spineIndex] = blue.relationships[blueIndex].Duplicate();
        }

        // Mirror every existing creature's relationship toward Blue Lizard into
        // its relationship toward Spineback Lizard as well.
        for (int i = 0; i < templates.Length; i++)
        {
            CreatureTemplate other = templates[i];
            if (other?.relationships == null ||
                blueIndex >= other.relationships.Length ||
                spineIndex >= other.relationships.Length ||
                other.relationships[blueIndex] == null)
            {
                continue;
            }

            other.relationships[spineIndex] = other.relationships[blueIndex].Duplicate();
        }
    }

    private static void Lizard_Update(On.Lizard.orig_Update orig, Lizard self, bool eu)
    {
        orig(self, eu);

        if (!IsSpineback(self))
        {
            return;
        }

        DefenseState state = DefenseStates.GetOrCreateValue(self);
        InitializeState(self, state);
        TickContactCooldowns(state);

        if (self.dead)
        {
            state.HoldFrames = 0;
        }
        else if (state.HoldFrames > 0)
        {
            state.HoldFrames--;
        }

        float target = state.HoldFrames > 0 ? 1f : 0f;
        state.Progress = Mathf.MoveTowards(
            state.Progress,
            target,
            target > state.Progress ? InflateRate : DeflateRate);

        ApplyDefensiveBodyShape(self, state);

        if (!self.dead && state.Progress >= FullDefenseThreshold)
        {
            RepelNearbyCreatures(self, state);
        }
    }

    private static void Lizard_Violence(
        On.Lizard.orig_Violence orig,
        Lizard self,
        BodyChunk source,
        Vector2? directionAndMomentum,
        BodyChunk hitChunk,
        Appendage.Pos onAppendagePos,
        Creature.DamageType type,
        float damage,
        float stunBonus)
    {
        if (!IsSpineback(self))
        {
            orig(self, source, directionAndMomentum, hitChunk, onAppendagePos, type, damage, stunBonus);
            return;
        }

        DefenseState state = DefenseStates.GetOrCreateValue(self);
        InitializeState(self, state);

        // The first hit still lands normally. Once the lizard has fully curled up,
        // the inflated spiny body becomes substantially harder to damage or stun.
        float armor = Mathf.InverseLerp(FullDefenseThreshold, 1f, state.Progress);
        damage *= Mathf.Lerp(1f, 0.38f, armor);
        stunBonus *= Mathf.Lerp(1f, 0.45f, armor);

        orig(self, source, directionAndMomentum, hitChunk, onAppendagePos, type, damage, stunBonus);

        if (!self.dead)
        {
            state.HoldFrames = Math.Max(state.HoldFrames, DefenseHoldFrames);
        }
    }

    private static void LizardGraphics_ctor(
        On.LizardGraphics.orig_ctor orig,
        LizardGraphics self,
        PhysicalObject owner)
    {
        orig(self, owner);

        if (!IsSpineback(self?.lizard))
        {
            return;
        }

        int seed = self.lizard.abstractCreature?.ID.RandomSeed ?? 0;
        float variation = Stable01(seed);
        self.lizard.effectColor = Color.Lerp(
            new Color(0.52f, 0.30f, 0.13f),
            new Color(0.82f, 0.62f, 0.30f),
            variation);

        int startSprite = self.startOfExtraSprites + self.extraSprites;
        self.AddCosmetic(startSprite, new SpinebackLizardSpikes(self, startSprite));

        // A second vanilla spine row makes the normal, uninflated animal visibly
        // thornier before the custom radial crown spreads during defense.
        int secondStart = self.startOfExtraSprites + self.extraSprites;
        SpineSpikes dorsalSpikes = new SpineSpikes(self, secondStart)
        {
            spineLength = self.BodyAndTailLength * 0.78f,
            sizeRangeMin = 0.42f,
            sizeRangeMax = 1.05f,
            sizeSkewExponent = 0.52f,
            scaleX = 1f
        };
        self.AddCosmetic(secondStart, dorsalSpikes);
    }

    private static void InitializeState(Lizard lizard, DefenseState state)
    {
        if (state.Initialized)
        {
            return;
        }

        state.Initialized = true;
        state.BaseRadii = new float[lizard.bodyChunks.Length];
        for (int i = 0; i < lizard.bodyChunks.Length; i++)
        {
            state.BaseRadii[i] = lizard.bodyChunks[i].rad;
        }

        state.BaseConnectionDistances = new float[lizard.bodyChunkConnections.Length];
        for (int i = 0; i < lizard.bodyChunkConnections.Length; i++)
        {
            state.BaseConnectionDistances[i] = lizard.bodyChunkConnections[i].distance;
        }
    }

    private static void ApplyDefensiveBodyShape(Lizard lizard, DefenseState state)
    {
        float p = Mathf.SmoothStep(0f, 1f, state.Progress);

        for (int i = 0; i < lizard.bodyChunks.Length && i < state.BaseRadii.Length; i++)
        {
            lizard.bodyChunks[i].rad = state.BaseRadii[i] * Mathf.Lerp(1f, 1.58f, p);
        }

        for (int i = 0; i < lizard.bodyChunkConnections.Length && i < state.BaseConnectionDistances.Length; i++)
        {
            lizard.bodyChunkConnections[i].distance =
                state.BaseConnectionDistances[i] * Mathf.Lerp(1f, 0.24f, p);
        }

        if (p <= 0.001f || lizard.bodyChunks.Length < 3)
        {
            return;
        }

        Vector2 center = Vector2.zero;
        for (int i = 0; i < lizard.bodyChunks.Length; i++)
        {
            center += lizard.bodyChunks[i].pos;
        }
        center /= lizard.bodyChunks.Length;

        float pull = Mathf.Lerp(0.035f, 0.23f, p);
        float velocityRetention = Mathf.Lerp(0.96f, 0.52f, p);

        for (int i = 0; i < lizard.bodyChunks.Length; i++)
        {
            lizard.bodyChunks[i].pos = Vector2.Lerp(lizard.bodyChunks[i].pos, center, pull);
            lizard.bodyChunks[i].vel *= velocityRetention;
        }

        lizard.JawOpen = Mathf.Min(lizard.JawOpen, 1f - p);

        if (p > 0.35f)
        {
            lizard.LoseAllGrasps();
            lizard.movementAnimation = null;
            lizard.animation = Lizard.Animation.Standard;
        }
    }

    private static void RepelNearbyCreatures(Lizard lizard, DefenseState state)
    {
        Room room = lizard.room;
        if (room?.physicalObjects == null || lizard.bodyChunks == null || lizard.bodyChunks.Length == 0)
        {
            return;
        }

        Vector2 center = Vector2.zero;
        for (int i = 0; i < lizard.bodyChunks.Length; i++)
        {
            center += lizard.bodyChunks[i].pos;
        }
        center /= lizard.bodyChunks.Length;

        float dangerRadius = Mathf.Lerp(24f, 36f, state.Progress);

        for (int layer = 0; layer < room.physicalObjects.Length; layer++)
        {
            List<PhysicalObject> objects = room.physicalObjects[layer];
            if (objects == null)
            {
                continue;
            }

            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is not Creature other ||
                    other == lizard ||
                    other.bodyChunks == null ||
                    other.bodyChunks.Length == 0 ||
                    (other is Lizard otherLizard && IsSpineback(otherLizard)))
                {
                    continue;
                }

                BodyChunk nearestChunk = null;
                float nearestDistance = float.MaxValue;

                for (int chunkIndex = 0; chunkIndex < other.bodyChunks.Length; chunkIndex++)
                {
                    BodyChunk chunk = other.bodyChunks[chunkIndex];
                    float distance = Vector2.Distance(center, chunk.pos) - chunk.rad;
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearestChunk = chunk;
                    }
                }

                if (nearestChunk == null || nearestDistance >= dangerRadius)
                {
                    continue;
                }

                Vector2 away = nearestChunk.pos - center;
                if (away.sqrMagnitude < 0.001f)
                {
                    away = Custom.RNV();
                }
                else
                {
                    away.Normalize();
                }

                float penetration = Mathf.Clamp01(1f - nearestDistance / dangerRadius);
                float push = Mathf.Lerp(2.8f, 7.2f, penetration) *
                             Mathf.Lerp(0.75f, 1f, state.Progress);

                nearestChunk.vel += away * push;
                for (int chunkIndex = 0; chunkIndex < lizard.bodyChunks.Length; chunkIndex++)
                {
                    lizard.bodyChunks[chunkIndex].vel -= away * 0.12f;
                }

                if (state.ContactCooldowns.ContainsKey(other))
                {
                    continue;
                }

                // Small stab damage makes physically forcing through the thorn crown
                // a losing strategy without turning the prototype into an instant-kill
                // hazard. The pushback is the primary defensive effect.
                other.Violence(
                    lizard.mainBodyChunk,
                    away * 2.5f,
                    nearestChunk,
                    null,
                    Creature.DamageType.Stab,
                    0.08f,
                    14f);

                state.ContactCooldowns[other] = ContactCooldownFrames;
            }
        }
    }

    private static void TickContactCooldowns(DefenseState state)
    {
        if (state.ContactCooldowns.Count == 0)
        {
            return;
        }

        List<Creature> keys = new List<Creature>(state.ContactCooldowns.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            Creature creature = keys[i];
            if (creature == null ||
                creature.slatedForDeletetion ||
                state.ContactCooldowns[creature] <= 1)
            {
                state.ContactCooldowns.Remove(creature);
            }
            else
            {
                state.ContactCooldowns[creature]--;
            }
        }
    }

    private static float Stable01(int seed)
    {
        float value = Mathf.Sin((seed + 31) * 12.9898f) * 43758.5453f;
        return value - Mathf.Floor(value);
    }
}
