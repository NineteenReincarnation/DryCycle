using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using ImGuiNET;
using RWIMGUI.API;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// RWImGUI 1.12 owns the D3D11 shader-resource view through ImGUITexture. Uploads and disposal
/// run on Unity's main thread; Present only consumes the published handle. A Unity texture pointer
/// is a D3D resource, not an ImGui image ID.
/// </summary>
internal sealed class WorldMapTextureBridge
{
    private sealed class ReferenceComparer : IEqualityComparer<Texture>
    {
        public bool Equals(Texture a, Texture b) => ReferenceEquals(a, b);
        public int GetHashCode(Texture value) => RuntimeHelpers.GetHashCode(value);
    }
    private sealed class Entry
    {
        internal ImGUITexture Image;
        internal ulong Handle;
        internal bool FlipY;
    }
    private readonly object gate = new();
    private readonly Dictionary<Texture, Entry> textures = new(new ReferenceComparer());
    private Entry raster;
    private string error = string.Empty;
    private ManualLogSource log;

    internal string Error { get { lock (gate) return error; } }
    internal void Initialize(ManualLogSource logger) => log = logger;

    internal bool Upload(RenderTexture source)
    {
        Entry entry;
        lock (gate) textures.TryGetValue(source, out entry);
        try
        {
            entry ??= new Entry { Image = new ImGUITexture(source.width, source.height), FlipY = SystemInfo.graphicsUVStartsAtTop };
            lock (gate) entry.Handle = 0;
            // Never hold a Present lock across Graphics.Blit / GetNativeTexturePtr: Unity can wait
            // for the render thread while that thread is inside our ImGui callback.
            entry.Image.UpdateFrom(source);
            lock (gate) { textures[source] = entry; return Publish(entry); }
        }
        catch (Exception failure)
        {
            lock (gate) textures.Remove(source);
            Report(failure);
            Dispose(entry);
            return false;
        }
    }

    internal bool Upload(int width, int height, uint[] argb)
    {
        Entry entry;
        lock (gate) entry = raster;
        try
        {
            if (argb == null || argb.Length != checked(width * height))
                throw new ArgumentException("Raster dimensions do not match its pixel data.", nameof(argb));
            if (entry != null && (entry.Image.Width != width || entry.Image.Height != height))
            {
                lock (gate) raster = null;
                Dispose(entry);
                entry = null;
            }
            entry ??= new Entry { Image = new ImGUITexture(width, height) };
            // Both Raster and ImGUITexture.Pixels use top-to-bottom rows. Avoid a redundant
            // Texture2D, CPU readback, and the Texture2D adapter's vertical flip.
            Color32[] pixels = entry.Image.Pixels;
            for (int i = 0; i < pixels.Length; i++)
            {
                uint c = argb[i];
                pixels[i] = new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24));
            }
            entry.Image.Apply();
            lock (gate) { raster = entry; return Publish(entry); }
        }
        catch (Exception failure)
        {
            lock (gate) raster = null;
            Report(failure);
            Dispose(entry);
            return false;
        }
    }

    private bool Publish(Entry entry)
    {
        entry.Handle = entry.Image.ImGuiHandle;
        error = entry.Handle == 0 ? "RWImGUI texture upload is waiting for its D3D11 device / shader-resource view." : string.Empty;
        return entry.Handle != 0;
    }

    internal bool TryPresent(ImDrawListPtr draw, Texture source, Num.Vector2 min, Num.Vector2 max,
        uint tint = uint.MaxValue, bool? flipY = null)
    {
        lock (gate)
            return textures.TryGetValue(source, out Entry entry) && Present(draw, entry, min, max, tint, flipY);
    }
    internal bool TryPresent(ImDrawListPtr draw, Num.Vector2 min, Num.Vector2 max, uint tint)
    {
        lock (gate) return Present(draw, raster, min, max, tint, null);
    }
    private static bool Present(ImDrawListPtr draw, Entry entry, Num.Vector2 min, Num.Vector2 max, uint tint, bool? flipY)
    {
        if (entry == null || entry.Handle == 0) return false;
        // AddImage only records a command. Keep its COM view alive until the next Present callback,
        // even if the main thread evicts the owner before ImGui submits the draw data.
        WorldMapTextureFrame.Retain(entry.Handle);
        bool flip = flipY ?? entry.FlipY;
        draw.AddImage(entry.Handle, min, max,
            flip ? new Num.Vector2(0, 1) : Num.Vector2.Zero,
            flip ? new Num.Vector2(1, 0) : Num.Vector2.One, tint);
        return true;
    }

    internal void Release(Texture source)
    {
        Entry entry;
        lock (gate)
        {
            if (!textures.TryGetValue(source, out entry)) return;
            textures.Remove(source);
        }
        Dispose(entry);
    }
    internal void Reset()
    {
        List<Entry> release;
        lock (gate)
        {
            release = new List<Entry>(textures.Values);
            textures.Clear();
            if (raster != null) release.Add(raster);
            raster = null;
            error = string.Empty;
        }
        foreach (Entry entry in release) Dispose(entry);
    }
    private void Report(Exception failure)
    {
        lock (gate) error = "RWImGUI texture upload failed: " + failure.Message;
        log?.LogError("Map texture upload failed: " + failure);
    }
    private void Dispose(Entry entry)
    {
        try { entry?.Image.Dispose(); }
        catch (Exception failure) { log?.LogError("Map texture disposal failed: " + failure); }
    }
}

internal static class WorldMapTextureFrame
{
    // Only the single RWImGUI Present thread accesses these leases. One last submitted frame may
    // remain retained while the context is closed; it is released when that context next renders.
    private static readonly HashSet<IntPtr> Handles = new();
    internal static void Begin()
    {
        foreach (IntPtr handle in Handles) Marshal.Release(handle);
        Handles.Clear();
    }
    internal static void Retain(ulong handle)
    {
        IntPtr pointer = new(unchecked((long)handle));
        if (Handles.Add(pointer)) Marshal.AddRef(pointer);
    }
}
