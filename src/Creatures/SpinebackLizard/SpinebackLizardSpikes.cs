using LizardCosmetics;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures;

/// <summary>
/// Custom thorny-devil surface for Spineback Lizard. In normal movement the sprites
/// form pale armored plates, rust/dark patches and many short conical spines along a
/// broad body. While defending, the same surface pieces fold into a compact armored
/// sphere instead of stretching the vanilla lizard mesh.
/// </summary>
internal sealed class SpinebackLizardSpikes : Template
{
    private const int OuterShellSprite = 0;
    private const int InnerShellSprite = 1;

    private const int PlateStart = 2;
    private const int PlateCount = 18;

    private const int PatchStart = PlateStart + PlateCount;
    private const int PatchCount = 10;

    private const int SpikeStart = PatchStart + PatchCount;
    private const int SpikeCount = 28;

    private readonly float _phase;
    private readonly float[] _plateVariation = new float[PlateCount];
    private readonly float[] _patchVariation = new float[PatchCount];
    private readonly float[] _spikeVariation = new float[SpikeCount];

    internal SpinebackLizardSpikes(LizardGraphics graphics, int startSprite)
        : base(graphics, startSprite)
    {
        spritesOverlap = SpritesOverlap.InFront;
        numberOfSprites = SpikeStart + SpikeCount;

        int seed = graphics?.lizard?.abstractCreature?.ID.RandomSeed ?? 0;
        _phase = Mathf.Repeat(seed * 37.173f, 360f);

        for (int i = 0; i < PlateCount; i++)
        {
            _plateVariation[i] = SpinebackLizardHooks.Stable01(seed + 3001 + i * 97);
        }

        for (int i = 0; i < PatchCount; i++)
        {
            _patchVariation[i] = SpinebackLizardHooks.Stable01(seed + 5003 + i * 131);
        }

        for (int i = 0; i < SpikeCount; i++)
        {
            _spikeVariation[i] = SpinebackLizardHooks.Stable01(seed + 7001 + i * 163);
        }
    }

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        FSprite outerShell = new FSprite("Circle20");
        outerShell.alpha = 0f;
        sLeaser.sprites[startSprite + OuterShellSprite] = outerShell;

        FSprite innerShell = new FSprite("Circle20");
        innerShell.alpha = 0f;
        sLeaser.sprites[startSprite + InnerShellSprite] = innerShell;

        for (int i = 0; i < PlateCount; i++)
        {
            FSprite plate = new FSprite("Circle20");
            plate.anchorX = 0.5f;
            plate.anchorY = 0.5f;
            sLeaser.sprites[startSprite + PlateStart + i] = plate;
        }

        for (int i = 0; i < PatchCount; i++)
        {
            FSprite patch = new FSprite("Circle20");
            patch.anchorX = 0.5f;
            patch.anchorY = 0.5f;
            sLeaser.sprites[startSprite + PatchStart + i] = patch;
        }

        for (int i = 0; i < SpikeCount; i++)
        {
            FSprite spike = new FSprite("LizardScaleA3");
            // Match the way vanilla/LizKin spine spikes are anchored: the root sits
            // close to the bottom of the sprite and almost all visible length points
            // away from the body surface.
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

        float defense = SpinebackLizardHooks.GetDefenseProgress(lizard);
        float ballBlend = Mathf.SmoothStep(
            0f,
            1f,
            Mathf.InverseLerp(0.14f, 0.82f, defense));

        Vector2 center = GetVisualCenter(lizard, timeStacker);
        float breath = 1f + Mathf.Sin(Time.time * 5.2f + _phase * 0.017453292f) * 0.018f * ballBlend;

        Color bodyColor = SpinebackLizardHooks.ShadeForRoom(
            lizard,
            rCam,
            SpinebackLizardHooks.GetBodyColor(lizard));
        Color plateColor = SpinebackLizardHooks.ShadeForRoom(
            lizard,
            rCam,
            SpinebackLizardHooks.GetPlateColor(lizard));
        Color rustColor = SpinebackLizardHooks.ShadeForRoom(
            lizard,
            rCam,
            SpinebackLizardHooks.GetRustColor(lizard));
        Color darkColor = SpinebackLizardHooks.ShadeForRoom(
            lizard,
            rCam,
            SpinebackLizardHooks.GetDarkColor(lizard));

        DrawBallShell(sLeaser, center, camPos, ballBlend, breath, bodyColor, darkColor);
        DrawPlates(sLeaser, timeStacker, camPos, center, ballBlend, plateColor, rustColor);
        DrawPatches(sLeaser, timeStacker, camPos, center, ballBlend, rustColor, darkColor);
        DrawSpikes(sLeaser, timeStacker, camPos, center, ballBlend, plateColor, darkColor);
    }

    public override void ApplyPalette(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        RoomPalette palette)
    {
        base.ApplyPalette(sLeaser, rCam, palette);
    }

