using UnityEngine;

namespace DryCycle.Creatures.MantleCrab.Rendering;

/// <summary>
/// CPU equivalent of MantleCrabMaterialBake.compute. The surface atlas stores AO in R,
/// roughness in G, a material-specific anatomical mask in B and emission in A. Material kind is
/// already encoded by the atlas tile, so the old redundant kind value in surface.B is now useful
/// structure data for the runtime shader.
/// </summary>
internal static class MantleCrabMaterialBaker
{
    internal static void Sample(MantleCrabVisualPhenotype p, MantleCrabMaterial kind, Vector2 uv,
        out Color pattern, out Color surface)
    {
        float u = uv.x, v = uv.y, wing = Mathf.Abs(u * 2f - 1f);
        float lateral = v * 2f - 1f;
        float mutation = u < .5f ? p.NoiseSeed.z : p.NoiseSeed.w;
        float n = MantleCrabRenderingMath.Noise(wing * 5f + p.NoiseSeed.x, v * 6f + mutation * p.Asymmetry);
        float meso = MantleCrabRenderingMath.Noise(u * 17f + p.NoiseSeed.x, v * 12f + p.NoiseSeed.y);
        float micro = MantleCrabRenderingMath.Noise(u * 83f + mutation, v * 91f + p.NoiseSeed.y);
        float plate = MantleCrabRenderingMath.PlateSeam((kind == MantleCrabMaterial.Shell ? wing : u) * 12f + p.NoiseSeed.x,
            v * (kind == MantleCrabMaterial.Shell ? 8f : 3f) + p.NoiseSeed.y + mutation * p.Asymmetry);
        float band = Mathf.Pow(.5f + .5f * Mathf.Cos(wing * (p.MotifScale + 8f) * 3.141593f +
            (1f - v) * (2f + wing * (2f + 2f * p.Curvature)) +
            (n - .5f) * p.MotifWarp * 3f + p.StripePhase), p.MotifSharpness + 1f);
        band *= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(p.Fragmentation, p.Fragmentation + .18f, meso));
        float endCollar = Mathf.Pow(Mathf.Abs(u * 2f - 1f), 12f);
        float ridge = Mathf.Abs(Mathf.Sin((u * p.LegDetail + meso * .12f) * 6.283185f));
        float height = .45f + (meso - .5f) * .12f + (micro - .5f) * .025f;
        float ao = .8f, rough = p.ChitinRoughness, emission = 0f, anatomy = 0f;
        Color color;

