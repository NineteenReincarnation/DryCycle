using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Presents room exits and creature holes as two independent World Map layers.
/// Room pipes keep the gold interactive shortcut language; creature pipes use the green
/// non-linkable language and expose their real AbstractRoom node index.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapPipeLayerPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapPipeLayers";
    public const string PluginName = "DryCycle DevTool World Map Pipe Layers";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapPipeLayers.Enable(Logger);

    private void OnDisable() => WorldMapPipeLayers.Disable();
}

internal static class WorldMapPipeLayers
{
    private delegate void OrigDrawCompactCheckbox(string label, string id, ref bool value);
    private delegate void HookDrawCompactCheckbox(
        OrigDrawCompactCheckbox orig,
        string label,
        string id,
        ref bool value);

    private delegate void OrigDrawShortcutSocket(
        ImDrawListPtr draw,
        Num.Vector2 point,
        uint shadow,
        uint color,
        bool connected,
        bool emphasized);
    private delegate void HookDrawShortcutSocket(
        OrigDrawShortcutSocket orig,
        ImDrawListPtr draw,
        Num.Vector2 point,
        uint shadow,
        uint color,
        bool connected,
        bool emphasized);

    private delegate void OrigDrawCreatureShortcuts(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize);
    private delegate void HookDrawCreatureShortcuts(
        OrigDrawCreatureShortcuts orig,
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize);

    private delegate void GetRoomRectDelegate(
        EditorMapRoomSnapshot room,
        EditorMapRoomVisualSnapshot visual,
        Num.Vector2 canvasMin,
        out Num.Vector2 min,
        out Num.Vector2 max);
    private delegate Num.Vector2 LocalToScreenDelegate(
        Num.Vector2 roomMin,
        EditorMapRoomVisualSnapshot visual,
        float tileX,
        float tileY);
    private delegate bool IsLayerVisibleDelegate(int layer);
    private delegate void DrawCreatureShortcutSocketDelegate(
        ImDrawListPtr draw,
        Num.Vector2 point,
        uint shadow);

    private static readonly HookDrawCompactCheckbox DrawCompactCheckboxHookDelegate = DrawCompactCheckboxHook;
    private static readonly HookDrawShortcutSocket DrawShortcutSocketHookDelegate = DrawShortcutSocketHook;
    private static readonly HookDrawCreatureShortcuts DrawCreatureShortcutsHookDelegate = DrawCreatureShortcutsHook;

    private static ManualLogSource log;
    private static IDisposable checkboxHook;
    private static IDisposable roomPipeSocketHook;
    private static IDisposable creaturePipeHook;
    private static GetRoomRectDelegate getRoomRect;
    private static LocalToScreenDelegate localToScreen;
    private static IsLayerVisibleDelegate isLayerVisible;
    private static DrawCreatureShortcutSocketDelegate drawCreatureShortcutSocket;
    private static FieldInfo zoomField;
    private static FieldInfo linkingRoomField;
    private static FieldInfo linkingNodeField;
    private static bool enabled;
    private static bool roomPipesVisible = true;
    private static bool creaturePipesVisible = true;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type mapType = typeof(WorldMapView);

            MethodInfo drawCompactCheckbox = mapType.GetMethod(
                "DrawCompactCheckbox",
                flags,
                null,
                new[] { typeof(string), typeof(string), typeof(bool).MakeByRefType() },
                null);
            MethodInfo drawShortcutSocket = mapType.GetMethod(
                "DrawShortcutSocket",
                flags,
                null,
                new[]
                {
                    typeof(ImDrawListPtr), typeof(Num.Vector2), typeof(uint), typeof(uint),
                    typeof(bool), typeof(bool)
                },
                null);
            MethodInfo drawCreatureShortcuts = mapType.GetMethod(
                "DrawCreatureShortcuts",
                flags,
                null,
                new[]
                {
                    typeof(ImDrawListPtr), typeof(EditorMapPresentationSnapshot),
                    typeof(Num.Vector2), typeof(Num.Vector2)
                },
                null);
            MethodInfo getRoomRectMethod = mapType.GetMethod("GetRoomRect", flags);
            MethodInfo localToScreenMethod = mapType.GetMethod("LocalToScreen", flags);
            MethodInfo isLayerVisibleMethod = mapType.GetMethod("IsLayerVisible", flags);
            MethodInfo drawCreatureShortcutSocketMethod = mapType.GetMethod("DrawCreatureShortcutSocket", flags);

            if (drawCompactCheckbox == null || drawShortcutSocket == null || drawCreatureShortcuts == null ||
                getRoomRectMethod == null || localToScreenMethod == null || isLayerVisibleMethod == null ||
                drawCreatureShortcutSocketMethod == null)
                throw new MissingMethodException("WorldMapView pipe presentation methods were not found.");

            getRoomRect = (GetRoomRectDelegate)Delegate.CreateDelegate(typeof(GetRoomRectDelegate), getRoomRectMethod);
            localToScreen = (LocalToScreenDelegate)Delegate.CreateDelegate(typeof(LocalToScreenDelegate), localToScreenMethod);
            isLayerVisible = (IsLayerVisibleDelegate)Delegate.CreateDelegate(typeof(IsLayerVisibleDelegate), isLayerVisibleMethod);
            drawCreatureShortcutSocket = (DrawCreatureShortcutSocketDelegate)Delegate.CreateDelegate(
                typeof(DrawCreatureShortcutSocketDelegate),
                drawCreatureShortcutSocketMethod);

