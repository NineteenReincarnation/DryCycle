using System;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Debug;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Dedicated New-UI page for live LanceScavenger combat diagnostics. The view never reaches into
/// live Rain World objects; it only consumes detached snapshots published by DryCycle.dll.
/// </summary>
internal static class LanceScavengerDebugView
{
    private static int selectedSpawner = int.MinValue;
    private static int selectedNumber = int.MinValue;
    private static bool captureActive;

    private static readonly Num.Vector4 Good = new(0.42f, 0.84f, 0.56f, 1f);
    private static readonly Num.Vector4 Warning = new(0.96f, 0.72f, 0.28f, 1f);
    private static readonly Num.Vector4 Bad = new(0.95f, 0.38f, 0.38f, 1f);
    private static readonly Num.Vector4 Accent = new(0.48f, 0.72f, 1.00f, 1f);
    private static readonly Num.Vector4 Muted = new(0.68f, 0.72f, 0.78f, 1f);
    private static readonly Num.Vector4 GraphBg = new(0.025f, 0.040f, 0.060f, 0.82f);
    private static readonly Num.Vector4 GraphBorder = new(0.24f, 0.34f, 0.46f, 0.95f);
    private static readonly Num.Vector4 Threshold = new(0.95f, 0.74f, 0.32f, 0.90f);
    private static readonly Num.Vector4 QualityLine = new(0.46f, 0.76f, 1.00f, 1f);
    private static readonly Num.Vector4 ReadyDot = new(0.45f, 0.90f, 0.58f, 1f);
    private static readonly Num.Vector4 HardBlockMark = new(0.96f, 0.34f, 0.34f, 0.55f);
    private static readonly Num.Vector4 BraceMark = new(0.35f, 0.58f, 0.94f, 0.16f);
    private static readonly Num.Vector4 Grid = new(0.42f, 0.50f, 0.60f, 0.13f);
    private static readonly Num.Vector4 OwnerMark = new(0.90f, 0.92f, 0.95f, 1f);
    private static readonly Num.Vector4 TargetMark = new(0.55f, 0.62f, 0.72f, 0.64f);
    private static readonly Num.Vector4 CurrentChunkMark = new(0.42f, 0.78f, 1.00f, 0.82f);
    private static readonly Num.Vector4 BestChunkMark = new(0.98f, 0.72f, 0.30f, 0.92f);
    private static readonly Num.Vector4 LanceMark = new(0.96f, 0.82f, 0.43f, 1f);
    private static readonly Num.Vector4 AimMark = new(0.52f, 0.94f, 0.66f, 1f);
    private static readonly Num.Vector4 BodyPathMark = new(0.45f, 0.78f, 1.00f, 0.78f);
    private static readonly Num.Vector4 TipPathMark = new(1.00f, 0.78f, 0.34f, 0.58f);
    private static readonly Num.Vector4 ActualToleranceMark = new(0.45f, 0.94f, 0.62f, 0.90f);
    private static readonly Num.Vector4 PlanningToleranceMark = new(0.72f, 0.50f, 1.00f, 0.78f);

