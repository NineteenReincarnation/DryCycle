using LizardCosmetics;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures;

/// <summary>
/// Custom Spineback surface based on the supplied concept art: a reddish-brown
/// dorsal mantle over a pale sand body, cream broken stripes, and several separated
/// clusters of long near-black spines rather than an evenly covered thorn coat.
/// </summary>
internal sealed class SpinebackLizardSpikes : Template
{
    private const int BackPatchStart = 0;
    private const int BackPatchCount = 20;

    private const int StripePairCount = 9;
    private const int StripeStart = BackPatchStart + BackPatchCount;
    private const int StripeSpriteCount = StripePairCount * 2;

    private const int SpikeStart = StripeStart + StripeSpriteCount;
    private const int SpikeCount = 26;

    private static readonly float[] StripePositions =
    {
        0.27f, 0.34f, 0.41f, 0.49f, 0.57f, 0.65f, 0.73f, 0.81f, 0.89f
    };

    // Small swept head crest -> very long shoulder crown -> low bridge -> second
    // long rear crown -> short tail-base thorns. This distribution follows the
    // supplied drawing instead of spacing every thorn uniformly down the spine.
    private static readonly float[] SpikePositions =
    {
        0.015f, 0.035f, 0.055f, 0.078f, 0.105f,
        0.145f, 0.175f, 0.205f, 0.235f, 0.270f, 0.305f, 0.345f,
        0.395f, 0.445f,
        0.525f, 0.560f, 0.595f, 0.635f, 0.675f, 0.715f, 0.755f,
        0.805f, 0.845f, 0.885f, 0.925f, 0.960f
    };

    private static readonly float[] SpikeLengths =
    {
        1.05f, 1.18f, 1.30f, 1.42f, 1.55f,
        2.75f, 3.65f, 4.35f, 4.05f, 3.55f, 2.80f, 2.05f,
        0.82f, 0.92f,
        2.45f, 3.35f, 3.95f, 3.65f, 3.10f, 2.55f, 1.90f,
        1.48f, 1.30f, 1.13f, 0.96f, 0.82f
    };

    private static readonly float[] SpikeLeans =
    {
        1.15f, 1.05f, 0.95f, 0.85f, 0.72f,
        0.05f, 0.18f, 0.34f, 0.48f, 0.62f, 0.78f, 0.92f,
        0.65f, 0.72f,
        0.02f, 0.18f, 0.34f, 0.50f, 0.65f, 0.78f, 0.90f,
        0.78f, 0.82f, 0.86f, 0.90f, 0.94f
    };

    private readonly float[] _backVariation = new float[BackPatchCount];
    private readonly float[] _stripeVariation = new float[StripePairCount];
    private readonly float[] _spikeVariation = new float[SpikeCount];

    internal SpinebackLizardSpikes(LizardGraphics graphics, int startSprite)
        : base(graphics, startSprite)
    {
        spritesOverlap = SpritesOverlap.InFront;
        numberOfSprites = SpikeStart + SpikeCount;

        int seed = graphics?.lizard?.abstractCreature?.ID.RandomSeed ?? 0;

        for (int i = 0; i < BackPatchCount; i++)
        {
            _backVariation[i] = SpinebackLizardHooks.Stable01(seed + 3001 + i * 97);
        }

        for (int i = 0; i < StripePairCount; i++)
        {
            _stripeVariation[i] = SpinebackLizardHooks.Stable01(seed + 5003 + i * 131);
        }

        for (int i = 0; i < SpikeCount; i++)
        {
            _spikeVariation[i] = SpinebackLizardHooks.Stable01(seed + 7001 + i * 163);
        }
    }

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        for (int i = 0; i < BackPatchCount; i++)
        {
            FSprite patch = new FSprite("Circle20");
            patch.anchorX = 0.5f;
            patch.anchorY = 0.5f;
            sLeaser.sprites[startSprite + BackPatchStart + i] = patch;
        }

