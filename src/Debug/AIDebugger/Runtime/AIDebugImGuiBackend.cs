using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
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
    private static readonly IntPtr FontTextureId = new(1);

    private readonly ManualLogSource logger;
    private readonly Mesh mesh;
    private readonly Material material;
    private readonly MaterialPropertyBlock properties = new();
    private readonly CommandBuffer commands = new() { name = "DryCycle AI Observatory" };
    private readonly List<DrawCommand> drawCommands = new(64);
    private readonly List<Vector3> vertices = new(4096);
    private readonly List<Vector2> uvs = new(4096);
    private readonly List<Color32> colors = new(4096);
    private readonly List<List<int>> indexBuffers = new(64);

    private Texture2D fontTexture;
    private IntPtr context;
    private bool frameReady;
    private bool disposed;
    private bool beginFrameLogged;
    private bool drawDataLogged;
    private bool emptyDrawDataLogged;
    private bool meshLogged;
    private bool renderLogged;
    private int previousSubMeshCount = -1;

    private readonly struct DrawCommand
    {
        internal readonly Rect Clip;
        internal readonly int SubMesh;

        internal DrawCommand(Rect clip, int subMesh)
        {
            Clip = clip;
            SubMesh = subMesh;
        }
    }

    internal AIDebugImGuiBackend(ManualLogSource log)
    {
        logger = log;
        AIDebugNativeBootstrap.Preload(logger);

        context = ImGui.CreateContext();
        if (context == IntPtr.Zero)
            throw new InvalidOperationException("DryCycle AI Observatory: ImGui.CreateContext returned null.");
        ImGui.SetCurrentContext(context);
        logger?.LogInfo("DryCycle AI Observatory ImGui context ready.");

        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.DisplayFramebufferScale = Num.Vector2.One;
        BuildFontAtlas(io);
        ConfigureStyle();

        // Keep the established shader order until live diagnostics prove a shader-specific
        // failure. The runtime task explicitly requires DrawData/Mesh/Render evidence first.
        Shader shader = Shader.Find("Futile/Basic") ?? Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
        if (shader == null) throw new InvalidOperationException("DryCycle AI Observatory: no compatible UI shader found.");
        material = new Material(shader)
        {
            name = "DryCycle AI Observatory Material",
            hideFlags = HideFlags.HideAndDontSave
        };
        material.mainTexture = fontTexture;
        if (material.HasProperty("_ZTest")) material.SetInt("_ZTest", (int)CompareFunction.Always);
        if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
        if (material.HasProperty("_Cull")) material.SetInt("_Cull", (int)CullMode.Off);

        mesh = new Mesh
        {
            name = "DryCycle AI Observatory Mesh",
            hideFlags = HideFlags.HideAndDontSave,
            indexFormat = IndexFormat.UInt32
        };
        mesh.MarkDynamic();
        logger?.LogInfo($"DryCycle AI Observatory renderer ready: shader={shader.name}, meshIndexFormat={mesh.indexFormat}.");
    }

    internal void MakeCurrent()
    {
        if (!disposed && context != IntPtr.Zero) ImGui.SetCurrentContext(context);
    }

    internal void BeginFrame()
    {
        if (disposed) return;
        MakeCurrent();
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new Num.Vector2(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
        io.DisplayFramebufferScale = Num.Vector2.One;
        io.DeltaTime = Mathf.Max(0.001f, Time.unscaledDeltaTime);
        FeedMouse(io);
        FeedKeyboard(io);
        ImGui.NewFrame();
        frameReady = false;

        if (!beginFrameLogged)
        {
            beginFrameLogged = true;
            logger?.LogInfo($"DryCycle AI Observatory first BeginFrame: display={io.DisplaySize.X:0}x{io.DisplaySize.Y:0}, framebufferScale={io.DisplayFramebufferScale.X:0.###},{io.DisplayFramebufferScale.Y:0.###}, delta={io.DeltaTime:0.0000}.");
        }
    }

    internal void EndFrame()
    {
        if (disposed) return;
        MakeCurrent();
        ImGui.Render();
        frameReady = true;

        ImDrawDataPtr data = ImGui.GetDrawData();
        if (!drawDataLogged && data.CmdListsCount > 0 && data.TotalVtxCount > 0 && data.TotalIdxCount > 0)
        {
            drawDataLogged = true;
            logger?.LogInfo($"DryCycle AI Observatory first DrawData: cmdLists={data.CmdListsCount}, vertices={data.TotalVtxCount}, indices={data.TotalIdxCount}, displayPos={data.DisplayPos.X:0.###},{data.DisplayPos.Y:0.###}, displaySize={data.DisplaySize.X:0.###}x{data.DisplaySize.Y:0.###}, framebufferScale={data.FramebufferScale.X:0.###},{data.FramebufferScale.Y:0.###}.");
        }
    }

    internal void Render()
    {
        if (disposed || !frameReady || context == IntPtr.Zero) return;
        MakeCurrent();
        ImDrawDataPtr data = ImGui.GetDrawData();
        if (data.TotalVtxCount <= 0 || data.TotalIdxCount <= 0 || data.CmdListsCount <= 0)
        {
            if (!emptyDrawDataLogged)
            {
                emptyDrawDataLogged = true;
                logger?.LogWarning($"DryCycle AI Observatory render reached with empty DrawData: cmdLists={data.CmdListsCount}, vertices={data.TotalVtxCount}, indices={data.TotalIdxCount}.");
            }
            frameReady = false;
            return;
        }

        BuildMesh(data);
        ExecuteDrawCommands(data);
        frameReady = false;
    }

    internal bool WantsMouse
    {
        get
        {
            if (disposed || context == IntPtr.Zero) return false;
            MakeCurrent();
            return ImGui.GetIO().WantCaptureMouse;
        }
    }

    internal bool WantsKeyboard
    {
        get
        {
            if (disposed || context == IntPtr.Zero) return false;
            MakeCurrent();
            return ImGui.GetIO().WantCaptureKeyboard;
        }
    }

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
        for (int i = (int)KeyCode.A; i <= (int)KeyCode.Z; i++)
            io.AddKeyEvent((ImGuiKey)((int)ImGuiKey.A + i - (int)KeyCode.A), Input.GetKey((KeyCode)i));
        for (int i = (int)KeyCode.Alpha0; i <= (int)KeyCode.Alpha9; i++)
            io.AddKeyEvent((ImGuiKey)((int)ImGuiKey._0 + i - (int)KeyCode.Alpha0), Input.GetKey((KeyCode)i));
        for (int i = (int)KeyCode.F1; i <= (int)KeyCode.F12; i++)
            io.AddKeyEvent((ImGuiKey)((int)ImGuiKey.F1 + i - (int)KeyCode.F1), Input.GetKey((KeyCode)i));

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
            if (char.IsControl(c)) continue;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                io.AddInputCharacter((uint)char.ConvertToUtf32(c, text[++i]));
                continue;
            }
            if (!char.IsSurrogate(c)) io.AddInputCharacter(c);
        }
    }

    private static void AddKey(ImGuiIOPtr io, ImGuiKey imgui, KeyCode unity) =>
        io.AddKeyEvent(imgui, Input.GetKey(unity));

    private void BuildFontAtlas(ImGuiIOPtr io)
    {
        string font = FindCjkFont();
        if (font != null)
            io.Fonts.AddFontFromFileTTF(font, 17f, default(ImFontConfigPtr), io.Fonts.GetGlyphRangesChineseFull());
        else
        {
            AIDebugLocalization.Language = AIDebugLanguage.English;
            io.Fonts.AddFontDefault();
        }

        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytesPerPixel);
        if (pixels == IntPtr.Zero || width <= 0 || height <= 0 || bytesPerPixel <= 0)
            throw new InvalidOperationException("DryCycle AI Observatory: ImGui font atlas returned invalid pixel data.");
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
        logger?.LogInfo($"DryCycle AI Observatory font atlas built: {width}x{height}, source={(font ?? "ImGui default")}, language={AIDebugLocalization.Language}.");
    }

    private static string FindCjkFont()
    {
        string systemFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates =
        {
            Path.Combine(systemFonts ?? string.Empty, "msyh.ttc"),
            Path.Combine(systemFonts ?? string.Empty, "msyhbd.ttc"),
            Path.Combine(systemFonts ?? string.Empty, "simhei.ttf"),
            Path.Combine(systemFonts ?? string.Empty, "simsun.ttc"),
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

    private List<int> IndexBuffer(int index)
    {
        while (indexBuffers.Count <= index) indexBuffers.Add(new List<int>(256));
        List<int> buffer = indexBuffers[index];
        buffer.Clear();
        return buffer;
    }

    private void BuildMesh(ImDrawDataPtr data)
    {
        vertices.Clear();
        uvs.Clear();
        colors.Clear();
        drawCommands.Clear();
        int vertexBase = 0;
        int subMesh = 0;
        Num.Vector2 displayPos = data.DisplayPos;
        Num.Vector2 framebufferScale = data.FramebufferScale;
        float scaleX = framebufferScale.X > 0f ? framebufferScale.X : 1f;
        float scaleY = framebufferScale.Y > 0f ? framebufferScale.Y : 1f;
        Rect firstClip = default;
        Vector3 firstVertex = default;
        bool haveFirstClip = false;
        bool haveFirstVertex = false;

        for (int listIndex = 0; listIndex < data.CmdListsCount; listIndex++)
        {
            ImDrawListPtr list = data.CmdLists[listIndex];
            for (int i = 0; i < list.VtxBuffer.Size; i++)
            {
                ImDrawVertPtr vertex = list.VtxBuffer[i];
                Num.Vector2 p = vertex.pos;
                Num.Vector2 uv = vertex.uv;
                uint packed = vertex.col;
                Vector3 transformed = new((p.X - displayPos.X) * scaleX, (p.Y - displayPos.Y) * scaleY, 0f);
                vertices.Add(transformed);
                uvs.Add(new Vector2(uv.X, uv.Y));
                colors.Add(new Color32((byte)(packed & 0xff), (byte)((packed >> 8) & 0xff),
                    (byte)((packed >> 16) & 0xff), (byte)((packed >> 24) & 0xff)));
                if (!haveFirstVertex)
                {
                    firstVertex = transformed;
                    haveFirstVertex = true;
                }
            }

            for (int cmdIndex = 0; cmdIndex < list.CmdBuffer.Size; cmdIndex++)
            {
                ImDrawCmdPtr cmd = list.CmdBuffer[cmdIndex];
                if (cmd.UserCallback != IntPtr.Zero || cmd.ElemCount == 0) continue;
                int firstIndex = checked((int)cmd.IdxOffset);
                int vtxOffset = checked((int)cmd.VtxOffset);
                int elemCount = checked((int)cmd.ElemCount);
                if (firstIndex < 0 || elemCount < 0 || firstIndex + elemCount > list.IdxBuffer.Size)
                    throw new InvalidOperationException($"DryCycle AI Observatory: ImGui index range invalid (first={firstIndex}, count={elemCount}, buffer={list.IdxBuffer.Size}).");

                List<int> indices = IndexBuffer(subMesh);
                for (int i = 0; i < elemCount; i++)
                {
                    int localIndex = vtxOffset + list.IdxBuffer[firstIndex + i];
                    if (localIndex < 0 || localIndex >= list.VtxBuffer.Size)
                        throw new InvalidOperationException($"DryCycle AI Observatory: ImGui vertex index {localIndex} outside list vertex range {list.VtxBuffer.Size}.");
                    indices.Add(vertexBase + localIndex);
                }

                Num.Vector4 clip = cmd.ClipRect;
                Rect transformedClip = Rect.MinMaxRect(
                    (clip.X - displayPos.X) * scaleX,
                    (clip.Y - displayPos.Y) * scaleY,
                    (clip.Z - displayPos.X) * scaleX,
                    (clip.W - displayPos.Y) * scaleY);
                drawCommands.Add(new DrawCommand(transformedClip, subMesh));
                if (!haveFirstClip && transformedClip.width > 0f && transformedClip.height > 0f)
                {
                    firstClip = transformedClip;
                    haveFirstClip = true;
                }
                subMesh++;
            }
            vertexBase += list.VtxBuffer.Size;
        }

        if (vertices.Count == 0 || drawCommands.Count == 0)
        {
            mesh.Clear(false);
            previousSubMeshCount = 0;
            return;
        }

        bool layoutChanged = previousSubMeshCount != drawCommands.Count;
        mesh.Clear(layoutChanged);
        previousSubMeshCount = drawCommands.Count;
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(colors);
        mesh.subMeshCount = drawCommands.Count;
        for (int i = 0; i < drawCommands.Count; i++) mesh.SetTriangles(indexBuffers[i], i, false);
        float width = Mathf.Max(1f, data.DisplaySize.X * scaleX);
        float height = Mathf.Max(1f, data.DisplaySize.Y * scaleY);
        mesh.bounds = new Bounds(new Vector3(width * 0.5f, height * 0.5f, 0f), new Vector3(width, height, 1f));

        if (!meshLogged)
        {
            meshLogged = true;
            string clipText = haveFirstClip ? $"{firstClip.xMin:0.###},{firstClip.yMin:0.###},{firstClip.xMax:0.###},{firstClip.yMax:0.###}" : "none";
            string vertexText = haveFirstVertex ? $"{firstVertex.x:0.###},{firstVertex.y:0.###}" : "none";
            logger?.LogInfo($"DryCycle AI Observatory first mesh: vertices={vertices.Count}, subMeshes={drawCommands.Count}, firstClip={clipText}, firstVertex={vertexText}.");
        }
    }

    private void ExecuteDrawCommands(ImDrawDataPtr data)
    {
        if (drawCommands.Count == 0) return;
        float scaleX = data.FramebufferScale.X > 0f ? data.FramebufferScale.X : 1f;
        float scaleY = data.FramebufferScale.Y > 0f ? data.FramebufferScale.Y : 1f;
        float width = Mathf.Max(1f, data.DisplaySize.X * scaleX);
        float height = Mathf.Max(1f, data.DisplaySize.Y * scaleY);
        commands.Clear();
        commands.SetViewport(new Rect(0f, 0f, width, height));
        commands.SetViewProjectionMatrices(
            Matrix4x4.identity,
            Matrix4x4.Ortho(0f, width, height, 0f, -1f, 1f));
        properties.SetTexture("_MainTex", fontTexture);

        int executed = 0;
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
            executed++;
        }
        commands.DisableScissorRect();
        Graphics.ExecuteCommandBuffer(commands);

        if (!renderLogged)
        {
            renderLogged = true;
            logger?.LogInfo($"DryCycle AI Observatory first render: drawCommands={executed}/{drawCommands.Count}, viewport={width:0}x{height:0}, ExecuteCommandBuffer completed.");
        }
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
            ImGui.SetCurrentContext(context);
            ImGui.DestroyContext(context);
            context = IntPtr.Zero;
        }
    }
}

internal static class AIDebugNativeBootstrap
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    private static bool attempted;
    private static IntPtr handle;

    internal static void Preload(ManualLogSource logger)
    {
        if (attempted) return;
        attempted = true;
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            logger?.LogInfo("DryCycle AI Observatory cimgui native preload skipped on non-Windows platform.");
            return;
        }

        string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string path = Path.Combine(folder ?? string.Empty, "cimgui.dll");
        if (!File.Exists(path))
        {
            logger?.LogWarning("DryCycle AI Observatory cimgui native preload file not found beside DryCycle.dll: " + path);
            return;
        }

        handle = LoadLibrary(path);
        if (handle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            logger?.LogWarning($"DryCycle AI Observatory cimgui native preload failed: Win32Error={error}, path={path}. Normal DllImport probing will still be attempted.");
            return;
        }
        logger?.LogInfo("DryCycle AI Observatory cimgui native preload success: " + path);
    }
}