    internal static void Draw(EditorPresentationSnapshot editor, Num.Vector2 display)
    {
        string room = string.IsNullOrEmpty(editor.RoomName) ? editor.Document : editor.RoomName;
        captureActive = true;
        LanceScavengerDebugPresentationHub.SetRequested(true, room);
        LanceScavengerDebugSnapshot snapshot = LanceScavengerDebugPresentationHub.Current;

        float scale = Math.Max(0.75f, Math.Min(3f, DevToolUiSettings.UiScale));
        float leftRail = Math.Min(400f, Math.Max(190f, 230f * Math.Min(1.35f, scale)));
        float width = Math.Min(Math.Max(760f, display.X - 250f), Math.Max(560f, display.X - 32f));
        float height = Math.Min(Math.Max(500f, display.Y - 190f), Math.Max(360f, display.Y - 32f));
        Num.Vector2 pos = new(
            Math.Max(210f, (display.X - width) * 0.56f),
            Math.Max(132f, (display.Y - height) * 0.56f));

        ImGui.SetNextWindowPos(pos, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(Math.Min(620f, Math.Max(420f, display.X - 32f)), 360f),
            new Num.Vector2(Math.Max(620f, display.X - 16f), Math.Max(360f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(DevToolUiSettings.T("长枪拾荒者调试###LanceScavengerDebug", "Lance Scavenger Debug###LanceScavengerDebug"), ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("LanceScavengerDebug");
        DrawHeader(snapshot, room);
        ImGui.Separator();
        ImGui.Spacing();

        Num.Vector2 available = ImGui.GetContentRegionAvail();
        float rosterWidth = Math.Min(leftRail, Math.Max(190f, available.X * 0.30f));
        if (ImGui.BeginChild("##LanceDebugRoster", new Num.Vector2(rosterWidth, available.Y), ImGuiChildFlags.Borders))
        {
            ImGui.SetWindowFontScale(1.16f);
            DevToolWidgets.PaneTitle(DevToolUiSettings.T("个体", "LANCERS"), 1.16f);
            DrawRoster(snapshot);
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("##LanceDebugDetails", new Num.Vector2(0f, available.Y), ImGuiChildFlags.Borders))
        {
            ImGui.SetWindowFontScale(DevToolUiSettings.IsChinese ? 1.18f : 1.14f);
            LanceScavengerDebugEntrySnapshot selected = ResolveSelected(snapshot);
            if (selected == null)
            {
                DevToolWidgets.PaneTitle(DevToolUiSettings.T("实时诊断", "LIVE DIAGNOSTICS"));
                ImGui.TextWrapped(DevToolUiSettings.T(
                    "当前房间没有已实现的长枪拾荒者，或调试采集刚刚开启。进入房间后通常下一帧就会出现。",
                    "No realized Lance Scavenger is currently publishing in this room, or capture has just started. Data normally appears on the next simulation frame."));
            }
            else
            {
                DrawDetails(selected);
            }
        }
        ImGui.EndChild();
        ImGui.End();
    }

    internal static void StopCapture()
    {
        if (!captureActive) return;
        captureActive = false;
        LanceScavengerDebugPresentationHub.SetRequested(false, string.Empty);
        selectedSpawner = int.MinValue;
        selectedNumber = int.MinValue;
    }

    private static void DrawHeader(LanceScavengerDebugSnapshot snapshot, string room)
    {
        int count = snapshot.Entries?.Length ?? 0;
        ImGui.TextColored(Accent, DevToolUiSettings.T("长枪拾荒者战斗诊断", "Lance Scavenger Combat Diagnostics"));
        ImGui.SameLine();
        DevToolWidgets.MutedText("· " + room + " · " + count + DevToolUiSettings.T(" 个体", " units"));
        ImGui.Spacing();
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "只在本页打开时采样；关闭页面后自动停采，不给正常游戏增加持续调试开销。",
            "Sampling is leased only while this page is visible; capture stops automatically when the page closes."));
    }

    private static void DrawRoster(LanceScavengerDebugSnapshot snapshot)
    {
        LanceScavengerDebugEntrySnapshot[] entries = snapshot.Entries ?? Array.Empty<LanceScavengerDebugEntrySnapshot>();
        if (entries.Length == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("等待长枪拾荒者数据…", "Waiting for Lance Scavenger data..."));
            return;
        }

        if (ResolveSelected(snapshot) == null)
        {
            selectedSpawner = entries[0].Spawner;
            selectedNumber = entries[0].Number;
        }

        for (int i = 0; i < entries.Length; i++)
        {
            LanceScavengerDebugEntrySnapshot entry = entries[i];
            bool selected = entry.Spawner == selectedSpawner && entry.Number == selectedNumber;
            string title = "#" + entry.Number + "  " + LocalizeState(entry.State) + "##LanceDebugEntity" + entry.Spawner + ":" + entry.Number;
            if (ImGui.Selectable(title, selected))
            {
                selectedSpawner = entry.Spawner;
                selectedNumber = entry.Number;
            }

            ImGui.Indent(10f);
            ImGui.TextColored(ReasonColor(entry), LocalizeReason(entry.DecisionReason));
            if (!string.IsNullOrEmpty(entry.Target))
            {
                ImGui.TextWrapped(DevToolUiSettings.T("目标：", "Target: ") + entry.Target);
                ImGui.TextDisabled(DevToolUiSettings.T("距离 ", "Distance ") + entry.Distance.ToString("0.0") + " px");
            }
            ImGui.Unindent(10f);
            if (i + 1 < entries.Length)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
            }
        }
    }

