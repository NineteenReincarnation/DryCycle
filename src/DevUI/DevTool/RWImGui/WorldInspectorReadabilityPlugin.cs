using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Presentation/layout guard for the World Workspace inspector.
///
/// A large part of the inspector uses the normal Dear ImGui "widget + label on the right" layout.
/// Several nested editors also call SetNextItemWidth(-1), which consumes the entire available row
/// and leaves no room for that visible label. On the narrow right-hand inspector this made labels
/// such as Position, Layer, creature fields and timeline fields draw beyond the child clip rect.
///
/// Keep the existing authoring code and command paths intact, but give visible labels a guaranteed
/// trailing column while this inspector is being drawn. The same scope also raises the local
/// contrast so the transparent editor remains readable over bright/complex Rain World rooms.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldInspectorReadabilityPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldWorkspace.InspectorReadability";
    public const string PluginName = "DryCycle DevTool World Inspector Readability";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldInspectorReadability.Enable(Logger);
    private void OnDisable() => WorldInspectorReadability.Disable();
}

internal static class WorldInspectorReadability
{
    private const float PreferredInspectorWidth = 420f;
    private const float MinimumInspectorWidth = 400f;
    private const float ChineseLabelReserve = 118f;
    private const float EnglishLabelReserve = 174f;

    private delegate void OrigDrawInspector(EditorMapPresentationSnapshot snapshot);
    private delegate void HookDrawInspector(OrigDrawInspector orig, EditorMapPresentationSnapshot snapshot);

    private delegate void OrigSetNextItemWidth(float itemWidth);
    private delegate void HookSetNextItemWidth(OrigSetNextItemWidth orig, float itemWidth);

    private static readonly HookDrawInspector DrawInspectorHookDelegate = DrawInspectorHook;
    private static readonly HookSetNextItemWidth SetNextItemWidthHookDelegate = SetNextItemWidthHook;