    private void DrawBallShell(
        RoomCamera.SpriteLeaser sLeaser,
        Vector2 center,
        Vector2 camPos,
        float ballBlend,
        float breath,
        Color bodyColor,
        Color darkColor)
    {
        FSprite outer = sLeaser.sprites[startSprite + OuterShellSprite];
        outer.x = center.x - camPos.x;
        outer.y = center.y - camPos.y;
        outer.scaleX = Mathf.Lerp(0.35f, 2.12f, ballBlend) * breath;
        outer.scaleY = Mathf.Lerp(0.30f, 1.92f, ballBlend) * breath;
        outer.color = darkColor;
        outer.alpha = Mathf.SmoothStep(0f, 1f, ballBlend);

        FSprite inner = sLeaser.sprites[startSprite + InnerShellSprite];
        inner.x = center.x - camPos.x;
        inner.y = center.y - camPos.y + 0.5f;
        inner.scaleX = Mathf.Lerp(0.30f, 1.92f, ballBlend) * breath;
        inner.scaleY = Mathf.Lerp(0.26f, 1.73f, ballBlend) * breath;
        inner.color = bodyColor;
        inner.alpha = Mathf.SmoothStep(0f, 1f, ballBlend);
    }

    private void DrawPlates(
        RoomCamera.SpriteLeaser sLeaser,
        float timeStacker,
        Vector2 camPos,
        Vector2 center,
        float ballBlend,
        Color plateColor,
        Color rustColor)
    {
        for (int i = 0; i < PlateCount; i++)
        {
            float t = Mathf.Lerp(0.035f, 0.74f, i / (float)(PlateCount - 1));
            LizardGraphics.LizardSpineData spine = lGraphics.SpinePosition(t, timeStacker);

            Vector2 normal = SafeNormal(spine.perp, Vector2.up);
            float side = (i % 2 == 0) ? 1f : -1f;
            float sideOffset = spine.rad * Mathf.Lerp(0.22f, 0.58f, _plateVariation[i]);
            Vector2 normalPos = spine.pos + normal * side * sideOffset;

            float angle = _phase + i * (360f / PlateCount) + _plateVariation[i] * 13f;
            Vector2 radial = Custom.DegToVec(angle);
            float ring = Mathf.Lerp(5.5f, 12.5f, _plateVariation[i]);
            Vector2 ballPos = center + radial * ring;

            Vector2 drawPos = Vector2.Lerp(normalPos, ballPos, ballBlend);
            Vector2 normalDir = SafeNormal(spine.dir, Vector2.right);
            Vector2 ballTangent = new Vector2(-radial.y, radial.x);
            Vector2 drawDir = SafeNormal(Vector2.Lerp(normalDir, ballTangent, ballBlend), Vector2.right);

            FSprite plate = sLeaser.sprites[startSprite + PlateStart + i];
            plate.x = drawPos.x - camPos.x;
            plate.y = drawPos.y - camPos.y;
            plate.rotation = Custom.VecToDeg(drawDir);
            plate.scaleX = Mathf.Lerp(
                Mathf.Lerp(0.25f, 0.43f, _plateVariation[i]),
                Mathf.Lerp(0.34f, 0.50f, _plateVariation[i]),
                ballBlend);
            plate.scaleY = Mathf.Lerp(
                Mathf.Lerp(0.18f, 0.31f, _plateVariation[i]),
                Mathf.Lerp(0.25f, 0.39f, _plateVariation[i]),
                ballBlend);
            plate.color = (i % 5 == 0) ? Color.Lerp(plateColor, rustColor, 0.24f) : plateColor;
            plate.alpha = 1f;
        }
    }

    private void DrawPatches(
        RoomCamera.SpriteLeaser sLeaser,
        float timeStacker,
        Vector2 camPos,
        Vector2 center,
        float ballBlend,
        Color rustColor,
        Color darkColor)
    {
        for (int i = 0; i < PatchCount; i++)
        {
            float t = Mathf.Lerp(0.06f, 0.70f, i / (float)(PatchCount - 1));
            LizardGraphics.LizardSpineData spine = lGraphics.SpinePosition(t, timeStacker);
            Vector2 normal = SafeNormal(spine.perp, Vector2.up);
            float side = (i % 2 == 0) ? -1f : 1f;
            Vector2 normalPos = spine.pos + normal * side * spine.rad * Mathf.Lerp(0.08f, 0.36f, _patchVariation[i]);

            float angle = _phase + 21f + i * (360f / PatchCount);
            Vector2 radial = Custom.DegToVec(angle);
            Vector2 ballPos = center + radial * Mathf.Lerp(4f, 10f, _patchVariation[i]);
            Vector2 drawPos = Vector2.Lerp(normalPos, ballPos, ballBlend);

            FSprite patch = sLeaser.sprites[startSprite + PatchStart + i];
            patch.x = drawPos.x - camPos.x;
            patch.y = drawPos.y - camPos.y;
            patch.rotation = angle;
            patch.scaleX = Mathf.Lerp(0.20f, 0.39f, _patchVariation[i]) * Mathf.Lerp(1f, 1.15f, ballBlend);
            patch.scaleY = Mathf.Lerp(0.15f, 0.30f, _patchVariation[i]) * Mathf.Lerp(1f, 1.12f, ballBlend);
            patch.color = (i % 3 == 0) ? darkColor : rustColor;
            patch.alpha = Mathf.Lerp(0.80f, 0.94f, ballBlend);
        }
    }

