using System;
using System.Runtime.CompilerServices;
using LizardCosmetics;
using UnityEngine;

namespace DryCycle.Creatures;

/// <summary>
/// Spineback Lizard prototype. Gameplay statistics, AI and pathing still use Blue
/// Lizard as the baseline, while the visible silhouette and palette are custom.
/// </summary>
internal static class SpinebackLizardHooks
{
    private sealed class GraphicsState
    {
        internal int VanillaExtraStart;
        internal int VanillaExtraEnd;
    }

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
        On.LizardGraphics.ctor -= LizardGraphics_ctor;
        On.LizardGraphics.DrawSprites -= LizardGraphics_DrawSprites;
        On.LizardGraphics.ApplyPalette -= LizardGraphics_ApplyPalette;
    }

    internal static Color GetBodyColor(Lizard lizard)
    {
        int seed = lizard?.abstractCreature?.ID.RandomSeed ?? 0;
        float variation = Stable01(seed + 101);
        return Color.Lerp(
            new Color(0.72f, 0.55f, 0.45f),
            new Color(0.82f, 0.67f, 0.55f),
            variation);
    }

    internal static Color GetBackColor(Lizard lizard)
    {
        int seed = lizard?.abstractCreature?.ID.RandomSeed ?? 0;
        float variation = Stable01(seed + 1907);
        return Color.Lerp(
            new Color(0.46f, 0.28f, 0.23f),
            new Color(0.59f, 0.38f, 0.31f),
            variation);
    }

    internal static Color GetStripeColor(Lizard lizard)
    {
        return Color.Lerp(
            GetBodyColor(lizard),
            new Color(0.91f, 0.79f, 0.67f),
            0.58f);
    }

    internal static Color GetSpikeColor(Lizard lizard)
    {
        return Color.Lerp(
            new Color(0.025f, 0.022f, 0.021f),
            GetBackColor(lizard),
            0.10f);
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
            shortcutColor = new Color(0.57f, 0.37f, 0.30f)
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

        // Match the supplied concept: large blunt head, thick low body and a heavy
        // tail rather than Blue Lizard's narrow silhouette.
        self.iVars.fatness = Mathf.Lerp(1.28f, 1.42f, Stable01(seed + 503));
        self.iVars.headSize = Mathf.Lerp(1.14f, 1.28f, Stable01(seed + 907));
        self.iVars.tailFatness = Mathf.Lerp(1.12f, 1.30f, Stable01(seed + 1301));
        self.iVars.tailColor = 0f;
        self.lizard.effectColor = GetBackColor(self.lizard);

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
        Color body = ShadeForRoom(lizard, rCam, GetBodyColor(lizard));
        Color back = ShadeForRoom(lizard, rCam, GetBackColor(lizard));
        Color stripe = ShadeForRoom(lizard, rCam, GetStripeColor(lizard));
        Color spike = ShadeForRoom(lizard, rCam, GetSpikeColor(lizard));

        self.ColorBody(sLeaser, body);

        // Keep the head mostly reddish-brown like the concept art, with a lighter
        // lower-jaw/throat accent and a near-black eye/mouth detail.
        if (self.SpriteHeadStart >= 0 && self.SpriteHeadStart + 4 < sLeaser.sprites.Length)
        {
            sLeaser.sprites[self.SpriteHeadStart].color = back;
            sLeaser.sprites[self.SpriteHeadStart + 1].color = stripe;
            sLeaser.sprites[self.SpriteHeadStart + 2].color = stripe;
            sLeaser.sprites[self.SpriteHeadStart + 3].color = back;
            sLeaser.sprites[self.SpriteHeadStart + 4].color = spike;
        }

        int limbColorEnd = Math.Min(self.SpriteLimbsColorEnd, sLeaser.sprites.Length);
        for (int i = self.SpriteLimbsColorStart; i < limbColorEnd; i++)
        {
            sLeaser.sprites[i].color = (i % 2 == 0) ? back : body;
        }

        // Remove Blue Lizard's random cosmetic rolls. The Spineback silhouette is
        // entirely defined by its own grouped spine and dorsal-pattern layer.
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

        self.ColorBody(
            sLeaser,
            ShadeForRoom(self.lizard, rCam, GetBodyColor(self.lizard)));
    }
}
