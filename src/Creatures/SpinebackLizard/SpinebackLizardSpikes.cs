using LizardCosmetics;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures;

/// <summary>
/// Custom Spineback surface based on the supplied concept art: a reddish-brown
/// dorsal mantle over a pale sand body, cream broken stripes, a five-piece brown
/// head-shell crest, and two groups of nine near-black dorsal spines.
/// </summary>
internal sealed class SpinebackLizardSpikes : Template
{
    private const int BackPatchStart = 0;
    private const int BackPatchCount = 20;

    private const int StripePairCount = 9;
    private const int StripeStart = BackPatchStart + BackPatchCount;
    private const int StripeSpriteCount = StripePairCount * 2;

    private const int SpineStart = StripeStart + StripeSpriteCount;
    private const int HeadCrestCount = 5;
    private const int ShoulderSpineCount = 5;
    private const int RearSpineCount = 4;
    private const int SpineCount = HeadCrestCount + ShoulderSpineCount + RearSpineCount;

    private static readonly float[] StripePositions =
    {
        0.27f, 0.34f, 0.41f, 0.49f, 0.57f, 0.65f, 0.73f, 0.81f, 0.89f
    };

    // 0-4: brown head-shell hair/crest.
    // 5-9: first black shoulder crown.
    // 10-13: second black rear crown.
    private static readonly float[] SpinePositions =
    {
        0.015f, 0.035f, 0.055f, 0.078f, 0.105f,
        0.155f, 0.195f, 0.235f, 0.280f, 0.330f,
        0.555f, 0.620f, 0.690f, 0.765f
    };

    private static readonly float[] SpineLengths =
    {
        1.05f, 1.18f, 1.30f, 1.42f, 1.55f,
        2.85f, 3.70f, 4.35f, 3.85f, 2.65f,
        2.55f, 3.45f, 3.90f, 2.75f
    };

    private static readonly float[] SpineLeans =
    {
        1.15f, 1.05f, 0.95f, 0.85f, 0.72f,
        0.05f, 0.22f, 0.42f, 0.62f, 0.82f,
        0.06f, 0.28f, 0.52f, 0.78f
    };

    private readonly float[] _backVariation = new float[BackPatchCount];
    private readonly float[] _stripeVariation = new float[StripePairCount];
    private readonly float[] _spineVariation = new float[SpineCount];

    internal SpinebackLizardSpikes(LizardGraphics graphics, int startSprite)
        : base(graphics, startSprite)
    {
        spritesOverlap = SpritesOverlap.InFront;
        numberOfSprites = SpineStart + SpineCount;

        int seed = graphics?.lizard?.abstractCreature?.ID.RandomSeed ?? 0;

        for (int i = 0; i < BackPatchCount; i++)
        {
            _backVariation[i] = SpinebackLizardHooks.Stable01(seed + 3001 + i * 97);
        }

        for (int i = 0; i < StripePairCount; i++)
        {
            _stripeVariation[i] = SpinebackLizardHooks.Stable01(seed + 5003 + i * 131);
        }

        for (int i = 0; i < SpineCount; i++)
        {
            _spineVariation[i] = SpinebackLizardHooks.Stable01(seed + 7001 + i * 163);
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

        for (int i = 0; i < SpineCount; i++)
        {
            FSprite spine = new FSprite("LizardScaleA3");
            spine.anchorY = 0.15f;
            sLeaser.sprites[startSprite + SpineStart + i] = spine;
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
        Color spineColor = SpinebackLizardHooks.ShadeForRoom(
            lizard,
            rCam,
            SpinebackLizardHooks.GetSpikeColor(lizard));

        DrawBackMantle(sLeaser, timeStacker, camPos, backColor);
        DrawBrokenStripes(sLeaser, timeStacker, camPos, stripeColor);
        DrawCrestAndSpines(sLeaser, timeStacker, camPos, backColor, spineColor);
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

    private void DrawCrestAndSpines(
        RoomCamera.SpriteLeaser sLeaser,
        float timeStacker,
        Vector2 camPos,
        Color backColor,
        Color spineColor)
    {
        for (int i = 0; i < SpineCount; i++)
        {
            float t = SpinePositions[i];
            LizardGraphics.LizardSpineData spineData = lGraphics.SpinePosition(t, timeStacker);
            Vector2 outward = GetSurfaceOutward(spineData);
            Vector2 tangent = SafeNormal(spineData.dir, Vector2.right);

            float variation = _spineVariation[i];
            float lean = SpineLeans[i] + Mathf.Lerp(-0.07f, 0.07f, variation);
            Vector2 direction = SafeNormal(outward + tangent * lean, outward);

            float rootLift = i < HeadCrestCount
                ? Mathf.Lerp(1.3f, 0.9f, t / 0.105f)
                : Mathf.Lerp(2.0f, 1.15f, t);
            Vector2 drawPos = spineData.outerPos + outward * rootLift;

            float length = SpineLengths[i] * Mathf.Lerp(0.94f, 1.07f, variation);
            float thickness;
            if (i < HeadCrestCount)
            {
                thickness = Mathf.Lerp(0.30f, 0.40f, variation);
            }
            else
            {
                thickness = Mathf.Lerp(0.43f, 0.58f, variation);
                if (length > 3f)
                {
                    thickness *= 1.08f;
                }
            }

            FSprite spine = sLeaser.sprites[startSprite + SpineStart + i];
            spine.x = drawPos.x - camPos.x;
            spine.y = drawPos.y - camPos.y;
            spine.rotation = Custom.AimFromOneVectorToAnother(-direction, direction);
            spine.scaleX = ((i % 2 == 0) ? 1f : -1f) * thickness;
            spine.scaleY = length;

            // The five front pieces are head-shell hair/crest, not dangerous black
            // dorsal spines, so they use the same reddish-brown as the head shell.
            spine.color = i < HeadCrestCount ? backColor : spineColor;
            spine.alpha = 1f;
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
