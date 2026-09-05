using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using ImGuiNET;
using UnityEngine;
using UnityEngine.Rendering;
using Num = System.Numerics;
using Object = UnityEngine.Object;

namespace DryCycle.Debugging.AI;

// Minimal Dear ImGui backend for Rain World's Built-in render pipeline. It deliberately
// does not depend on Unity Editor, UImGui, SRP or OS multi-viewports.
internal sealed class AIDebugImGuiBackend : IDisposable
{
    private const long FontTextureIdValue = 1;
    private static readonly IntPtr FontTextureId = new(FontTextureIdValue);

    private readonly Mesh mesh;
    private readonly Material material;
    private readonly MaterialPropertyBlock properties = new();
    private readonly CommandBuffer commands = new() { name = "DryCycle AI Observatory" };
    private readonly List<DrawCommand> drawCommands = new(64);
    private readonly List<int[]> subMeshIndices = new(64);

    private Vector3[] vertices = Array.Empty<Vector3>();
    private Vector2[] uvs = Array.Empty<Vector2>();
    private Color32[] colors = Array.Empty<Color32>();
    private Texture2D fontTexture;
    private IntPtr context;
    private bool frameReady;
    private bool disposed;

    private readonly struct DrawCommand
    {
        internal readonly Rect Clip;
        internal readonly IntPtr TextureId;
        internal readonly int SubMesh;

        internal DrawCommand(Rect clip, IntPtr textureId, int subMesh)
        {
            Clip = clip;
            TextureId = textureId;
            SubMesh = subMesh;
        }
    }

    internal AIDebugImGuiBackend()
    {
        AIDebugNativeBootstrap.Preload();
        context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);

        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.IniFilename = null;
        io.LogFilename = null;
        io.DisplayFramebufferScale = Num.Vector2.One;
        BuildFontAtlas(io);
        ConfigureStyle();

        Shader shader = Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default");
        if (shader == null) throw new InvalidOperationException("DryCycle AI Observatory: no compatible UI shader found.");
        material = new Material(shader)
        {
            name = "DryCycle AI Observatory Material",
            hideFlags = HideFlags.HideAndDontSave
        };
        material.mainTexture = fontTexture;
        if (material.HasProperty("_ZTest")) material.SetInt("_ZTest", (int)CompareFunction.Always);