        for (int i = 0; i < StripeSpriteCount; i++)
        {
            FSprite stripe = new FSprite("Circle20");
            stripe.anchorX = 0.5f;
            stripe.anchorY = 0.5f;
            sLeaser.sprites[startSprite + StripeStart + i] = stripe;
        }

        for (int i = 0; i < SpikeCount; i++)
        {
            FSprite spike = new FSprite("LizardScaleA3");
            spike.anchorY = 0.15f;
            sLeaser.sprites[startSprite + SpikeStart + i] = spike;
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

        Color backColor = SpinebackLizardHooks.ShadeForRoom(
            lizard,
            rCam,
            SpinebackLizardHooks.GetBackColor(lizard));
        Color stripeColor = SpinebackLizardHooks.ShadeForRoom(
            lizard,
            rCam,
            SpinebackLizardHooks.GetStripeColor(lizard));
        Color spikeColor = SpinebackLizardHooks.ShadeForRoom(
            lizard,
            rCam,
            SpinebackLizardHooks.GetSpikeColor(lizard));

        DrawBackMantle(sLeaser, timeStacker, camPos, backColor);
        DrawBrokenStripes(sLeaser, timeStacker, camPos, stripeColor);
        DrawGroupedSpines(sLeaser, timeStacker, camPos, backColor, spikeColor);
    }

    public override void ApplyPalette(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        RoomPalette palette)
    {
        base.ApplyPalette(sLeaser, rCam, palette);
    }

    private void DrawBackMantle(
        RoomCamera.SpriteLeaser sLeaser,
        float timeStacker,
        Vector2 camPos,
        Color backColor)
    {
        for (int i = 0; i < BackPatchCount; i++)
        {
            float t = Mathf.Lerp(0.035f, 0.945f, i / (float)(BackPatchCount - 1));
            LizardGraphics.LizardSpineData spine = lGraphics.SpinePosition(t, timeStacker);
            Vector2 tangent = SafeNormal(spine.dir, Vector2.right);

            // Keep the darker mantle on the visible dorsal half while leaving the
            // pale base body exposed below, matching the two-tone concept.
            Vector2 drawPos = Vector2.Lerp(spine.pos, spine.outerPos, 0.43f);
            float localWidth = Mathf.Clamp(spine.rad / 9.5f, 0.42f, 1.10f);
            float taper = Mathf.Lerp(1f, 0.62f, Mathf.InverseLerp(0.66f, 0.96f, t));
            float variation = Mathf.Lerp(0.92f, 1.08f, _backVariation[i]);

            FSprite patch = sLeaser.sprites[startSprite + BackPatchStart + i];
            patch.x = drawPos.x - camPos.x;
            patch.y = drawPos.y - camPos.y;
            patch.rotation = Custom.VecToDeg(tangent);
            patch.scaleX = Mathf.Lerp(0.58f, 0.46f, t) * variation;
            patch.scaleY = localWidth * Mathf.Lerp(0.52f, 0.34f, t) * taper;
            patch.color = backColor;
            patch.alpha = 0.98f;
        }
    }