            zoomField = mapType.GetField("zoom", flags);
            linkingRoomField = mapType.GetField("linkingRoom", flags);
            linkingNodeField = mapType.GetField("linkingNode", flags);
            if (zoomField == null || linkingRoomField == null || linkingNodeField == null)
                throw new MissingFieldException("WorldMapView pipe state fields were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            checkboxHook = constructor.Invoke(new object[] { drawCompactCheckbox, DrawCompactCheckboxHookDelegate }) as IDisposable;
            roomPipeSocketHook = constructor.Invoke(new object[] { drawShortcutSocket, DrawShortcutSocketHookDelegate }) as IDisposable;
            creaturePipeHook = constructor.Invoke(new object[] { drawCreatureShortcuts, DrawCreatureShortcutsHookDelegate }) as IDisposable;

            enabled = true;
            log?.LogInfo("World Map room/creature pipe layers enabled.");
        }
        catch (Exception error)
        {
            Disable();
            log?.LogWarning("World Map pipe layers could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref creaturePipeHook);
        DisposeHook(ref roomPipeSocketHook);
        DisposeHook(ref checkboxHook);
        getRoomRect = null;
        localToScreen = null;
        isLayerVisible = null;
        drawCreatureShortcutSocket = null;
        zoomField = null;
        linkingRoomField = null;
        linkingNodeField = null;
        roomPipesVisible = true;
        creaturePipesVisible = true;
        enabled = false;
        log = null;
    }

    private static void DrawCompactCheckboxHook(
        OrigDrawCompactCheckbox orig,
        string label,
        string id,
        ref bool value)
    {
        if (!string.Equals(id, "WorldMapPortLabels", StringComparison.Ordinal))
        {
            orig(label, id, ref value);
            return;
        }

        value = roomPipesVisible;
        orig(DevToolUiSettings.T("房间管道", "Room pipes"), "WorldMapRoomPipes", ref value);
        bool wasVisible = roomPipesVisible;
        roomPipesVisible = value;

        if (wasVisible && !roomPipesVisible)
        {
            linkingRoomField?.SetValue(null, -1);
            linkingNodeField?.SetValue(null, -1);
        }

        ImGui.SameLine();
        ImGui.Checkbox(
            DevToolUiSettings.T("生物管道", "Creature pipes") + "##WorldMapCreaturePipes",
            ref creaturePipesVisible);
    }

    private static void DrawShortcutSocketHook(
        OrigDrawShortcutSocket orig,
        ImDrawListPtr draw,
        Num.Vector2 point,
        uint shadow,
        uint color,
        bool connected,
        bool emphasized)
    {
        if (!roomPipesVisible) return;
        orig(draw, point, shadow, color, connected, emphasized);
    }

    private static void DrawCreatureShortcutsHook(
        OrigDrawCreatureShortcuts orig,
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize)
    {
        if (!creaturePipesVisible || snapshot == null || getRoomRect == null ||
            localToScreen == null || isLayerVisible == null || drawCreatureShortcutSocket == null)
            return;

        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        uint shadow = ImGui.GetColorU32(ImGuiCol.WindowBg);
        uint labelBorder = ImGui.GetColorU32(new Num.Vector4(0.10f, 0.72f, 0.28f, 1f));
        uint labelText = ImGui.GetColorU32(new Num.Vector4(0.32f, 1.00f, 0.46f, 1f));
        float zoom = zoomField?.GetValue(null) is float value ? value : 1f;

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room == null || !isLayerVisible(room.Layer)) continue;

            WorldMapShortcutPresentation.ShortcutMarker[] holes =
                WorldMapShortcutPresentation.GetCreatureHoles(room.RoomIndex);
            if (holes == null || holes.Length == 0) continue;

            EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
            getRoomRect(room, visual, canvasMin, out Num.Vector2 min, out Num.Vector2 max);

            for (int h = 0; h < holes.Length; h++)
            {
                WorldMapShortcutPresentation.ShortcutMarker hole = holes[h];
                Num.Vector2 point = localToScreen(min, visual, hole.X, hole.Y);
                drawCreatureShortcutSocket(draw, point, shadow);

                if (hole.NodeIndex < 0 || (zoom < 0.48f && room.RoomIndex != snapshot.SelectedRoomIndex))
                    continue;

                string label = hole.NodeIndex.ToString();
                Num.Vector2 labelSize = ImGui.CalcTextSize(label);
                bool left = point.X <= (min.X + max.X) * 0.5f;
                float offset = 13f;
                float x = left ? point.X - labelSize.X - offset : point.X + offset;
                Num.Vector2 labelPos = new(x, point.Y - labelSize.Y * 0.5f);
                Num.Vector2 pad = new(4f, 2f);
                draw.AddRectFilled(labelPos - pad, labelPos + labelSize + pad, shadow, 3f);
                draw.AddRect(labelPos - pad, labelPos + labelSize + pad, labelBorder, 3f, ImDrawFlags.None, 1f);
                draw.AddText(labelPos, labelText, label);
            }
        }
    }

    private static void DisposeHook(ref IDisposable hook)
    {
        try
        {
            hook?.Dispose();
        }
        catch
        {
        }
        finally
        {
            hook = null;
        }
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
