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
        float band = Mathf.Pow(.5f + .5f * Mathf.Cos(wing * (p.MotifScale + 15f) * 3.141593f +
            (1f - v) * (6f + wing * (8f + 5f * p.Curvature)) +
            (1f - v) * (1f - v) * 7f * wing +
            (n - .5f) * p.MotifWarp * 3f + p.StripePhase), p.MotifSharpness + 1f);
        band *= .76f + .24f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(p.Fragmentation, p.Fragmentation + .18f, meso));
        float endCollar = Mathf.Pow(Mathf.Abs(u * 2f - 1f), 12f);
        float ridge = Mathf.Abs(Mathf.Sin((u * p.LegDetail + meso * .12f) * 6.283185f));
        float height = .45f + (meso - .5f) * .12f + (micro - .5f) * .025f;
        float ao = .8f, rough = p.ChitinRoughness, emission = 0f, anatomy = 0f;
        Color color;

        switch (kind)
        {
            case MantleCrabMaterial.Shell:
            {
                float wingMask = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.30f, .69f, wing));
                color = Color.Lerp(new Color(.021f, .018f, .048f), new Color(.24f, .27f, .33f), wingMask);
                color = Color.Lerp(color, new Color(.48f, .065f, .17f + p.Hue),
                    band * p.MotifContrast * wingMask * (.55f + v * .45f));
                float crown = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.65f, .95f, v)) * (1f - wingMask);
                color = Color.Lerp(color, new Color(.24f, .070f, .125f), crown * .75f);
                height += band * .08f;
                ao = Mathf.Lerp(.4f, .95f, Mathf.SmoothStep(0f, 1f, v * 3f));
                rough = Mathf.Lerp(.94f, .68f, wing);
                anatomy = band * wingMask;
                break;
            }
            case MantleCrabMaterial.Pincer:
            {
                // A broad warm front plane turns into a cool, receding lateral plane. Clouded
                // pigment follows the length; fine relief stays out of the RGB silhouette.
                float face = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-.65f, .48f, lateral));
                float pigment = MantleCrabRenderingMath.Noise(u * 5f + p.NoiseSeed.x, v * 2f + p.NoiseSeed.y);
                Color front = Color.Lerp(new Color(.49f, .067f, .18f), new Color(.30f, .060f, .19f), pigment * .30f);
                Color side = new(.070f, .048f, .125f);
                color = Color.Lerp(side, front, face) * (.82f + .30f * p.PincerAccent);
                color *= .94f + .09f * n;
                float coolEdge = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-.99f, -.80f, lateral));
                color = Color.Lerp(color, new Color(.10f, .17f, .23f), coolEdge * .38f);
                anatomy = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.72f, .98f, Mathf.Abs(lateral)));
                ao = .97f - .10f * endCollar;
                rough = .72f;
                height = .48f + Mathf.Sqrt(Mathf.Max(0f, 1f - lateral * lateral)) * .075f +
                         (n - .5f) * .018f + (micro - .5f) * .006f;
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
                float growthBand = Mathf.Pow(Mathf.Max(0f, Mathf.Cos((u * 2.6f + .3f) * Mathf.PI)), 8f) * Mathf.Clamp01((u - .25f) / .6f);
                color = Color.Lerp(color, new Color(.075f, .025f, .115f), growthBand * .55f);
                height += heelPlate * .045f;
                anatomy = sole;
                break;
            }
            case MantleCrabMaterial.Joint:
            {
                // Dark underside of the proximal shell, with a thin reflected back rim.
                // It is not an all-around ring: UV.x runs from the back lip toward the front lip.
                float lip = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.04f, .30f, u));
                color = Color.Lerp(new Color(.022f, .014f, .038f), new Color(.12f, .105f, .19f), lip * .70f);
                color *= .94f + meso * .08f;
                ao = .72f;
                rough = .86f;
                height = .48f + lip * .018f;
                anatomy = 1f - lip;
                break;
            }
            case MantleCrabMaterial.Fringe:
                color = new Color(.075f, .10f, .15f);
                ao = .65f;
                rough = .76f;
                break;
            default:
            {
                color = Color.Lerp(new Color(.20f, .32f, .39f + p.Hue),
                    new Color(.29f, .43f, .50f), meso * .15f);
                float collar = endCollar;
                ao = .96f - collar * .16f;
                rough = .52f;
                color *= Mathf.Lerp(.62f, 1.16f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-.25f, .15f, lateral)));
                height += (1f - Mathf.Abs(lateral)) * .035f;
                anatomy = Mathf.Clamp01(plate * .65f + collar * .20f);
                break;
            }
        }

        if (kind != MantleCrabMaterial.Eye)
        {
            height -= plate * (kind == MantleCrabMaterial.Shell ? .025f : .006f);
            ao *= 1f - plate * (kind == MantleCrabMaterial.Shell ? .10f : .025f);
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