    private void DrawSpikes(
        RoomCamera.SpriteLeaser sLeaser,
        float timeStacker,
        Vector2 camPos,
        Vector2 center,
        float ballBlend,
        Color plateColor,
        Color darkColor)
    {
        for (int i = 0; i < SpikeCount; i++)
        {
            float t = Mathf.Lerp(0.005f, 0.90f, i / (float)(SpikeCount - 1));
            LizardGraphics.LizardSpineData spine = lGraphics.SpinePosition(t, timeStacker);

            // Use the actual visible body surface, not spine.pos + rad. The previous
            // version could place roots inside the body mesh. outerPos is also what
            // Rain World's normal spine-spike cosmetics use.
            Vector2 outward = SafeNormal(
                spine.outerPos - spine.pos,
                SafeNormal(spine.perp * spine.depthRotation, Vector2.up));
            Vector2 tangent = SafeNormal(spine.dir, Vector2.right);

            // Keep every normal-state thorn pointing out of the visible silhouette.
            // A small fore/aft lean prevents them from looking like a perfect comb
            // without flipping any of them back into the body.
            float lean = Mathf.Lerp(-0.42f, 0.42f, _spikeVariation[i]);
            if (t < 0.16f)
            {
                lean += (i % 2 == 0) ? -0.20f : 0.20f;
            }
            Vector2 normalDir = SafeNormal(outward + tangent * lean, outward);

            float hornFactor;
            if (t < 0.11f)
            {
                // Large head/neck horns are a defining thorny-devil silhouette.
                hornFactor = Mathf.Lerp(2.75f, 3.45f, _spikeVariation[i]);
            }
            else if (t < 0.32f)
            {
                hornFactor = Mathf.Lerp(2.05f, 2.75f, _spikeVariation[i]);
            }
            else if (t < 0.68f)
            {
                hornFactor = Mathf.Lerp(1.45f, 2.15f, _spikeVariation[i]);
            }
            else
            {
                hornFactor = Mathf.Lerp(1.05f, 1.70f, _spikeVariation[i]);
            }

            // Push the root a few pixels outside the mesh so the base cannot be
            // swallowed by the wide body silhouette.
            float rootLift = Mathf.Lerp(2.8f, 1.4f, t);
            Vector2 normalPos = spine.outerPos + outward * rootLift;

            float radialAngle = _phase + i * (360f / SpikeCount) + _spikeVariation[i] * 9f;
            Vector2 radialDir = Custom.DegToVec(radialAngle);
            float radialDistance = Mathf.Lerp(18.5f, 24.5f, _spikeVariation[i]) + hornFactor * 2.0f;
            Vector2 ballPos = center + radialDir * radialDistance;

            Vector2 drawPos = Vector2.Lerp(normalPos, ballPos, ballBlend);
            Vector2 spikeDir = SafeNormal(Vector2.Lerp(normalDir, radialDir, ballBlend), radialDir);

            FSprite spike = sLeaser.sprites[startSprite + SpikeStart + i];
            spike.x = drawPos.x - camPos.x;
            spike.y = drawPos.y - camPos.y;

            // LizardScaleA sprites already point along their local +Y axis. Vanilla
            // spine cosmetics use AimFromOneVectorToAnother directly; the old -90
            // degree offset rotated the thorns almost along the body and hid them.
            spike.rotation = Custom.AimFromOneVectorToAnother(-spikeDir, spikeDir);
            spike.scaleX = ((i % 2 == 0) ? 1f : -1f) *
                           Mathf.Lerp(0.70f, 1.05f, _spikeVariation[i]);
            spike.scaleY = hornFactor * Mathf.Lerp(1f, 1.12f, ballBlend);
            spike.color = Color.Lerp(plateColor, darkColor, Mathf.Lerp(0.08f, 0.28f, t));
            spike.alpha = 1f;
        }
    }

    private static Vector2 GetVisualCenter(Lizard lizard, float timeStacker)
    {
        Vector2 center = Vector2.zero;
        int count = Mathf.Min(3, lizard.bodyChunks.Length);
        for (int i = 0; i < count; i++)
        {
            center += Vector2.Lerp(lizard.bodyChunks[i].lastPos, lizard.bodyChunks[i].pos, timeStacker);
        }
        return count > 0 ? center / count : lizard.mainBodyChunk.pos;
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
