using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

// The only Unity-dependent asset adapter. Read each atlas once, on the game thread.
internal static class CartographyGameAssets
{
    private static readonly HashSet<string> pending =
        new(StringComparer.Ordinal);
    private static int nextRetryTick;

    internal static bool HasPending =>
        pending.Count > 0;

    internal static void Load(CartographySource source)
    {
        string[] standard = { "ShelterMarker", "ChieftainA", "Symbol_Pearl", "Sandbox_Unlock", "Kill_Slugcat",
            "karma0", "karma1", "karma2", "karma3", "karma4", "karma5-9", "karma6-9", "karma7-9", "karma8-9", "karma9-9" };

        pending.Clear();

        foreach (string name in standard
            .Concat(CartographyIconCatalog.Objects.Values.Select(v => v.Sprite))
            .Concat(source?.Decorations?.Select(i => i.Appearance.Icon) ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal))
        {
            if (CartographyAssets.Sprite(name) == null)
                pending.Add(name);
        }

        TryLoadPending(force: true);
    }

    /// <summary>
    /// Futile/mod atlases are not guaranteed to contain every element at the exact frame the
    /// Cartography source becomes ready. Retry missing raw atlas elements on the game thread; when
    /// anything arrives, rebuild the semantic Object_* aliases as well.
    /// </summary>
    internal static bool RetryPending() =>
        TryLoadPending(force: false);

    private static bool TryLoadPending(bool force)
    {
        if (pending.Count == 0)
            return false;

        int now =
            Environment.TickCount;

        if (!force &&
            unchecked(now - nextRetryTick) < 0)
            return false;

        nextRetryTick =
            unchecked(now + 750);

        bool changed = false;
        var atlases =
            new Dictionary<Texture, Color32[]>();

        foreach (string name in pending.ToArray())
        {
            if (CartographyAssets.Sprite(name) != null)
            {
                pending.Remove(name);
                changed = true;
                continue;
            }

            if (!Futile.atlasManager.DoesContainElementWithName(name))
                continue;

            try
            {
                FAtlasElement element =
                    Futile.atlasManager.GetElementWithName(name);
                Texture texture =
                    element.atlas.texture;

                if (!atlases.TryGetValue(
                        texture,
                        out Color32[] pixels))
                {
                    atlases[texture] =
                        pixels =
                            ReadTexture(texture);
                }

                CartographyAssets.Register(
                    name,
                    ReadSprite(
                        element,
                        pixels));
                pending.Remove(name);
                changed = true;
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogError(
                    "Cartography atlas readback failed for '" +
                    name +
                    "': " +
                    error);
            }
        }

        if (changed)
            CartographyIconCatalog.RegisterObjectSprites();

        return changed;
    }

    internal static CartographyRaster ReadSprite(FAtlasElement element, Color32[] pixels = null)
    {
        Texture texture = element.atlas.texture;
        pixels ??= ReadTexture(texture);
        // FSprite renders these four corners. Repacked/flipped elements can differ from uvRect.
        Vector2 topLeft = element.uvTopLeft, topRight = element.uvTopRight;
        Vector2 bottomLeft = element.uvBottomLeft, bottomRight = element.uvBottomRight;
        Vector2 texel = new(texture.width, texture.height);
        int w = Mathf.RoundToInt(Vector2.Scale(topRight - topLeft, texel).magnitude);
        int h = Mathf.RoundToInt(Vector2.Scale(bottomLeft - topLeft, texel).magnitude);
        if (w < 1 || h < 1 || w > texture.width || h > texture.height)
            throw new InvalidOperationException("Invalid atlas frame for " + element.name);
        uint[] result = new uint[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float u = (x + .5f) / w, v = (y + .5f) / h;
            Vector2 uv = Vector2.Lerp(Vector2.Lerp(topLeft, topRight, u), Vector2.Lerp(bottomLeft, bottomRight, u), v);
            int sx = Mathf.FloorToInt(uv.x * texture.width), sy = Mathf.FloorToInt(uv.y * texture.height);
            if (sx < 0 || sy < 0 || sx >= texture.width || sy >= texture.height)
                throw new InvalidOperationException("Atlas UV outside texture for " + element.name);
            Color32 c = pixels[sy * texture.width + sx];
            result[y * w + x] = (uint)c.a << 24 | (uint)c.r << 16 | (uint)c.g << 8 | c.b;
        }
        return new CartographyRaster(w, h, result);
    }

    private static Color32[] ReadTexture(Texture texture)
    {
        if (texture is Texture2D readable && readable.isReadable) return readable.GetPixels32();
        RenderTexture previous = RenderTexture.active;
        RenderTexture target = null;
        Texture2D copy = null;
        try
        {
            target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
            target.filterMode = FilterMode.Point;
            Graphics.Blit(texture, target);
            RenderTexture.active = target;
            copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
            copy.Apply(false, false);
            return copy.GetPixels32();
        }
        finally
        {
            RenderTexture.active = previous;
            if (copy != null) UnityEngine.Object.Destroy(copy);
            if (target != null) RenderTexture.ReleaseTemporary(target);
        }
    }
}