    private static LanceScavengerDebugEntrySnapshot ResolveSelected(LanceScavengerDebugSnapshot snapshot)
    {
        LanceScavengerDebugEntrySnapshot[] entries = snapshot.Entries ?? Array.Empty<LanceScavengerDebugEntrySnapshot>();
        for (int i = 0; i < entries.Length; i++)
            if (entries[i].Spawner == selectedSpawner && entries[i].Number == selectedNumber)
                return entries[i];
        return null;
    }

    private static void DrawDetails(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.PaneTitle(DevToolUiSettings.T("实时诊断", "LIVE DIAGNOSTICS"));
        ImGui.TextUnformatted(entry.EntityId);
        ImGui.SameLine();
        ImGui.TextColored(ReasonColor(entry), LocalizeReason(entry.DecisionReason));

        DrawDecision(entry);
        DrawAim(entry);
        DrawGeometry(entry);
        DrawAimHistory(entry);
        DrawSafety(entry);
        DrawTargetMotion(entry);
        DrawCounterSweep(entry);
    }

    private static void DrawDecision(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("决策", "DECISION"));
        KeyValue(DevToolUiSettings.T("状态", "State"), LocalizeState(entry.State) + "  ·  " + entry.StateAge + "f");
        KeyValue(DevToolUiSettings.T("行为", "Behavior"), entry.Behavior);
        KeyValue(DevToolUiSettings.T("暴力等级", "Violence"), entry.Violence + (entry.Afraid ? DevToolUiSettings.T(" · 恐惧", " · Afraid") : string.Empty));
        KeyValue(DevToolUiSettings.T("目标", "Target"), string.IsNullOrEmpty(entry.Target) ? "-" : entry.Target);
        KeyValue(DevToolUiSettings.T("距离", "Distance"), entry.Distance.ToString("0.0") + " px");
        KeyValue(DevToolUiSettings.T("冷却", "Cooldown"), entry.Cooldown + "f");
        KeyValue(DevToolUiSettings.T("冲锋优先权", "Charge priority"), YesNo(entry.ChargePriority));
        KeyValue(DevToolUiSettings.T("当前结论", "Decision"), LocalizeReason(entry.DecisionReason), ReasonColor(entry));
    }

    private static void DrawAim(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("瞄准解", "AIM SOLUTION"));

        float fraction = entry.AimThreshold <= 0f ? 0f : Math.Min(1f, entry.AimQuality / entry.AimThreshold);
        DrawMeter(
            DevToolUiSettings.T("当前质量", "Current quality"),
            entry.AimQuality,
            entry.AimThreshold,
            fraction,
            entry.AimReady ? Good : Warning);

        string bestAge = entry.BestAimAge == int.MaxValue ? "-" : entry.BestAimAge + "f";
        KeyValue(DevToolUiSettings.T("38帧最佳质量", "Best in 38f"), entry.BestAimReady ? entry.BestAimQuality.ToString("0.000") : "-");
        KeyValue(DevToolUiSettings.T("最佳解年龄", "Best age"), bestAge);
        KeyValue(DevToolUiSettings.T("当前 BodyChunk", "Current BodyChunk"), Chunk(entry.TargetChunkIndex));
        KeyValue(DevToolUiSettings.T("最佳 BodyChunk", "Best BodyChunk"), Chunk(entry.BestTargetChunkIndex));
        KeyValue(DevToolUiSettings.T("预计命中帧", "Impact frame"), entry.ImpactFrame > 0 ? entry.ImpactFrame + "f" : "-");
        KeyValue(DevToolUiSettings.T("枪角", "Lance pitch"), entry.LancePitchDegrees.ToString("+0.0;-0.0;0.0") + "°");
        KeyValue(DevToolUiSettings.T("精确解", "Exact solution"), YesNo(entry.ExactAim));
        KeyValue(DevToolUiSettings.T("瞄准点", "Aim point"), "(" + entry.AimX.ToString("0.0") + ", " + entry.AimY.ToString("0.0") + ")");
    }

    private static void DrawGeometry(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("冲锋几何", "CHARGE GEOMETRY"));
        LanceScavengerDebugChunkSnapshot[] chunks = entry.TargetChunks ?? Array.Empty<LanceScavengerDebugChunkSnapshot>();
        float[] bodyX = entry.BodyPathX ?? Array.Empty<float>();
        float[] bodyY = entry.BodyPathY ?? Array.Empty<float>();
        float[] tipPathX = entry.TipPathX ?? Array.Empty<float>();
        float[] tipPathY = entry.TipPathY ?? Array.Empty<float>();
        if (chunks.Length == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("当前没有可绘制目标。", "No target geometry is available."));
            return;
        }

        float tipX = entry.GripX + entry.LanceDirectionX * entry.LanceForwardLength;
        float tipY = entry.GripY + entry.LanceDirectionY * entry.LanceForwardLength;
        float minX = Math.Min(entry.OriginX, Math.Min(entry.GripX, tipX));
        float maxX = Math.Max(entry.OriginX, Math.Max(entry.GripX, tipX));
        float minY = Math.Min(entry.OriginY, Math.Min(entry.GripY, tipY));
        float maxY = Math.Max(entry.OriginY, Math.Max(entry.GripY, tipY));
        bool showAim = entry.TargetChunkIndex >= 0;
        if (showAim)
        {
            minX = Math.Min(minX, entry.AimX);
            maxX = Math.Max(maxX, entry.AimX);
            minY = Math.Min(minY, entry.AimY);
            maxY = Math.Max(maxY, entry.AimY);
        }

        for (int i = 0; i < chunks.Length; i++)
        {
            LanceScavengerDebugChunkSnapshot chunk = chunks[i];
            float planningRadius = chunk.Radius + entry.PlanningBladeHitPadding;
            minX = Math.Min(minX, chunk.X - planningRadius);
            maxX = Math.Max(maxX, chunk.X + planningRadius);
            minY = Math.Min(minY, chunk.Y - planningRadius);
            maxY = Math.Max(maxY, chunk.Y + planningRadius);
        }

        int bodyCount = Math.Min(bodyX.Length, bodyY.Length);
        for (int i = 0; i < bodyCount; i++)
        {
            minX = Math.Min(minX, bodyX[i]);
            maxX = Math.Max(maxX, bodyX[i]);
            minY = Math.Min(minY, bodyY[i]);
            maxY = Math.Max(maxY, bodyY[i]);
        }
        int tipCount = Math.Min(tipPathX.Length, tipPathY.Length);
        for (int i = 0; i < tipCount; i++)
        {
            minX = Math.Min(minX, tipPathX[i]);
            maxX = Math.Max(maxX, tipPathX[i]);
            minY = Math.Min(minY, tipPathY[i]);
            maxY = Math.Max(maxY, tipPathY[i]);
        }

        minX -= 24f;
        maxX += 24f;
        minY -= 24f;
        maxY += 24f;

        Num.Vector2 origin = ImGui.GetCursorScreenPos();
        float width = Math.Max(260f, ImGui.GetContentRegionAvail().X);
        float height = 260f;
        Num.Vector2 size = new(width, height);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(GraphBg), 4f);
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(GraphBorder), 4f);

        float inner = 14f;
        float worldWidth = Math.Max(1f, maxX - minX);
        float worldHeight = Math.Max(1f, maxY - minY);
        float scale = Math.Min((width - inner * 2f) / worldWidth, (height - inner * 2f) / worldHeight);
        scale = Math.Max(0.05f, Math.Min(4f, scale));
        float usedWidth = worldWidth * scale;
        float usedHeight = worldHeight * scale;
        float offsetX = origin.X + (width - usedWidth) * 0.5f;
        float offsetY = origin.Y + (height - usedHeight) * 0.5f;

        Num.Vector2 ToScreen(float x, float y) => new(
            offsetX + (x - minX) * scale,
            offsetY + (maxY - y) * scale);

        int firstGridX = (int)Math.Floor(minX / 20f) * 20;
        int lastGridX = (int)Math.Ceiling(maxX / 20f) * 20;
        int firstGridY = (int)Math.Floor(minY / 20f) * 20;
        int lastGridY = (int)Math.Ceiling(maxY / 20f) * 20;
        uint gridColor = ImGui.GetColorU32(Grid);
        if ((lastGridX - firstGridX) / 20 <= 64)
        {
            for (int x = firstGridX; x <= lastGridX; x += 20)
                draw.AddLine(ToScreen(x, minY), ToScreen(x, maxY), gridColor, 1f);
        }
        if ((lastGridY - firstGridY) / 20 <= 64)
        {
            for (int y = firstGridY; y <= lastGridY; y += 20)
                draw.AddLine(ToScreen(minX, y), ToScreen(maxX, y), gridColor, 1f);
        }

        uint bodyPathColor = ImGui.GetColorU32(BodyPathMark);
        for (int i = 1; i < bodyCount; i++)
        {
            Num.Vector2 a = ToScreen(bodyX[i - 1], bodyY[i - 1]);
            Num.Vector2 b = ToScreen(bodyX[i], bodyY[i]);
            draw.AddLine(a, b, bodyPathColor, 2f);
            if (i % 4 == 0 || i == bodyCount - 1)
                draw.AddCircleFilled(b, 2.5f, bodyPathColor);
        }

        uint tipPathColor = ImGui.GetColorU32(TipPathMark);
        for (int i = 1; i < tipCount; i++)
        {
            Num.Vector2 a = ToScreen(tipPathX[i - 1], tipPathY[i - 1]);
            Num.Vector2 b = ToScreen(tipPathX[i], tipPathY[i]);
            draw.AddLine(a, b, tipPathColor, 1.5f);
        }

        Num.Vector2 owner = ToScreen(entry.OriginX, entry.OriginY);
        Num.Vector2 grip = ToScreen(entry.GripX, entry.GripY);
        Num.Vector2 tip = ToScreen(tipX, tipY);
        draw.AddCircleFilled(owner, 5f, ImGui.GetColorU32(OwnerMark));
        draw.AddLine(grip, tip, ImGui.GetColorU32(LanceMark), 3f);
        draw.AddCircleFilled(grip, 3f, ImGui.GetColorU32(LanceMark));
        draw.AddCircleFilled(tip, 4f, ImGui.GetColorU32(LanceMark));

        LanceScavengerDebugChunkSnapshot currentChunk = null;
        for (int i = 0; i < chunks.Length; i++)
        {
            LanceScavengerDebugChunkSnapshot chunk = chunks[i];
            Num.Vector2 center = ToScreen(chunk.X, chunk.Y);
            float radius = Math.Max(4f, chunk.Radius * scale);
            Num.Vector4 fill = chunk.CurrentAimChunk ? CurrentChunkMark : TargetMark;
            draw.AddCircleFilled(center, radius, ImGui.GetColorU32(fill));
            if (chunk.CurrentAimChunk) currentChunk = chunk;
            if (chunk.BestAimChunk)
                draw.AddCircle(center, radius + 3f, ImGui.GetColorU32(BestChunkMark), 0, 2f);
            draw.AddText(center + new Num.Vector2(radius + 3f, -7f), ImGui.GetColorU32(OwnerMark), "#" + chunk.Index);
        }

        if (showAim)
        {
            Num.Vector2 aim = ToScreen(entry.AimX, entry.AimY);
            if (currentChunk != null)
            {
                float actualRadius = Math.Max(3f, (currentChunk.Radius + entry.ActualBladeHitPadding) * scale);
                float planningRadius = Math.Max(actualRadius + 1f,
                    (currentChunk.Radius + entry.PlanningBladeHitPadding) * scale);
                draw.AddCircle(aim, planningRadius, ImGui.GetColorU32(PlanningToleranceMark), 0, 1.5f);
                draw.AddCircle(aim, actualRadius, ImGui.GetColorU32(ActualToleranceMark), 0, 2f);
            }
            uint aimColor = ImGui.GetColorU32(AimMark);
            draw.AddLine(aim - new Num.Vector2(6f, 0f), aim + new Num.Vector2(6f, 0f), aimColor, 2f);
            draw.AddLine(aim - new Num.Vector2(0f, 6f), aim + new Num.Vector2(0f, 6f), aimColor, 2f);
        }

        ImGui.Dummy(size);
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "蓝线 = 预测身体轨迹 · 黄细线 = 预测枪尖轨迹 · 白点 = 当前身体中心 · 黄粗线 = 当前长枪 · 蓝色块 = 当前瞄准 BodyChunk · 橙圈 = 38帧最佳 BodyChunk · 绿十字 = 预测瞄准点",
            "Blue = predicted body path · thin yellow = predicted tip path · white = current body center · thick yellow = current lance · blue chunk = current target BodyChunk · orange ring = best BodyChunk in 38f · green cross = predicted aim point"));
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "瞄准点周围：绿圈 = 目标半径 + 实际额外容差；紫圈 = 目标半径 + AI规划额外容差。枪刃自身宽度另算。",
            "Around the aim point: green = target radius + real extra padding; purple = target radius + AI planning padding. Blade width is additional."));
        KeyValue(DevToolUiSettings.T("实际额外容差", "Real extra padding"), "+" + entry.ActualBladeHitPadding.ToString("0.0") + " px");
        KeyValue(DevToolUiSettings.T("AI规划额外容差", "AI planning padding"), "+" + entry.PlanningBladeHitPadding.ToString("0.0") + " px");
        KeyValue(DevToolUiSettings.T("枪刃半宽", "Blade half-width"),
            entry.BladeShoulderHalfWidth.ToString("0.00") + " → " + entry.BladeTipHalfWidth.ToString("0.00") + " px");
    }

    private static void DrawAimHistory(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("最近38帧", "LAST 38 FRAMES"));
        float[] quality = entry.AimQualityHistory ?? Array.Empty<float>();
        bool[] ready = entry.AimReadyHistory ?? Array.Empty<bool>();
        bool[] hard = entry.HardBlockHistory ?? Array.Empty<bool>();
        bool[] brace = entry.BraceHistory ?? Array.Empty<bool>();

        Num.Vector2 origin = ImGui.GetCursorScreenPos();
        float width = Math.Max(260f, ImGui.GetContentRegionAvail().X);
        float height = 154f;
        Num.Vector2 size = new(width, height);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        uint bg = ImGui.GetColorU32(GraphBg);
        uint border = ImGui.GetColorU32(GraphBorder);
        draw.AddRectFilled(origin, origin + size, bg, 4f);
        draw.AddRect(origin, origin + size, border, 4f);

        float padX = 12f;
        float padY = 12f;
        float plotLeft = origin.X + padX;
        float plotRight = origin.X + width - padX;
        float plotTop = origin.Y + padY;
        float plotBottom = origin.Y + height - 26f;
        float thresholdY = plotBottom - (plotBottom - plotTop) * Math.Min(1f, Math.Max(0f, entry.AimThreshold));
        draw.AddLine(new Num.Vector2(plotLeft, thresholdY), new Num.Vector2(plotRight, thresholdY), ImGui.GetColorU32(Threshold), 1.5f);

        int count = quality.Length;
        if (count > 0)
        {
            float step = count <= 1 ? 0f : (plotRight - plotLeft) / (count - 1);
            for (int i = 0; i < count; i++)
            {
                float x = count <= 1 ? plotRight : plotLeft + step * i;
                float q = Math.Min(1f, Math.Max(0f, quality[i]));
                float y = plotBottom - (plotBottom - plotTop) * q;

                if (i < brace.Length && brace[i])
                    draw.AddRectFilled(new Num.Vector2(x - Math.Max(1f, step * 0.45f), plotTop),
                        new Num.Vector2(x + Math.Max(1f, step * 0.45f), plotBottom), ImGui.GetColorU32(BraceMark));
                if (i < hard.Length && hard[i])
                    draw.AddRectFilled(new Num.Vector2(x - 1.5f, plotTop), new Num.Vector2(x + 1.5f, plotBottom), ImGui.GetColorU32(HardBlockMark));

                if (i > 0)
                {
                    float previousQ = Math.Min(1f, Math.Max(0f, quality[i - 1]));
                    float previousX = count <= 1 ? plotRight : plotLeft + step * (i - 1);
                    float previousY = plotBottom - (plotBottom - plotTop) * previousQ;
                    draw.AddLine(new Num.Vector2(previousX, previousY), new Num.Vector2(x, y), ImGui.GetColorU32(QualityLine), 2f);
                }

                if (i < ready.Length && ready[i])
                    draw.AddCircleFilled(new Num.Vector2(x, y), 3f, ImGui.GetColorU32(ReadyDot));
            }
        }

        draw.AddText(new Num.Vector2(plotLeft, plotBottom + 5f), ImGui.GetColorU32(Muted),
            DevToolUiSettings.T("旧", "old"));
        string newest = DevToolUiSettings.T("当前", "now");
        float newestWidth = ImGui.CalcTextSize(newest).X;
        draw.AddText(new Num.Vector2(plotRight - newestWidth, plotBottom + 5f), ImGui.GetColorU32(Muted), newest);
        string threshold = DevToolUiSettings.T("阈值 ", "threshold ") + entry.AimThreshold.ToString("0.00");
        float textHeight = ImGui.CalcTextSize("Ag").Y;
        draw.AddText(new Num.Vector2(plotLeft + 4f, Math.Max(plotTop, thresholdY - textHeight)), ImGui.GetColorU32(Threshold), threshold);
        ImGui.Dummy(size);

        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "蓝色带 = 架枪帧 · 绿点 = 当帧有可提交瞄准解 · 红柱 = 硬阻断",
            "Blue band = brace frame · green dot = commit-capable aim · red mark = hard block"));
    }

    private static void DrawSafety(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("路径与提交", "PATH & COMMIT"));
        KeyValue(DevToolUiSettings.T("路径清晰", "Path clear"), YesNo(entry.PathClear), entry.PathClear ? Good : Bad);
        KeyValue(DevToolUiSettings.T("路径原因", "Lane reason"), LocalizeLane(entry.LaneReason));
        KeyValue(DevToolUiSettings.T("友军阻挡", "Friend blocked"), YesNo(entry.FriendBlocked), entry.FriendBlocked ? Bad : Good);
        KeyValue(DevToolUiSettings.T("硬阻断", "Hard blocked"), YesNo(entry.HardBlocked), entry.HardBlocked ? Bad : Good);
        KeyValue(DevToolUiSettings.T("38帧提交已满足", "Commit ready"), YesNo(entry.CommitReady), entry.CommitReady ? Good : Warning);
        KeyValue(DevToolUiSettings.T("当前冲锋机会", "Charge opportunity"), YesNo(entry.ChargeOpportunity), entry.ChargeOpportunity ? Good : Muted);
    }

    private static void DrawTargetMotion(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("目标运动", "TARGET MOTION"));
        KeyValue(DevToolUiSettings.T("稳定度", "Stability"), entry.TargetStability.ToString("0.000"));
        KeyValue(DevToolUiSettings.T("闪避严重度", "Dodge severity"), entry.DodgeSeverity.ToString("0.000"));
        KeyValue(DevToolUiSettings.T("平滑速度", "Smoothed velocity"),
            "(" + entry.TargetVelocityX.ToString("+0.00;-0.00;0.00") + ", " + entry.TargetVelocityY.ToString("+0.00;-0.00;0.00") + ")");
    }

    private static void DrawCounterSweep(LanceScavengerDebugEntrySnapshot entry)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("冲锋反扫", "COUNTER SWEEP"));
        KeyValue(DevToolUiSettings.T("已尝试", "Attempted"), YesNo(entry.CounterSweepAttempted));
        KeyValue(DevToolUiSettings.T("正在反扫", "Active"), YesNo(entry.CounterSweepActive), entry.CounterSweepActive ? Good : Muted);
        KeyValue(DevToolUiSettings.T("触发概率", "Chance"), (entry.CounterSweepChance * 100f).ToString("0.0") + "%");
    }

    private static void DrawMeter(string label, float value, float threshold, float fraction, Num.Vector4 color)
    {
        ImGui.TextUnformatted(label);
        Num.Vector2 origin = ImGui.GetCursorScreenPos();
        float width = Math.Max(160f, ImGui.GetContentRegionAvail().X);
        float height = 21f;
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + new Num.Vector2(width, height), ImGui.GetColorU32(GraphBg), 3f);
        draw.AddRectFilled(origin, origin + new Num.Vector2(width * Math.Min(1f, Math.Max(0f, fraction)), height), ImGui.GetColorU32(color), 3f);
        draw.AddRect(origin, origin + new Num.Vector2(width, height), ImGui.GetColorU32(GraphBorder), 3f);
        string valueText = value.ToString("0.000") + " / " + threshold.ToString("0.000");
        Num.Vector2 textSize = ImGui.CalcTextSize(valueText);
        draw.AddText(new Num.Vector2(origin.X + Math.Max(5f, width - textSize.X - 6f), origin.Y + Math.Max(1f, (height - textSize.Y) * 0.5f)),
            ImGui.GetColorU32(OwnerMark), valueText);
        ImGui.Dummy(new Num.Vector2(width, height));
    }

    private static void KeyValue(string key, string value)
    {
        KeyValue(key, value, new Num.Vector4(1f, 1f, 1f, 1f));
    }

    private static void KeyValue(string key, string value, Num.Vector4 color)
    {
        DevToolWidgets.MutedText(key);
        ImGui.SameLine(168f);
        ImGui.TextColored(color, string.IsNullOrEmpty(value) ? "-" : value);
    }

    private static string Chunk(int index) => index < 0 ? "-" : "#" + index;

    private static string YesNo(bool value) => value
        ? DevToolUiSettings.T("是", "Yes")
        : DevToolUiSettings.T("否", "No");

    private static Num.Vector4 ReasonColor(LanceScavengerDebugEntrySnapshot entry)
    {
        if (entry.HardBlocked || string.Equals(entry.DecisionReason, "inactive", StringComparison.Ordinal) ||
            string.Equals(entry.DecisionReason, "no lance", StringComparison.Ordinal)) return Bad;
        if (entry.ChargeOpportunity || string.Equals(entry.DecisionReason, "brace ready", StringComparison.Ordinal) ||
            string.Equals(entry.DecisionReason, "charging", StringComparison.Ordinal) ||
            string.Equals(entry.DecisionReason, "counter sweep", StringComparison.Ordinal)) return Good;
        return Warning;
    }

    private static string LocalizeState(string value)
    {
        if (!DevToolUiSettings.IsChinese) return value;
        return value switch
        {
            "Observe" => "观察",
            "Threaten" => "威慑",
            "CreateDistance" => "拉开距离",
            "AcquireChargeLane" => "寻找冲锋线",
            "Backstep" => "后撤",
            "Brace" => "架枪",
            "Charge" => "跳冲",
            "FollowUpThrow" => "后续投矛",
            "Recover" => "恢复",
            "CloseDefense" => "近身防御",
            "Disarmed" => "失去长枪",
            _ => value
        };
    }

    private static string LocalizeLane(string value)
    {
        if (!DevToolUiSettings.IsChinese) return string.IsNullOrEmpty(value) ? "-" : value;
        return value switch
        {
            "no target" => "无目标",
            "distance" => "距离不满足",
            "wall / ceiling" => "墙体 / 天花板阻挡",
            "lance blocked" => "枪身轨迹被阻挡",
            "friend in lane" => "友军位于冲锋路径",
            "clear" => "路径清晰且瞄准就绪",
            "aim low" => "路径清晰，但瞄准质量不足",
            "no aim opportunity" => "路径清晰，但当前没有瞄准解",
            _ => string.IsNullOrEmpty(value) ? "-" : value
        };
    }

    private static string LocalizeReason(string value)
    {
        if (!DevToolUiSettings.IsChinese) return string.IsNullOrEmpty(value) ? "-" : value;
        return value switch
        {
            "inactive" => "个体当前不可战斗",
            "no lance" => "没有长枪",
            "no target" => "没有有效目标",
            "not lethal" => "原版敌意尚未达到致命等级",
            "yielding priority" => "正在让出同目标冲锋优先权",
            "too close" => "目标过近",
            "too far" => "目标超出个体冲锋距离",
            "friend in lane" => "友军挡住冲锋线",
            "wall / ceiling" => "身体轨迹被地形阻挡",
            "lance blocked" => "长枪轨迹被地形阻挡",
            "distance" => "距离条件不满足",
            "hard block" => "存在硬阻断",
            "cooldown" => "冲锋冷却中",
            "counter sweep" => "冲锋中，正在反扫",
            "charging" => "正在跳冲",
            "brace ready" => "架枪期间已有可提交瞄准解",
            "aim quality" => "瞄准质量尚未达到阈值",
            "no aim opportunity" => "当前没有可用瞄准解",
            "path blocked" => "冲锋路径被阻挡",
            "ready" => "当前具备冲锋条件",
            "waiting state" => "瞄准与路径可用，等待状态机进入冲锋流程",
            _ => string.IsNullOrEmpty(value) ? "-" : value
        };
    }
}