        mesh = new Mesh
        {
            name = "DryCycle AI Observatory Mesh",
            hideFlags = HideFlags.HideAndDontSave
        };
        mesh.MarkDynamic();
    }

    internal void BeginFrame()
    {
        if (disposed) return;
        ImGui.SetCurrentContext(context);
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new Num.Vector2(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
        io.DisplayFramebufferScale = Num.Vector2.One;
        io.DeltaTime = Mathf.Max(0.001f, Time.unscaledDeltaTime);
        FeedMouse(io);
        FeedKeyboard(io);
        ImGui.NewFrame();
        frameReady = false;
    }

    internal void EndFrame()
    {
        if (disposed) return;
        ImGui.Render();
        frameReady = true;
    }

    internal void Render()
    {
        if (disposed || !frameReady || context == IntPtr.Zero) return;
        ImGui.SetCurrentContext(context);
        ImDrawDataPtr data = ImGui.GetDrawData();
        if (data.NativePtr == null || data.TotalVtxCount <= 0) return;
        BuildMesh(data);
        ExecuteDrawCommands(data);
    }

    internal bool WantsMouse => !disposed && context != IntPtr.Zero && ImGui.GetIO().WantCaptureMouse;
    internal bool WantsKeyboard => !disposed && context != IntPtr.Zero && ImGui.GetIO().WantCaptureKeyboard;

    private static void FeedMouse(ImGuiIOPtr io)
    {
        Vector3 mouse = Input.mousePosition;
        io.AddMousePosEvent(mouse.x, Screen.height - mouse.y);
        io.AddMouseButtonEvent(0, Input.GetMouseButton(0));
        io.AddMouseButtonEvent(1, Input.GetMouseButton(1));
        io.AddMouseButtonEvent(2, Input.GetMouseButton(2));
        Vector2 wheel = Input.mouseScrollDelta;
        if (wheel.sqrMagnitude > 0f) io.AddMouseWheelEvent(wheel.x, wheel.y);
    }

    private static void FeedKeyboard(ImGuiIOPtr io)
    {
        for (KeyCode key = KeyCode.A; key <= KeyCode.Z; key++)
            io.AddKeyEvent(ImGuiKey.A + ((int)key - (int)KeyCode.A), Input.GetKey(key));
        for (KeyCode key = KeyCode.Alpha0; key <= KeyCode.Alpha9; key++)
            io.AddKeyEvent(ImGuiKey._0 + ((int)key - (int)KeyCode.Alpha0), Input.GetKey(key));
        for (KeyCode key = KeyCode.F1; key <= KeyCode.F12; key++)
            io.AddKeyEvent(ImGuiKey.F1 + ((int)key - (int)KeyCode.F1), Input.GetKey(key));

        AddKey(io, ImGuiKey.Tab, KeyCode.Tab);
        AddKey(io, ImGuiKey.LeftArrow, KeyCode.LeftArrow);
        AddKey(io, ImGuiKey.RightArrow, KeyCode.RightArrow);
        AddKey(io, ImGuiKey.UpArrow, KeyCode.UpArrow);
        AddKey(io, ImGuiKey.DownArrow, KeyCode.DownArrow);
        AddKey(io, ImGuiKey.PageUp, KeyCode.PageUp);
        AddKey(io, ImGuiKey.PageDown, KeyCode.PageDown);
        AddKey(io, ImGuiKey.Home, KeyCode.Home);
        AddKey(io, ImGuiKey.End, KeyCode.End);
        AddKey(io, ImGuiKey.Insert, KeyCode.Insert);
        AddKey(io, ImGuiKey.Delete, KeyCode.Delete);
        AddKey(io, ImGuiKey.Backspace, KeyCode.Backspace);
        AddKey(io, ImGuiKey.Space, KeyCode.Space);
        AddKey(io, ImGuiKey.Enter, KeyCode.Return);
        AddKey(io, ImGuiKey.Escape, KeyCode.Escape);
        AddKey(io, ImGuiKey.LeftCtrl, KeyCode.LeftControl);
        AddKey(io, ImGuiKey.RightCtrl, KeyCode.RightControl);
        AddKey(io, ImGuiKey.LeftShift, KeyCode.LeftShift);
        AddKey(io, ImGuiKey.RightShift, KeyCode.RightShift);
        AddKey(io, ImGuiKey.LeftAlt, KeyCode.LeftAlt);
        AddKey(io, ImGuiKey.RightAlt, KeyCode.RightAlt);
        io.AddKeyEvent(ImGuiKey.ModCtrl, Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));
        io.AddKeyEvent(ImGuiKey.ModShift, Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
        io.AddKeyEvent(ImGuiKey.ModAlt, Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));

        string text = Input.inputString;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!char.IsControl(c)) io.AddInputCharacter(c);
        }
    }

    private static void AddKey(ImGuiIOPtr io, ImGuiKey imgui, KeyCode unity) =>
        io.AddKeyEvent(imgui, Input.GetKey(unity));

    private void BuildFontAtlas(ImGuiIOPtr io)
    {
        string font = FindCjkFont();
        if (font != null)
            io.Fonts.AddFontFromFileTTF(font, 17f, default, io.Fonts.GetGlyphRangesChineseFull());
        else
            io.Fonts.AddFontDefault();

        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytesPerPixel);
        int byteCount = checked(width * height * bytesPerPixel);
        byte[] managed = new byte[byteCount];
        Marshal.Copy(pixels, managed, 0, byteCount);
        fontTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
        {
            name = "DryCycle AI Observatory Font",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        fontTexture.LoadRawTextureData(managed);
        fontTexture.Apply(false, true);
        io.Fonts.TexID = FontTextureId;
        io.Fonts.ClearTexData();
    }

    private static string FindCjkFont()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates =
        {
            Path.Combine(windows, "msyh.ttc"),
            Path.Combine(windows, "msyhbd.ttc"),
            Path.Combine(windows, "simhei.ttf"),
            Path.Combine(windows, "simsun.ttc"),
            "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc"
        };
        for (int i = 0; i < candidates.Length; i++)
            if (!string.IsNullOrEmpty(candidates[i]) && File.Exists(candidates[i])) return candidates[i];
        return null;
    }

    private static void ConfigureStyle()
    {
        ImGui.StyleColorsDark();
        ImGuiStylePtr style = ImGui.GetStyle();
        style.WindowRounding = 5f;
        style.ChildRounding = 4f;
        style.FrameRounding = 3f;
        style.PopupRounding = 4f;
        style.ScrollbarRounding = 4f;
        style.WindowPadding = new Num.Vector2(10f, 9f);
        style.FramePadding = new Num.Vector2(7f, 4f);
        style.ItemSpacing = new Num.Vector2(7f, 5f);
    }

    private void EnsureBuffers(int count)
    {
        if (vertices.Length >= count) return;
        int capacity = Mathf.NextPowerOfTwo(Mathf.Max(256, count));
        vertices = new Vector3[capacity];
        uvs = new Vector2[capacity];
        colors = new Color32[capacity];
    }

    private void BuildMesh(ImDrawDataPtr data)
    {
        EnsureBuffers(data.TotalVtxCount);
        drawCommands.Clear();
        subMeshIndices.Clear();
        int vertexBase = 0;

        for (int listIndex = 0; listIndex < data.CmdListsCount; listIndex++)
        {
            ImDrawListPtr list = data.CmdLists[listIndex];
            for (int i = 0; i < list.VtxBuffer.Size; i++)
            {
                ImDrawVertPtr vertex = list.VtxBuffer[i];
                Num.Vector2 p = vertex.pos;
                Num.Vector2 uv = vertex.uv;
                uint packed = vertex.col;
                int dst = vertexBase + i;
                vertices[dst] = new Vector3(p.X, p.Y, 0f);
                uvs[dst] = new Vector2(uv.X, uv.Y);
                colors[dst] = new Color32((byte)(packed & 0xff), (byte)((packed >> 8) & 0xff),
                    (byte)((packed >> 16) & 0xff), (byte)((packed >> 24) & 0xff));
            }

            for (int cmdIndex = 0; cmdIndex < list.CmdBuffer.Size; cmdIndex++)
            {
                ImDrawCmdPtr cmd = list.CmdBuffer[cmdIndex];
                if (cmd.UserCallback != IntPtr.Zero || cmd.ElemCount == 0) continue;
                int elemCount = checked((int)cmd.ElemCount);
                int[] indices = new int[elemCount];
                int firstIndex = checked((int)cmd.IdxOffset);
                int vtxOffset = checked((int)cmd.VtxOffset);
                for (int i = 0; i < elemCount; i++)
                    indices[i] = vertexBase + vtxOffset + list.IdxBuffer[firstIndex + i];

                Num.Vector4 clip = cmd.ClipRect;
                Rect rect = Rect.MinMaxRect(clip.X, clip.Y, clip.Z, clip.W);
                int subMesh = subMeshIndices.Count;
                subMeshIndices.Add(indices);
                drawCommands.Add(new DrawCommand(rect, cmd.TextureId, subMesh));
            }
            vertexBase += list.VtxBuffer.Size;
        }

        mesh.Clear(false);
        if (data.TotalVtxCount == 0 || subMeshIndices.Count == 0) return;
        var usedVertices = new Vector3[data.TotalVtxCount];
        var usedUvs = new Vector2[data.TotalVtxCount];
        var usedColors = new Color32[data.TotalVtxCount];
        Array.Copy(vertices, usedVertices, data.TotalVtxCount);
        Array.Copy(uvs, usedUvs, data.TotalVtxCount);
        Array.Copy(colors, usedColors, data.TotalVtxCount);
        mesh.vertices = usedVertices;
        mesh.uv = usedUvs;
        mesh.colors32 = usedColors;
        mesh.subMeshCount = subMeshIndices.Count;
        for (int i = 0; i < subMeshIndices.Count; i++) mesh.SetTriangles(subMeshIndices[i], i, false);
        mesh.RecalculateBounds();
    }

    private void ExecuteDrawCommands(ImDrawDataPtr data)
    {
        if (drawCommands.Count == 0) return;
        float width = Mathf.Max(1f, data.DisplaySize.X * data.FramebufferScale.X);
        float height = Mathf.Max(1f, data.DisplaySize.Y * data.FramebufferScale.Y);
        commands.Clear();
        commands.SetViewport(new Rect(0f, 0f, width, height));
        commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, width, height, 0f, -1f, 1f));
        properties.SetTexture("_MainTex", fontTexture);

        for (int i = 0; i < drawCommands.Count; i++)
        {
            DrawCommand draw = drawCommands[i];
            float x1 = Mathf.Clamp(draw.Clip.xMin, 0f, width);
            float y1 = Mathf.Clamp(draw.Clip.yMin, 0f, height);
            float x2 = Mathf.Clamp(draw.Clip.xMax, 0f, width);
            float y2 = Mathf.Clamp(draw.Clip.yMax, 0f, height);
            if (x2 <= x1 || y2 <= y1) continue;
            commands.EnableScissorRect(new Rect(x1, height - y2, x2 - x1, y2 - y1));
            commands.DrawMesh(mesh, Matrix4x4.identity, material, draw.SubMesh, -1, properties);
        }
        commands.DisableScissorRect();
        Graphics.ExecuteCommandBuffer(commands);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        frameReady = false;
        commands.Release();
        if (mesh != null) Object.Destroy(mesh);
        if (material != null) Object.Destroy(material);
        if (fontTexture != null) Object.Destroy(fontTexture);
        if (context != IntPtr.Zero)
        {
            ImGui.DestroyContext(context);
            context = IntPtr.Zero;
        }
    }
}

internal static class AIDebugNativeBootstrap
{
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);
#endif
    private static bool attempted;

    internal static void Preload()
    {
        if (attempted) return;
        attempted = true;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string path = Path.Combine(folder ?? string.Empty, "cimgui.dll");
        if (File.Exists(path)) LoadLibrary(path);
#endif
    }
}