    private static ManualLogSource log;
    private static IDisposable drawInspectorHook;
    private static IDisposable setNextItemWidthHook;
    private static FieldInfo inspectorWidthField;
    private static int inspectorDepth;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags allStatic = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo drawInspector = typeof(WorldWorkspaceView).GetMethod(
                "DrawInspector",
                allStatic,
                null,
                new[] { typeof(EditorMapPresentationSnapshot) },
                null);
            MethodInfo setNextItemWidth = typeof(ImGui).GetMethod(
                "SetNextItemWidth",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(float) },
                null);
            inspectorWidthField = typeof(WorldWorkspaceView).GetField("inspectorWidth", allStatic);

            if (drawInspector == null || setNextItemWidth == null || inspectorWidthField == null)
                throw new MissingMemberException("World Workspace inspector presentation targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            drawInspectorHook = constructor.Invoke(new object[] { drawInspector, DrawInspectorHookDelegate }) as IDisposable;
            setNextItemWidthHook = constructor.Invoke(new object[] { setNextItemWidth, SetNextItemWidthHookDelegate }) as IDisposable;
            if (drawInspectorHook == null || setNextItemWidthHook == null)
                throw new InvalidOperationException("World inspector readability hooks were not created.");

            EnsurePreferredWidth(initial: true);
            enabled = true;
            log?.LogInfo("World inspector readability enabled: label reserve, wider pane and high-contrast local styling active.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("World inspector readability could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref setNextItemWidthHook);
        DisposeHook(ref drawInspectorHook);
        inspectorWidthField = null;
        inspectorDepth = 0;
        enabled = false;
        log = null;
    }

    private static void DrawInspectorHook(OrigDrawInspector orig, EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled)
        {
            orig(snapshot);
            return;
        }

        EnsurePreferredWidth(initial: false);

        // The child window has already begun by the time DrawInspector is called. Paint an opaque
        // local surface over its translucent game-facing background before any inspector widgets are
        // emitted. Inset by one pixel so the parent child-border remains visible.
        Num.Vector2 windowMin = ImGui.GetWindowPos() + new Num.Vector2(1f, 1f);
        Num.Vector2 windowMax = ImGui.GetWindowPos() + ImGui.GetWindowSize() - new Num.Vector2(1f, 1f);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(
            windowMin,
            windowMax,
            ImGui.GetColorU32(new Num.Vector4(0.045f, 0.055f, 0.070f, 0.965f)),
            3f);
        draw.AddRect(
            windowMin,
            windowMax,
            ImGui.GetColorU32(new Num.Vector4(0.32f, 0.38f, 0.46f, 0.86f)),
            3f,
            ImDrawFlags.None,
            1f);

        float labelReserve = CurrentLabelReserve();
        inspectorDepth++;

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Num.Vector2(8f, 5f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Num.Vector2(8f, 8f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Num.Vector2(7f, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);

        ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(0.93f, 0.95f, 0.97f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, new Num.Vector4(0.67f, 0.72f, 0.78f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Num.Vector4(0.075f, 0.095f, 0.125f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Num.Vector4(0.11f, 0.15f, 0.20f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Num.Vector4(0.14f, 0.20f, 0.28f, 1f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Num.Vector4(0.045f, 0.060f, 0.080f, 0.99f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Num.Vector4(0.32f, 0.38f, 0.46f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Num.Vector4(0.28f, 0.34f, 0.42f, 0.82f));
        ImGui.PushStyleColor(ImGuiCol.Header, new Num.Vector4(0.13f, 0.25f, 0.40f, 0.90f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Num.Vector4(0.18f, 0.36f, 0.58f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Num.Vector4(0.22f, 0.43f, 0.70f, 1f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, new Num.Vector4(0.46f, 0.72f, 1f, 1f));

        // Controls that do not explicitly request a width (InputFloat2 / default combos, etc.) also
        // use the same reserved-label column. Calls to SetNextItemWidth(-1) are corrected by the
        // scoped hook below, so both old and new inspector widgets obey one layout rule.
        ImGui.PushItemWidth(-labelReserve);
        try
        {
            orig(snapshot);
        }
        finally
        {
            ImGui.PopItemWidth();
            ImGui.PopStyleColor(12);
            ImGui.PopStyleVar(5);
            inspectorDepth = Math.Max(0, inspectorDepth - 1);
        }
    }

    private static void SetNextItemWidthHook(OrigSetNextItemWidth orig, float itemWidth)
    {
        if (inspectorDepth <= 0 || itemWidth >= 0f)
        {
            orig(itemWidth);
            return;
        }

        // Dear ImGui interprets negative widths as "align N pixels from the right edge". A value of
        // -1 therefore leaves essentially zero room for the visible label that follows the widget.
        // Preserve callers that already reserve more space, but upgrade full-width/near-full-width
        // requests to the inspector's readable label column.
        float reserve = CurrentLabelReserve();
        orig(Math.Min(itemWidth, -reserve));
    }

    private static float CurrentLabelReserve()
    {
        float desired = DevToolUiSettings.IsChinese ? ChineseLabelReserve : EnglishLabelReserve;
        float available;
        try { available = ImGui.GetContentRegionAvail().X; }
        catch { return desired; }

        if (available <= 1f) return desired;

        // Never let the label reserve starve the actual editor. On unusually narrow windows retain
        // at least roughly 180 px for the control itself; the pane-width guard normally keeps us well
        // above this fallback path.
        float maxReserve = Math.Max(92f, available - 180f);
        return Math.Min(desired, maxReserve);
    }

    private static void EnsurePreferredWidth(bool initial)
    {
        if (inspectorWidthField == null) return;
        try
        {
            float current = inspectorWidthField.GetValue(null) is float value ? value : 0f;
            float target = initial ? PreferredInspectorWidth : MinimumInspectorWidth;
            if (current < target)
                inspectorWidthField.SetValue(null, initial ? PreferredInspectorWidth : MinimumInspectorWidth);
        }
        catch
        {
            // A presentation improvement must never make the authoring workspace unusable if a
            // future refactor changes the retained width field.
        }
    }

    private static void DisposeHook(ref IDisposable hook)
    {
        try { hook?.Dispose(); }
        catch { }
        hook = null;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