        switch (kind)
        {
            case MantleCrabMaterial.Shell:
            {
                float wingMask = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.38f, .8f, wing));
                color = Color.Lerp(new Color(.018f, .015f, .042f), new Color(.20f, .22f, .28f), wingMask);
                color = Color.Lerp(color, new Color(.38f, .075f, .15f + p.Hue),
                    band * p.MotifContrast * wingMask * (.55f + v * .45f));
                float crown = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.65f, .95f, v)) * (1f - wingMask);
                color = Color.Lerp(color, new Color(.14f, .052f, .08f), crown * .75f);
                height += band * .08f;
                ao = Mathf.Lerp(.4f, .95f, Mathf.SmoothStep(0f, 1f, v * 3f));
                rough = Mathf.Lerp(.94f, .68f, wing);
                anatomy = band * wingMask;
                break;
            }
            case MantleCrabMaterial.Pincer:
            {
                float shaftVariation = .82f + .18f * meso;
                color = Color.Lerp(new Color(.052f, .010f, .030f), new Color(.27f, .038f, .085f),
                    Mathf.Clamp01(p.PincerAccent * shaftVariation + .10f));
                float centerGroove = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.08f, .52f, Mathf.Abs(lateral)));
                color *= Mathf.Lerp(1f, .72f, centerGroove * .35f);
                anatomy = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.52f, .94f, Mathf.Abs(lateral))) *
                          Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.28f, .95f, u));
                ao = .91f - .34f * endCollar - centerGroove * .10f;
                rough = .50f;
                height += ridge * .055f;
                break;
            }
            case MantleCrabMaterial.Eye:
            {
                color = new Color(.8f, .018f + p.EyeWarmth * .025f, .035f);
                float core = 1f - Mathf.SmoothStep(0f, 1f,
                    Vector2.Distance(uv, new Vector2(.5f, .5f)) / p.EyeCore);
                emission = .22f + core * .48f;
                rough = .15f;
                ao = 1f;
                height = .5f;
                break;
            }
            case MantleCrabMaterial.Foot:
            {
                // Deliberate anatomical plate design, not free noise. UV.y is oriented consistently
                // by Foot(), so negative lateral is the darker inner plate on both left/right feet.
                Color blueGrey = Color.Lerp(new Color(.105f, .155f, .205f + p.Hue),
                    new Color(.205f, .270f, .315f), .32f + meso * .22f);
                Color innerPlateColor = new(.090f, .035f, .105f);
                float innerPlate = Mathf.SmoothStep(0f, 1f,
                    Mathf.Clamp01((-lateral - .02f) / .72f));
                float heelPlate = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(u - .57f) / .22f), 2f) *
                                  Mathf.Lerp(.35f, .72f, innerPlate);
                color = Color.Lerp(blueGrey, innerPlateColor,
                    Mathf.Clamp01(innerPlate * .78f + heelPlate * .22f));

                float sole = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.76f, .98f, u));
                float ankleBand = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(u - .18f) / .07f), 2f);
                color *= Mathf.Lerp(1f, .72f, sole * .55f);
                color = Color.Lerp(color, new Color(.07f, .055f, .105f), ankleBand * .32f);
                ao = .76f - sole * .28f - ankleBand * .10f;
                rough = Mathf.Lerp(.68f, .94f, sole);
                height += heelPlate * .045f;
                anatomy = sole;
                break;
            }
            case MantleCrabMaterial.Joint:
            {
                float dx = (u - .5f) / .52f;
                float dy = (v - .5f) / .72f;
                float radial = Mathf.Sqrt(dx * dx + dy * dy);
                float cavity = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.16f, .72f, radial));
                Color shell = new(.105f, .105f, .185f);
                Color membrane = new(.026f, .018f, .045f);
                color = Color.Lerp(shell, membrane, cavity * .78f);
                ao = Mathf.Lerp(.72f, .30f, cavity);
                rough = Mathf.Lerp(.62f, .90f, cavity);
                height -= cavity * .10f;
                anatomy = cavity;
                break;
            }
            case MantleCrabMaterial.Fringe:
                color = new Color(.11f, .055f, .16f);
                ao = .65f;
                rough = .76f;
                break;
            default:
            {
                color = Color.Lerp(new Color(.105f, .17f, .23f + p.Hue),
                    new Color(.23f, .31f, .38f), meso * .15f);
                float collar = endCollar;
                ao = .96f - collar * .16f;
                rough = .52f;
                height += ridge * .018f;
                anatomy = Mathf.Clamp01(plate * .65f + collar * .20f);
                break;
            }
        }

        if (kind != MantleCrabMaterial.Eye)
        {
            height -= plate * (kind == MantleCrabMaterial.Joint ? .018f : .045f);
            ao *= 1f - plate * (kind == MantleCrabMaterial.Foot ? .09f : .18f);
            rough = Mathf.Clamp01(rough + (micro - .5f) * .08f);
        }

        pattern = new Color(color.r, color.g, color.b, Mathf.Clamp01(height));
        surface = new Color(Mathf.Clamp01(ao), Mathf.Clamp01(rough), Mathf.Clamp01(anatomy), emission);
    }

    internal static Texture2D BakeCPU(MantleCrabVisualPhenotype phenotype)
    {
        const int size = MantleCrabMeshBuilder.TileSize;
        int width = size * MantleCrabMeshBuilder.MaterialCount;
        Texture2D texture = new(width, size * 2, TextureFormat.RGBA32, false, true);
        try
        {
            Color32[] pixels = new Color32[width * size * 2];
            for (int kind = 0; kind < MantleCrabMeshBuilder.MaterialCount; kind++)
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Sample(phenotype, (MantleCrabMaterial)kind,
                    new Vector2(x / (float)(size - 1), y / (float)(size - 1)),
                    out Color pattern, out Color surface);
                pixels[y * width + kind * size + x] = pattern;
                pixels[(y + size) * width + kind * size + x] = surface;
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }
        catch
        {
            Object.Destroy(texture);
            throw;
        }
    }
}
