using LizardCosmetics;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures;

/// <summary>
/// Dense dorsal spikes that spread into a radial crown while the Spineback Lizard
/// inflates. The ordinary lizard body mesh supplies the swollen ball silhouette;
/// these sprites make the dangerous no-contact zone readable at game zoom.
/// </summary>
internal sealed class SpinebackLizardSpikes : Template
{
    private const int SpikeCount = 16;

    private readonly float _phase;

    internal SpinebackLizardSpikes(LizardGraphics graphics, int startSprite)
        : base(graphics, startSprite)
    {
        spritesOverlap = SpritesOverlap.BehindHead;
        numberOfSprites = SpikeCount;

        int seed = graphics?.lizard?.abstractCreature?.ID.RandomSeed ?? 0;
        _phase = Mathf.Repeat(seed * 37.173f, 360f);
    }

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        for (int i = 0; i < SpikeCount; i++)
        {
            FSprite spike = new FSprite("LizardScaleA3");
            spike.anchorY = 0.12f;
            spike.scaleX = (i % 2 == 0) ? 0.72f : -0.72f;
            sLeaser.sprites[startSprite + i] = spike;
        }
    }

    public override void DrawSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        Lizard lizard = lGraphics?.lizard;
        if (lizard == null || lizard.bodyChunks == null || lizard.bodyChunks.Length < 3)
        {
            return;
        }

        float defense = SpinebackLizardHooks.GetDefenseProgress(lizard);
        float ballBlend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.18f, 0.92f, defense));

        Vector2 center = (
            Vector2.Lerp(lizard.bodyChunks[0].lastPos, lizard.bodyChunks[0].pos, timeStacker) +
            Vector2.Lerp(lizard.bodyChunks[1].lastPos, lizard.bodyChunks[1].pos, timeStacker) +
            Vector2.Lerp(lizard.bodyChunks[2].lastPos, lizard.bodyChunks[2].pos, timeStacker)) / 3f;

        for (int i = 0; i < SpikeCount; i++)
        {
            float t = i / (float)(SpikeCount - 1);
            LizardGraphics.LizardSpineData spine = lGraphics.SpinePosition(
                Mathf.Lerp(0.025f, 0.76f, t),
                timeStacker);

            Vector2 normal = spine.perp;
            if (normal.sqrMagnitude < 0.0001f)
            {
                normal = Vector2.up;
            }
            else
            {
                normal.Normalize();
            }

            if (spine.depthRotation < 0f)
            {
                normal = -normal;
            }

            float radialAngle = _phase + i * (360f / SpikeCount);
            Vector2 radialDir = Custom.DegToVec(radialAngle);
            float radialDistance = Mathf.Lerp(12f, 25f, defense) *
                                   Mathf.Lerp(0.88f, 1.12f, Mathf.Sin((i + 1) * 2.41f) * 0.5f + 0.5f);

            Vector2 normalPos = spine.outerPos + normal * 1.5f;
            Vector2 ballPos = center + radialDir * radialDistance;
            Vector2 drawPos = Vector2.Lerp(normalPos, ballPos, ballBlend);

            Vector2 spikeDir = Vector2.Lerp(normal, radialDir, ballBlend);
            if (spikeDir.sqrMagnitude < 0.0001f)
            {
                spikeDir = Vector2.up;
            }
            else
            {
                spikeDir.Normalize();
            }

            float sizeVariation = Mathf.Lerp(
                0.72f,
                1.18f,
                Mathf.Sin((i + 2) * 1.77f) * 0.5f + 0.5f);

            FSprite spike = sLeaser.sprites[startSprite + i];
            spike.x = drawPos.x - camPos.x;
            spike.y = drawPos.y - camPos.y;
            spike.rotation = Custom.VecToDeg(spikeDir) - 90f;
            spike.scaleX = Mathf.Sign(spike.scaleX) * Mathf.Lerp(0.62f, 0.92f, defense) * sizeVariation;
            spike.scaleY = Mathf.Lerp(0.72f, 1.62f, defense) * sizeVariation;
            spike.alpha = 1f;
        }
    }

    public override void ApplyPalette(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        RoomPalette palette)
    {
        base.ApplyPalette(sLeaser, rCam, palette);

        Color baseColor = lGraphics?.lizard != null
            ? lGraphics.lizard.effectColor
            : new Color(0.65f, 0.43f, 0.20f);

        Color spikeColor = Color.Lerp(baseColor, palette.blackColor, 0.18f);
        for (int i = 0; i < SpikeCount; i++)
        {
            sLeaser.sprites[startSprite + i].color = spikeColor;
        }
    }
}
