using UnityEngine;

namespace DryCycle.Creatures.MantleCrab.Rendering;

/// <summary>CPU equivalent of MaterialBake.compute. Only initialization evaluates layered noise.</summary>
internal static class MantleCrabMaterialBaker
{
    internal static void Sample(MantleCrabVisualPhenotype p, MantleCrabMaterial kind, Vector2 uv,
        out Color pattern, out Color surface)
    {
        float u = uv.x, v = uv.y, wing = Mathf.Abs(u * 2f - 1f);
        float mutation = u < .5f ? p.NoiseSeed.z : p.NoiseSeed.w;
        float n = MantleCrabRenderingMath.Noise(wing * 5f + p.NoiseSeed.x, v * 6f + mutation * p.Asymmetry);
        float meso = MantleCrabRenderingMath.Noise(u * 17f + p.NoiseSeed.x, v * 12f + p.NoiseSeed.y);
        float micro = MantleCrabRenderingMath.Noise(u * 83f + mutation, v * 91f + p.NoiseSeed.y);
        float plate = MantleCrabRenderingMath.PlateSeam((kind == MantleCrabMaterial.Shell ? wing : u) * 12f + p.NoiseSeed.x,
            v * (kind == MantleCrabMaterial.Shell ? 8f : 3f) + p.NoiseSeed.y + mutation * p.Asymmetry);
        float band = Mathf.Pow(.5f + .5f * Mathf.Cos(wing*(p.MotifScale+8f)*3.141593f + (1-v)*(2f+wing*(2f+2f*p.Curvature)) +
            (n-.5f)*p.MotifWarp*3f + p.StripePhase), p.MotifSharpness+1f);
        band *= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(p.Fragmentation, p.Fragmentation + .18f, meso));
        float joint = Mathf.Pow(Mathf.Abs(u * 2 - 1), 12f);
        float ridge = Mathf.Abs(Mathf.Sin((u * p.LegDetail + meso * .12f) * 6.283185f));
        float height = .45f + (meso - .5f) * .12f + (micro - .5f) * .025f;
        float ao = .8f, rough = p.ChitinRoughness, emission = 0f;
        Color color;
        switch (kind)
        {
            case MantleCrabMaterial.Shell:
                float wingMask = Mathf.SmoothStep(0,1,Mathf.InverseLerp(.38f,.8f,wing));
                color = Color.Lerp(new Color(.018f,.015f,.042f),new Color(.20f,.22f,.28f),wingMask);
                color = Color.Lerp(color,new Color(.38f,.075f,.15f+p.Hue),band*p.MotifContrast*wingMask*(.55f+v*.45f));
                float crown = Mathf.SmoothStep(0,1,Mathf.InverseLerp(.65f,.95f,v))*(1-wingMask);
                color = Color.Lerp(color,new Color(.14f,.052f,.08f),crown*.75f);
                height += band * .08f;
                ao = Mathf.Lerp(.4f, .95f, Mathf.SmoothStep(0f, 1f, v * 3f));
                rough = Mathf.Lerp(.94f, .68f, wing);
                break;
            case MantleCrabMaterial.Pincer:
                color = Color.Lerp(new Color(.055f, .012f, .034f), new Color(.24f, .035f, .08f), p.PincerAccent + meso * .12f);
                ao = .9f - .48f * joint; rough = .53f; height += ridge * .06f; break;
            case MantleCrabMaterial.Eye:
                color = new Color(.8f, .018f + p.EyeWarmth * .025f, .035f);
                float core = 1f - Mathf.SmoothStep(0f, 1f, Vector2.Distance(uv, new Vector2(.5f, .5f)) / p.EyeCore);
                emission = .22f + core * .48f; rough = .15f; ao = 1f; height = .5f; break;
            case MantleCrabMaterial.Foot:
                color = Color.Lerp(new Color(.065f, .045f, .12f), new Color(.13f, .11f, .21f), meso * .4f);
                ao = .58f + v * .27f; rough = .72f; break;
            case MantleCrabMaterial.Joint:
                color = new Color(.085f, .075f, .16f); ao = .48f + ridge * .25f; rough = .7f; break;
            case MantleCrabMaterial.Fringe:
                color = new Color(.11f, .055f, .16f); ao = .65f; break;
            default:
                color = Color.Lerp(new Color(.105f, .17f, .23f + p.Hue), new Color(.23f, .31f, .38f), meso * .15f);
                ao = .96f - joint * .22f; rough = .5f; height += ridge * .012f; break;
        }
        if (kind != MantleCrabMaterial.Eye) { height -= plate * .045f; ao *= 1f - plate * .18f; rough = Mathf.Clamp01(rough + (micro - .5f) * .08f); }
        pattern = new Color(color.r, color.g, color.b, Mathf.Clamp01(height));
        surface = new Color(ao, rough, (int)kind / 6f, emission);
    }

    internal static Texture2D BakeCPU(MantleCrabVisualPhenotype phenotype)
    {
        const int size = MantleCrabMeshBuilder.TileSize, width = size * 7;
        Texture2D texture = new(width, size * 2, TextureFormat.RGBA32, false, true);
        try
        {
            Color32[] pixels = new Color32[width * size * 2];
            for (int kind = 0; kind < 7; kind++)
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Sample(phenotype, (MantleCrabMaterial)kind, new Vector2(x / (float)(size - 1), y / (float)(size - 1)),
                    out Color pattern, out Color surface);
                pixels[y * width + kind * size + x] = pattern;
                pixels[(y + size) * width + kind * size + x] = surface;
            }
            texture.SetPixels32(pixels); texture.Apply(false, true);
            return texture;
        }
        catch { Object.Destroy(texture); throw; }
    }
}
