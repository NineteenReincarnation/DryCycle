using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>唯一接触相机 Sprite 数字下标的适配层；每个 SpriteLeaser 持有独立 TriangleMesh。</summary>
internal sealed class IteratorGraphicsHost : GraphicsModule
{
    private readonly IteratorGraphics _graphics;
    private readonly List<View> _views = new();
    private readonly int[] _order;
    private readonly HashSet<string> _missingAssets = new(StringComparer.Ordinal);
    internal int ViewCount => _views.Count;

    internal IteratorGraphicsHost(PhysicalObject owner, IteratorGraphics graphics) : base(owner, false)
    {
        _graphics = graphics;
        cullRange = -1f; culled = lastCulled = false;
        _order = new int[graphics.Sprites.Entries.Count];
        for (int i = 0; i < _order.Length; i++) _order[i] = i;
        Array.Sort(_order, (a, b) =>
        {
            int layer = graphics.Sprites.Entries[a].Layer.CompareTo(graphics.Sprites.Entries[b].Layer);
            return layer == 0 ? a.CompareTo(b) : layer;
        });
    }

    // Runtime advances animations exactly once after physics. Cameras only interpolate.
    public override void Update() { culled = lastCulled = false; PruneViews(); }
    public override void Reset() { }

    public override void InitiateSprites(RoomCamera.SpriteLeaser leaser, RoomCamera camera)
    {
        leaser.sprites = Array.Empty<FSprite>();
        if (_graphics.IsDestroyed || !_graphics.IsEnabled) return;
        var sprites = new FSprite[_order.Length];
        try
        {
            _graphics.LoadAssets();
            for (int i = 0; i < sprites.Length; i++)
            {
                SpriteHandle entry = _graphics.Sprites.Entries[i];
                IteratorMesh data = entry.Mesh;
                int triangleCount = data.Indices.Length / 3;
                // TriangleMesh sizes its buffers from the greatest index, including
                // when extension meshes intentionally leave trailing vertices unused.
                var triangles = new TriangleMesh.Triangle[triangleCount + 1];
                for (int j = 0; j < triangleCount; j++) triangles[j] = new TriangleMesh.Triangle(data.Indices[j * 3], data.Indices[j * 3 + 1], data.Indices[j * 3 + 2]);
                int last = data.Points.Length - 1;
                triangles[triangleCount] = new TriangleMesh.Triangle(last, last, last);
                string element = entry.Element;
                if (!Futile.atlasManager.DoesContainElementWithName(element)) { Missing(element); element = "Futile_White"; }
                var mesh = new TriangleMesh(element, triangles, true, true);
                sprites[i] = mesh;
                Rect uv = mesh.element.uvRect;
                for (int j = 0; j < data.UVs.Length; j++) mesh.UVvertices[j] = new Vector2(uv.x + data.UVs[j].x * uv.width, uv.y + data.UVs[j].y * uv.height);
                if (!camera.game.rainWorld.Shaders.TryGetValue(entry.Shader, out FShader shader))
                { Missing(entry.Shader); shader = camera.game.rainWorld.Shaders["Basic"]; }
                mesh.shader = shader;
            }
            leaser.sprites = sprites;
            _views.Add(new View { Leaser = leaser, Camera = camera });
            AddToContainer(leaser, camera, null);
        }
        catch (Exception exception)
        {
            // Engine SpriteLeaser cleanup assumes every entry is non-null.
            foreach (FSprite sprite in sprites) SafeRemove(sprite);
            leaser.sprites = Array.Empty<FSprite>();
            _graphics.Disable("InitiateSprites", exception);
        }
    }

    public override void AddToContainer(RoomCamera.SpriteLeaser leaser, RoomCamera camera, FContainer container)
    {
        if (leaser.sprites.Length != _order.Length) return;
        container ??= camera.ReturnFContainer("Midground");
        foreach (int index in _order)
        {
            FSprite sprite = leaser.sprites[index];
            sprite.RemoveFromContainer(); container.AddChild(sprite);
        }
    }

    public override void ApplyPalette(RoomCamera.SpriteLeaser leaser, RoomCamera camera, RoomPalette palette)
    {
        foreach (View view in _views) if (ReferenceEquals(view.Leaser, leaser)) { view.Black = palette.blackColor; break; }
    }

    public override void DrawSprites(RoomCamera.SpriteLeaser leaser, RoomCamera camera, float timeStacker, Vector2 camPos)
    {
        if (_graphics.IsDestroyed || owner.slatedForDeletetion || !ReferenceEquals(owner.room, camera.room) || dispose)
        { Clean(leaser); PruneViews(); return; }
        if (leaser.sprites.Length != _order.Length) return;
        try
        {
            _graphics.Render(timeStacker);
            if (_graphics.IsDestroyed) return;
            Color black = Color.black;
            foreach (View view in _views) if (ReferenceEquals(view.Leaser, leaser)) { black = view.Black; break; }
            float influence = _graphics.Profile.PaletteInfluence * (1f - _graphics.Profile.Glow);
            for (int i = 0; i < leaser.sprites.Length; i++)
            {
                SpriteHandle entry = _graphics.Sprites.Entries[i];
                var mesh = (TriangleMesh)leaser.sprites[i];
                mesh.isVisible = _graphics.IsEnabled && entry.Visible && (entry.Owner == null || entry.Owner.IsEnabled);
                if (!mesh.isVisible) continue;
                for (int j = 0; j < entry.Mesh.Points.Length; j++)
                {
                    mesh.MoveVertice(j, entry.Mesh.Points[j] - camPos);
                    Color color = entry.Mesh.Tints[j];
                    Color shaded = Color.Lerp(color, black, influence); shaded.a = color.a;
                    mesh.verticeColors[j] = shaded;
                }
                mesh.Refresh();
            }
        }
        catch (Exception exception) { _graphics.Disable("DrawSprites", exception); Clean(leaser); }
    }

    internal void PruneViews()
    {
        for (int i = _views.Count - 1; i >= 0; i--)
        {
            View view = _views[i];
            if (!view.Leaser.deleteMeNextFrame && ReferenceEquals(view.Camera.room, owner.room) && !_graphics.IsDestroyed) continue;
            Clean(view.Leaser); _views.RemoveAt(i);
        }
    }
    internal void ReleaseViews()
    {
        foreach (View view in _views) Clean(view.Leaser);
        _views.Clear(); dispose = true;
    }
    private void Clean(RoomCamera.SpriteLeaser leaser)
    {
        if (leaser.sprites != null) foreach (FSprite sprite in leaser.sprites) SafeRemove(sprite);
        leaser.sprites = Array.Empty<FSprite>(); leaser.deleteMeNextFrame = true;
    }
    private void SafeRemove(FSprite sprite)
    {
        try { sprite?.RemoveFromContainer(); }
        catch (Exception exception) { _graphics.Log.ForPhase("Detach").Error("Sprite detach failed.", exception); }
    }
    private void Missing(string name)
    {
        if (_missingAssets.Add(name)) _graphics.Log.ForPhase("Assets").Warn("Missing visual asset; using default: " + name);
    }
    private sealed class View
    {
        internal RoomCamera.SpriteLeaser Leaser;
        internal RoomCamera Camera;
        internal Color Black = Color.black;
    }
}
