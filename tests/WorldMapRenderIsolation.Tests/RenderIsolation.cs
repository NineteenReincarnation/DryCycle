using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Num = System.Numerics;

// Executed by a real Unity Editor with graphics enabled. The test loads the deployed frontend;
// no camera, mesh, material, transform, texture or render call is mocked.
public static partial class MapRenderIsolationTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static string game, plugins, output;
    private static Assembly frontend;
    private static int checks;
    private static readonly System.Collections.Generic.List<string> results = new();

    public static void Run()
    {
        game = Argument("-rainWorldDir") ?? "D:/Steam/steamapps/common/Rain World";
        output = Argument("-isolationOutput") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
        plugins = Path.Combine(game, "RainWorld_Data/StreamingAssets/mods/Ancient Site/newest/plugins");
        Directory.CreateDirectory(output);
        // This is an isolated generated project. Play mode gives the actual renderers their normal
        // deferred Object.Destroy semantics; disabling reload preserves only this test's arguments.
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
        EditorApplication.playModeStateChanged += EnteredPlayMode;
        EditorApplication.isPlaying = true;
    }

    private static void EnteredPlayMode(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode) return;
        EditorApplication.playModeStateChanged -= EnteredPlayMode;
        int exit = 0;
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            frontend = Assembly.LoadFrom(Path.Combine(plugins, "DryCycle.DevTool.RWImGui.dll"));
            results.Add("GPU: " + SystemInfo.graphicsDeviceType + " / " + SystemInfo.graphicsDeviceName);
            Check(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null, "A real graphics device is active.");
            InitializeTextureDevice();
            ExerciseRenderer();
            ExerciseTexturePresentation();
            results.Add("PASS: " + checks + " real Unity/GPU assertions.");
        }
        catch (Exception error) { exit = 1; results.Add("FAIL: " + error); }
        File.WriteAllLines(Path.Combine(output, "gpu-results.txt"), results);
        Debug.Log(string.Join("\n", results));
        EditorApplication.Exit(exit);
    }

    private static void ExerciseRenderer()
    {
        object surface = New("WorldMapRenderTextureSurface");
        // The isolated editor uses the same real D3D11 device for RWImGUI's public texture API.
        object scene = New("WorldMapScene"), resources = New("WorldMapRoomResourceStore");
        Invoke(scene, "GetOrCreateRoom", 0);
        object routes = New("WorldMapConnectionResourceStore"), route = New("ConnectionRouteResource");
        Set(route, "ConnectionId", "test-route"); Set(route, "Revision", 1L);
        Set(route, "Points", new[] { new Num.Vector2(0, 20), new Num.Vector2(40, 20) });
        ((IDictionary)Get(routes, "routes"))["test-route"] = route;
        object roomsRenderer = New("WorldMapRetainedRoomRenderer"), routesRenderer = New("WorldMapRetainedConnectionRenderer");
        object view = View(128, 1f);
        Action<Transform> prepare = parent =>
        {
            Check(!parent.gameObject.activeInHierarchy, "Room/route uploads happen under an inactive parent.");
            Check((bool)Invoke(roomsRenderer, "SynchronizeVisible", scene, resources, new[] { 0 }, parent), "Actual room renderer prepares its mesh.");
            Check((bool)Invoke(routesRenderer, "SynchronizeVisible", routes, new[] { "test-route" }, true, parent), "Actual connection renderer prepares its mesh.");
        };
        Check((bool)Invoke(surface, "Render", view, prepare), "The retained surface renders successfully.");
        GameObject root = (GameObject)Get(surface, "sceneObject");
        RenderTexture map = (RenderTexture)Get(surface, "presented");
        Check(!root.activeSelf, "Map scene is inactive immediately after Camera.Render.");
        Check(OpaquePixels(map, "map-ui-texture.png") > 0, "The UI map texture still contains rendered geometry.");
        Check(root.GetComponentsInChildren<MeshRenderer>(true).Length >= 3, "Rooms, terrain overlay and connection meshes share the isolated hierarchy.");

        var gameplayObject = new GameObject("Isolation test gameplay camera");
        Camera gameplay = gameplayObject.AddComponent<Camera>(); gameplay.enabled = false;
        gameplay.orthographic = true; gameplay.orthographicSize = 32; gameplay.aspect = 1;
        gameplay.transform.position = new Vector3(16, -16, -100);
        gameplay.cullingMask = -1; gameplay.clearFlags = CameraClearFlags.SolidColor;
        gameplay.backgroundColor = Color.clear; gameplay.nearClipPlane = 0.1f; gameplay.farClipPlane = 500;
        var gameTarget = new RenderTexture(128, 128, 0, RenderTextureFormat.ARGB32); gameTarget.Create();
        gameplay.targetTexture = gameTarget;
        int GamePixels(string name = null) { gameplay.Render(); return OpaquePixels(gameTarget, name); }
        Check(GamePixels("gameplay-after-fix.png") == 0, "An all-layer gameplay camera sees zero map pixels after the UI render.");
        // Positive control reproduces the old leak and proves that the dedicated layer alone
        // cannot protect the game's cameras. Immediately hide the fixture again afterward.
        root.SetActive(true);
        try { Check(GamePixels("gameplay-leak-control.png") > 0, "Leaving the retained hierarchy active reproduces visible gameplay leakage."); }
        finally { root.SetActive(false); }
        Check(GamePixels() == 0, "Cached/stable frames leave no map meshes visible to gameplay.");

        Check((bool)Invoke(surface, "Render", View(192, 1.5f), prepare), "A resized/zoomed map renders successfully.");
        Check(GamePixels() == 0 && !root.activeSelf, "Resize and zoom cannot reopen the gameplay leak.");
        Action<Transform> failPreparation = parent => { prepare(parent); throw new InvalidOperationException("Expected preparation failure"); };
        Check(!(bool)Invoke(surface, "Render", View(192, 1.5f), failPreparation), "Scene preparation failure is reported as a failed render.");
        Check(GamePixels() == 0 && !root.activeSelf, "A failed render leaves every retained mesh hidden.");
        Check(((string)Get(surface, "error")).Contains("Expected preparation failure"), "The original render failure remains observable.");

        Invoke(roomsRenderer, "Reset"); Invoke(routesRenderer, "Reset"); Invoke(surface, "Reset");
        Check(GamePixels() == 0, "Closing the surface cannot leak meshes during deferred Unity destruction.");
        Check(gameplay.cullingMask == -1, "Gameplay camera settings are unchanged.");
        gameplay.targetTexture = null; gameTarget.Release();
        UnityEngine.Object.Destroy(gameTarget); UnityEngine.Object.Destroy(gameplayObject);
    }

    private static int OpaquePixels(RenderTexture target, string filename = null)
    {
        RenderTexture previous = RenderTexture.active;
        var pixels = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
        try
        {
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
            if (filename != null) File.WriteAllBytes(Path.Combine(output, filename), pixels.EncodeToPNG());
            int count = 0; foreach (Color32 pixel in pixels.GetPixels32()) if (pixel.a > 8) count++;
            if (filename != null) results.Add(filename + ": " + count + " opaque pixels");
            return count;
        }
        finally { RenderTexture.active = previous; UnityEngine.Object.Destroy(pixels); }
    }

    private static object View(int size, float zoom) => Activator.CreateInstance(Front("WorldMapViewTransform"), Flags, null,
        new object[] { Num.Vector2.Zero, new Num.Vector2(size, size), new Num.Vector2(32, 32), zoom }, null);
    private static Type Front(string name) => frontend.GetType("DryCycle.DevUI.DevTool.RWImGui." + name, true);
    private static object New(string type) => Activator.CreateInstance(Front(type), true);
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); results.Add("OK: " + message); }
    private static string Argument(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
        return null;
    }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        if (name == "Assembly-CSharp") return Assembly.LoadFrom(Path.Combine(game, "BepInEx/utils/PUBLIC-Assembly-CSharp.dll"));
        foreach (string directory in new[] { plugins, Path.Combine(game, "BepInEx/core"), Path.Combine(game, "BepInEx/plugins"),
            Path.Combine(game, "RainWorld_Data/Managed"), Path.GetFullPath(Path.Combine(game, "../../workshop/content/312520/3417372413/plugins")) })
        {
            string path = Path.Combine(directory, name + ".dll"); if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
}
