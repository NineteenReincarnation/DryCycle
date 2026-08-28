using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LizardCosmetics;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures;

/// <summary>
/// Spineback Lizard prototype. Gameplay statistics and pathing still use Blue Lizard
/// as the baseline, while the graphics and defensive pose are custom.
/// </summary>
internal static class SpinebackLizardHooks
{
    private const int DefenseHoldFrames = 170;
    private const float InflateRate = 0.075f;
    private const float DeflateRate = 0.025f;
    private const float FullDefenseThreshold = 0.65f;
    private const int ContactCooldownFrames = 24;

    private const float NormalBodyRadiusScale = 1.06f;
    private const float DefensiveBodyRadiusScale = 1.12f;
    private const float NormalConnectionScale = 0.92f;
    private const float DefensiveConnectionScale = 0.54f;

    private sealed class DefenseState
    {
        internal bool Initialized;
        internal int HoldFrames;
        internal float Progress;
        internal float[] BaseRadii;
        internal float[] BaseConnectionDistances;
        internal readonly Dictionary<Creature, int> ContactCooldowns = new();
    }

    private sealed class GraphicsState
    {
        internal int VanillaExtraStart;
        internal int VanillaExtraEnd;
    }

    private static readonly ConditionalWeakTable<Lizard, DefenseState> DefenseStates = new();
    private static readonly ConditionalWeakTable<LizardGraphics, GraphicsState> GraphicsStates = new();
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
        On.LizardGraphics.DrawSprites += LizardGraphics_DrawSprites;
        On.LizardGraphics.ApplyPalette += LizardGraphics_ApplyPalette;
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
        On.LizardGraphics.DrawSprites -= LizardGraphics_DrawSprites;
        On.LizardGraphics.ApplyPalette -= LizardGraphics_ApplyPalette;
    }

    internal static float GetDefenseProgress(Lizard lizard)
    {
        return lizard != null && DefenseStates.TryGetValue(lizard, out DefenseState state)
            ? state.Progress
            : 0f;
    }

    internal static Color GetBodyColor(Lizard lizard)
    {
        int seed = lizard?.abstractCreature?.ID.RandomSeed ?? 0;
        float variation = Stable01(seed + 101);
        return Color.Lerp(
            new Color(0.62f, 0.52f, 0.38f),
            new Color(0.80f, 0.71f, 0.55f),
            variation);
    }

    internal static Color GetPlateColor(Lizard lizard)
    {
        return Color.Lerp(
            GetBodyColor(lizard),
            new Color(0.88f, 0.82f, 0.67f),
            0.48f);
    }

    internal static Color GetRustColor(Lizard lizard)
    {
        int seed = lizard?.abstractCreature?.ID.RandomSeed ?? 0;
        float variation = Stable01(seed + 1907);
        return Color.Lerp(
            new Color(0.34f, 0.18f, 0.10f),
            new Color(0.52f, 0.30f, 0.16f),
            variation);
    }

    internal static Color GetDarkColor(Lizard lizard)
    {
        return Color.Lerp(GetRustColor(lizard), new Color(0.08f, 0.065f, 0.055f), 0.58f);
    }

    internal static Color ShadeForRoom(Lizard lizard, RoomCamera rCam, Color color)
    {
        if (lizard?.room == null || rCam == null)
        {
            return color;
        }

        float darkness = Mathf.Clamp01(lizard.room.Darkness(lizard.mainBodyChunk.pos));
        return Color.Lerp(color, rCam.currentPalette.blackColor, darkness * 0.72f);
    }

    internal static float Stable01(int seed)
    {
        float value = Mathf.Sin((seed + 31) * 12.9898f) * 43758.5453f;
        return value - Mathf.Floor(value);
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
            doPreBakedPathing = false,
            preBakedPathingAncestor = blue,
            shortcutColor = new Color(0.72f, 0.60f, 0.42f)
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

        if (spineIndex < spineback.relationships.Length && blueIndex < blue.relationships.Length)
        {
            spineback.relationships[spineIndex] = blue.relationships[blueIndex].Duplicate();
        }

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
        PhysicalObject.Appendage.Pos onAppendagePos,
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
        float widthVariation = Stable01(seed + 503);

        self.iVars.fatness = Mathf.Lerp(1.24f, 1.42f, widthVariation);
        self.iVars.headSize = Mathf.Lerp(0.84f, 0.96f, Stable01(seed + 907));
        self.iVars.tailFatness = Mathf.Lerp(0.92f, 1.08f, Stable01(seed + 1301));
        self.iVars.tailColor = 0f;
        self.lizard.effectColor = GetBodyColor(self.lizard);

        int vanillaExtraStart = self.startOfExtraSprites;
        int customStart = vanillaExtraStart + self.extraSprites;
        GraphicsState graphicsState = GraphicsStates.GetOrCreateValue(self);
        graphicsState.VanillaExtraStart = vanillaExtraStart;
        graphicsState.VanillaExtraEnd = customStart;
        self.AddCosmetic(customStart, new SpinebackLizardSpikes(self, customStart));
    }

    private static void LizardGraphics_DrawSprites(
        On.LizardGraphics.orig_DrawSprites orig,
        LizardGraphics self,
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        orig(self, sLeaser, rCam, timeStacker, camPos);

        if (!IsSpineback(self?.lizard) || sLeaser?.sprites == null)
        {
            return;
        }

        Lizard lizard = self.lizard;
        float defense = GetDefenseProgress(lizard);
        float ballBlend = Mathf.SmoothStep(
            0f,
            1f,
            Mathf.InverseLerp(0.18f, 0.78f, defense));
        float baseAlpha = 1f - ballBlend;

        Color body = ShadeForRoom(lizard, rCam, GetBodyColor(lizard));
        Color plate = ShadeForRoom(lizard, rCam, GetPlateColor(lizard));
        Color rust = ShadeForRoom(lizard, rCam, GetRustColor(lizard));
        Color dark = ShadeForRoom(lizard, rCam, GetDarkColor(lizard));

        self.ColorBody(sLeaser, body);

        if (self.SpriteHeadStart >= 0 && self.SpriteHeadStart + 4 < sLeaser.sprites.Length)
        {
            sLeaser.sprites[self.SpriteHeadStart].color = body;
            sLeaser.sprites[self.SpriteHeadStart + 1].color = plate;
            sLeaser.sprites[self.SpriteHeadStart + 2].color = plate;
            sLeaser.sprites[self.SpriteHeadStart + 3].color = body;
            sLeaser.sprites[self.SpriteHeadStart + 4].color = dark;
        }

        int limbColorEnd = Math.Min(self.SpriteLimbsColorEnd, sLeaser.sprites.Length);
        for (int i = self.SpriteLimbsColorStart; i < limbColorEnd; i++)
        {
            sLeaser.sprites[i].color = (i % 2 == 0) ? rust : plate;
        }

        if (GraphicsStates.TryGetValue(self, out GraphicsState graphicsState))
        {
            int vanillaStart = Math.Max(0, graphicsState.VanillaExtraStart);
            int vanillaEnd = Math.Min(graphicsState.VanillaExtraEnd, sLeaser.sprites.Length);
            for (int i = vanillaStart; i < vanillaEnd; i++)
            {
                if (sLeaser.sprites[i] != null)
                {
                    sLeaser.sprites[i].alpha = 0f;
                }
            }
        }

        int baseEnd = GraphicsStates.TryGetValue(self, out GraphicsState baseGraphicsState)
            ? Math.Min(baseGraphicsState.VanillaExtraStart, sLeaser.sprites.Length)
            : Math.Min(self.startOfExtraSprites, sLeaser.sprites.Length);
        for (int i = 0; i < baseEnd; i++)
        {
            if (sLeaser.sprites[i] != null)
            {
                sLeaser.sprites[i].alpha *= baseAlpha;
            }
        }
    }

    private static void LizardGraphics_ApplyPalette(
        On.LizardGraphics.orig_ApplyPalette orig,
        LizardGraphics self,
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        RoomPalette palette)
    {
        orig(self, sLeaser, rCam, palette);

        if (!IsSpineback(self?.lizard) || sLeaser?.sprites == null)
        {
            return;
        }

        Color body = ShadeForRoom(self.lizard, rCam, GetBodyColor(self.lizard));
        self.ColorBody(sLeaser, body);
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
            lizard.bodyChunks[i].rad = state.BaseRadii[i] * Mathf.Lerp(
                NormalBodyRadiusScale,
                DefensiveBodyRadiusScale,
                p);
        }

        for (int i = 0; i < lizard.bodyChunkConnections.Length && i < state.BaseConnectionDistances.Length; i++)
        {
            lizard.bodyChunkConnections[i].distance = state.BaseConnectionDistances[i] * Mathf.Lerp(
                NormalConnectionScale,
                DefensiveConnectionScale,
                p);
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

        float pull = Mathf.Lerp(0.012f, 0.115f, p);
        float velocityRetention = Mathf.Lerp(0.96f, 0.62f, p);

        for (int i = 0; i < lizard.bodyChunks.Length; i++)
        {
            lizard.bodyChunks[i].pos = Vector2.Lerp(lizard.bodyChunks[i].pos, center, pull);
            lizard.bodyChunks[i].vel *= velocityRetention;
        }

        lizard.JawOpen = Mathf.Min(lizard.JawOpen, 1f - p);

        if (p > 0.28f)
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

        float dangerRadius = Mathf.Lerp(23f, 34f, state.Progress);

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
}
