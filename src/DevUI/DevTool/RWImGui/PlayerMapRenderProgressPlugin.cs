using System;
using System.Globalization;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Adds live Render Map progress to the right-side Player Map inspector. Progress starts while room /
/// authored-terrain bakes are still preparing, then hands off to the incremental deterministic
/// compositor. All percentages are fed by real work counters rather than timers.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapWorkspaceIntegrationPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapRenderProgressPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.PlayerMap.RenderProgress";
    public const string PluginName = "DryCycle Player Map Render Progress";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => PlayerMapRenderProgressView.Enable(Logger);
    private void OnDisable() => PlayerMapRenderProgressView.Disable();
}

internal static class PlayerMapRenderProgressView
{
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map live Render progress enabled through direct view calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        enabled = false;
        log = null;
    }

    internal static void Draw(PlayerMapPresentationSnapshot snapshot)
    {
        if (!enabled)
        {
            PlayerMapWorkspaceView.DrawRenderReportBase(snapshot);
            return;
        }

        PlayerMapRenderProgressSnapshot render = PlayerMapRenderScheduler.Progress;
        if (render?.Running == true)
        {
            DrawRenderProgress(render);
            return;
        }

        PlayerMapRenderPreparationSnapshot preparation = PlayerMapRenderPreparationController.Progress;
        if (preparation?.Running == true)
        {
            DrawPreparationProgress(preparation);
            return;
        }

        PlayerMapWorkspaceView.DrawRenderReportBase(snapshot);
    }

    private static void DrawPreparationProgress(PlayerMapRenderPreparationSnapshot progress)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("Render 进度", "RENDER PROGRESS"));
        ImGui.TextUnformatted(DevToolUiSettings.T("准备房间 Bake", "Preparing room bakes"));
        DrawProgressBar(progress.Progress);

        string percent = (progress.Progress * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        string units = progress.ReadyRooms.ToString(CultureInfo.InvariantCulture) + " / " +
                       progress.TotalRooms.ToString(CultureInfo.InvariantCulture) + " rooms";
        ImGui.TextDisabled(percent + "  ·  " + units);
        if (!string.IsNullOrWhiteSpace(progress.Detail))
            ImGui.TextWrapped(progress.Detail);

        if (progress.CanCancel && DevToolWidgets.ActionButton(
                DevToolUiSettings.T("取消 Render", "Cancel Render"),
                "PlayerMapCancelRenderPreparation",
                DevToolButtonTone.Danger))
            PlayerMapRenderPreparationController.RequestCancel();
    }

    private static void DrawRenderProgress(PlayerMapRenderProgressSnapshot progress)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("Render 进度", "RENDER PROGRESS"));
        ImGui.TextUnformatted(progress.StageLabel ?? string.Empty);
        DrawProgressBar(progress.Progress);

        string percent = (progress.Progress * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        string stagePercent = (progress.StageProgress * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
        ImGui.TextDisabled(percent + "  ·  " + stagePercent + " " + DevToolUiSettings.T("阶段", "stage"));

        if (progress.TotalUnits > 1)
        {
            string units = progress.CompletedUnits.ToString("N0", CultureInfo.InvariantCulture) + " / " +
                           progress.TotalUnits.ToString("N0", CultureInfo.InvariantCulture);
            ImGui.TextDisabled(units);
        }
        if (!string.IsNullOrWhiteSpace(progress.Detail))
            ImGui.TextWrapped(progress.Detail);

        if (progress.CanCancel)
        {
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("取消 Render", "Cancel Render"),
                    "PlayerMapCancelRender",
                    DevToolButtonTone.Danger))
                PlayerMapRenderScheduler.RequestCancel();
        }
        else
        {
            ImGui.TextDisabled(DevToolUiSettings.T("正在提交文件，已不可取消。", "Committing files; cancellation is disabled."));
        }
    }

    private static void DrawProgressBar(float fraction)
    {
        fraction = Math.Max(0f, Math.Min(1f, fraction));
        float width = Math.Max(80f, ImGui.GetContentRegionAvail().X);
        float height = Math.Max(8f, ImGui.GetFrameHeight() * 0.62f);
        Num.Vector2 min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##PlayerMapRenderProgressBar", new Num.Vector2(width, height));
        Num.Vector2 max = min + new Num.Vector2(width, height);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        uint bg = ImGui.GetColorU32(ImGuiCol.FrameBg);
        uint fill = ImGui.GetColorU32(ImGuiCol.HeaderActive);
        uint border = ImGui.GetColorU32(ImGuiCol.Border);
        draw.AddRectFilled(min, max, bg, 2f);
        if (fraction > 0f)
        {
            Num.Vector2 fillMax = new(min.X + width * fraction, max.Y);
            draw.AddRectFilled(min, fillMax, fill, 2f);
        }
        draw.AddRect(min, max, border, 2f);
    }

}
