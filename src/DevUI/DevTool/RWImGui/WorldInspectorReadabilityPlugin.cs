using System;
using BepInEx;
using BepInEx.Logging;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Presentation/layout guard for the World Workspace inspector. The view enters this scope directly;
/// no DryCycle-owned method or ImGui API is RuntimeDetoured.
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

    internal readonly struct Scope : IDisposable
    {
        private readonly bool active;

        internal Scope(bool active) => this.active = active;

        public void Dispose()
        {
            if (!active) return;
            ImGui.PopItemWidth();
            ImGui.PopStyleColor(12);
            ImGui.PopStyleVar(5);
        }
    }

    private static bool enabled;
    private static bool firstWidthNormalization = true;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        firstWidthNormalization = true;
        logger?.LogInfo("World inspector readability enabled through direct view scope; no self-detours attached.");
    }

    internal static void Disable()
    {
        enabled = false;
        firstWidthNormalization = true;
    }

    internal static void NormalizeInspectorWidth(ref float width)
    {
        if (!enabled) return;
        float target = firstWidthNormalization ? PreferredInspectorWidth : MinimumInspectorWidth;
        if (width < target) width = target;
        firstWidthNormalization = false;
    }

    internal static Scope Enter()
    {
        if (!enabled) return new Scope(false);

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

        ImGui.PushItemWidth(-CurrentLabelReserve());
        return new Scope(true);
    }

    private static float CurrentLabelReserve()
    {
        float desired = DevToolUiSettings.IsChinese ? ChineseLabelReserve : EnglishLabelReserve;
        float available;
        try { available = ImGui.GetContentRegionAvail().X; }
        catch { return desired; }

        if (available <= 1f) return desired;
        float maxReserve = Math.Max(92f, available - 180f);
        return Math.Min(desired, maxReserve);
    }
}