    private void DrawBrokenStripes(
        RoomCamera.SpriteLeaser sLeaser,
        float timeStacker,
        Vector2 camPos,
        Color stripeColor)
    {
        for (int i = 0; i < StripePairCount; i++)
        {
            float t = StripePositions[i];
            LizardGraphics.LizardSpineData spine = lGraphics.SpinePosition(t, timeStacker);
            Vector2 outward = GetSurfaceOutward(spine);
            Vector2 tangent = SafeNormal(spine.dir, Vector2.right);

            float variation = _stripeVariation[i];
            float kink = Mathf.Lerp(-0.28f, 0.28f, variation);
            if (i % 2 == 0)
            {
                kink = -kink;
            }

            Vector2 stripeDirA = SafeNormal(-outward + tangent * kink, -outward);
            Vector2 stripeDirB = SafeNormal(-outward - tangent * kink * 0.65f, -outward);

            float stripeLength = Mathf.Lerp(0.34f, 0.49f, variation) *
                                 Mathf.Lerp(1f, 0.72f, Mathf.InverseLerp(0.72f, 0.92f, t));
            float stripeWidth = Mathf.Lerp(0.105f, 0.155f, variation);

            Vector2 posA = spine.pos + outward * spine.rad * 0.19f;
            Vector2 posB = spine.pos - outward * spine.rad * 0.16f + tangent * kink * 2.2f;

            DrawStripeSegment(
                sLeaser.sprites[startSprite + StripeStart + i * 2],
                posA,
                stripeDirA,
                stripeWidth,
                stripeLength,
                stripeColor,
                camPos);

            DrawStripeSegment(
                sLeaser.sprites[startSprite + StripeStart + i * 2 + 1],
                posB,
                stripeDirB,
                stripeWidth * 0.88f,
                stripeLength * 0.72f,
                stripeColor,
                camPos);
        }
    }

    private static void DrawStripeSegment(
        FSprite stripe,
        Vector2 pos,
        Vector2 direction,
        float width,
        float length,
        Color color,
        Vector2 camPos)
    {
        stripe.x = pos.x - camPos.x;
        stripe.y = pos.y - camPos.y;
        stripe.rotation = Custom.AimFromOneVectorToAnother(-direction, direction);
        stripe.scaleX = width;
        stripe.scaleY = length;
        stripe.color = color;
        stripe.alpha = 0.96f;
    }

    private void DrawGroupedSpines(
        RoomCamera.SpriteLeaser sLeaser,
        float timeStacker,
        Vector2 camPos,
        Color backColor,
        Color spikeColor)
    {
        for (int i = 0; i < SpikeCount; i++)
        {
            float t = SpikePositions[i];
            LizardGraphics.LizardSpineData spine = lGraphics.SpinePosition(t, timeStacker);
            Vector2 outward = GetSurfaceOutward(spine);
            Vector2 tangent = SafeNormal(spine.dir, Vector2.right);

            float variation = _spikeVariation[i];
            float lean = SpikeLeans[i] + Mathf.Lerp(-0.08f, 0.08f, variation);
            Vector2 spikeDir = SafeNormal(outward + tangent * lean, outward);

            float rootLift = Mathf.Lerp(1.9f, 1.0f, t);
            Vector2 drawPos = spine.outerPos + outward * rootLift;

            float length = SpikeLengths[i] * Mathf.Lerp(0.92f, 1.10f, variation);
            float thickness = Mathf.Lerp(0.40f, 0.57f, variation);
            if (length > 3f)
            {
                thickness *= 1.08f;
            }

            FSprite spike = sLeaser.sprites[startSprite + SpikeStart + i];
            spike.x = drawPos.x - camPos.x;
            spike.y = drawPos.y - camPos.y;
            spike.rotation = Custom.AimFromOneVectorToAnother(-spikeDir, spikeDir);
            spike.scaleX = ((i % 2 == 0) ? 1f : -1f) * thickness;
            spike.scaleY = length;

            // The small facial/neck crest stays brown like the concept drawing;
            // the two large dorsal crowns and tail spines are almost black.
            spike.color = i < 5
                ? Color.Lerp(backColor, spikeColor, 0.36f)
                : spikeColor;
            spike.alpha = 1f;
        }
    }

    private static Vector2 GetSurfaceOutward(LizardGraphics.LizardSpineData spine)
    {
        return SafeNormal(
            spine.outerPos - spine.pos,
            SafeNormal(spine.perp * spine.depthRotation, Vector2.up));
    }

    private static Vector2 SafeNormal(Vector2 value, Vector2 fallback)
    {
        if (value.sqrMagnitude < 0.0001f)
        {
            return fallback;
        }

        value.Normalize();
        return value;
    }
}
