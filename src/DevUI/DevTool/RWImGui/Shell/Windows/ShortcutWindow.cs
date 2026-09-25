using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Compact RimWorld-style radial shortcut palette.
///
/// The collapsed state is one draggable mechanical orb. Opening it reveals a smaller global ring
/// followed by the larger current-tool ring. Shortcut metadata stays in DevToolShortcutRegistry;
/// this surface only owns cached presentation data, radial layout, paging, hover help and feedback.
///
/// Performance rules:
/// - registry strings and node sizing are projected only when registry/mode changes;
/// - render-time arrays are fixed and reused;
/// - one NoInputs ImGui window owns drawing only, so its rectangular bounds never capture mouse;
/// - mouse capture is asserted explicitly only over the real circular controls / active gestures.
/// </summary>
internal static class ShortcutWindow
{
    private const int FullCirclePageSize = 8;
    private const int EdgeArcPageSize = 6;
    private const float TooltipDelaySeconds = 0.15f;

    private sealed class ShortcutVisual
    {
        internal DevToolShortcutDescriptor Descriptor;
        internal string KeyText;
        internal float RadiusScale;
    }

    private static readonly Num.Vector4 OrbFill = new(0.025f, 0.035f, 0.046f, 0.96f);
    private static readonly Num.Vector4 OrbFillHover = new(0.045f, 0.085f, 0.112f, 0.99f);
    private static readonly Num.Vector4 OrbBorder = new(0.60f, 0.76f, 0.84f, 0.92f);
    private static readonly Num.Vector4 OrbAccent = new(0.42f, 0.72f, 0.90f, 0.92f);
    private static readonly Num.Vector4 OrbAccentHot = new(0.67f, 0.88f, 1.00f, 1f);
    private static readonly Num.Vector4 OrbPetal = new(0.84f, 0.92f, 0.95f, 0.96f);
    private static readonly Num.Vector4 OrbPetalShadow = new(0.18f, 0.31f, 0.38f, 0.86f);

    private static readonly Num.Vector4 TooltipBackground = new(0.025f, 0.038f, 0.052f, 0.96f);
    private static readonly Num.Vector4 TooltipBorder = new(0.30f, 0.49f, 0.62f, 0.88f);
    private static readonly Num.Vector4 TooltipTitle = new(0.58f, 0.82f, 0.98f, 1f);
    private static readonly Num.Vector4 TooltipChip = new(0.10f, 0.22f, 0.31f, 0.98f);
    private static readonly Num.Vector4 TooltipChipBorder = new(0.36f, 0.65f, 0.82f, 0.92f);
    private static readonly Num.Vector4 TooltipHint = new(0.67f, 0.75f, 0.80f, 0.92f);

    private static readonly Num.Vector4 GlobalLine = new(0.42f, 0.66f, 0.84f, 0.66f);
    private static readonly Num.Vector4 GlobalFill = new(0.052f, 0.096f, 0.140f, 0.97f);
    private static readonly Num.Vector4 CurrentLine = new(0.90f, 0.67f, 0.34f, 0.72f);
    private static readonly Num.Vector4 CurrentFill = new(0.140f, 0.097f, 0.047f, 0.97f);

    private static readonly Num.Vector4 HoverFill = new(0.14f, 0.29f, 0.36f, 0.99f);
    private static readonly Num.Vector4 FeedbackFill = new(0.20f, 0.47f, 0.33f, 1.00f);
    private static readonly Num.Vector4 DisabledFill = new(0.055f, 0.060f, 0.063f, 0.92f);
    private static readonly Num.Vector4 DisabledBorder = new(0.28f, 0.30f, 0.30f, 0.70f);
    private static readonly Num.Vector4 TextColor = new(0.91f, 0.94f, 0.92f, 1f);
    private static readonly Num.Vector4 DisabledText = new(0.48f, 0.51f, 0.50f, 0.82f);

    private static readonly Num.Vector2[] CommonPositions = new Num.Vector2[FullCirclePageSize];
    private static readonly Num.Vector2[] CurrentPositions = new Num.Vector2[FullCirclePageSize];
    private static readonly float[] CommonNodeRadii = new float[FullCirclePageSize];
    private static readonly float[] CurrentNodeRadii = new float[FullCirclePageSize];

    private static long cachedRegistryRevision = -1;
    private static EditorToolMode cachedMode;
    private static bool cachedModeValid;
    private static ShortcutVisual[] cachedCommon = Array.Empty<ShortcutVisual>();
    private static ShortcutVisual[] cachedCurrentMode = Array.Empty<ShortcutVisual>();

    private static bool anchorInitialized;
    private static Num.Vector2 anchor;

    private static bool expanded;
    private static float openAmount;
    private static bool orbPressActive;
    private static bool orbPressDragged;

    private static int commonPage;
    private static int currentPage;
    private static float commonPageMotion;
    private static float currentPageMotion;
    private static int commonPageDirection;
    private static int currentPageDirection;
    private static float currentModeMotion;

    private static int observedFeedbackRevision = -1;
    private static string feedbackKeys = string.Empty;
    private static float feedbackPulse;

    private static DevToolShortcutDescriptor hoverDescriptor;
    private static bool hoverGlobal;
    private static bool hoverOrb;
    private static float hoverSeconds;

    private static bool ownsMouse;

    internal static bool OwnsMouse => ownsMouse;

    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        ownsMouse = false;
        if (snapshot == null || !snapshot.Available) return;

        EnsureShortcutCache(snapshot.ToolMode);

        ImGuiIOPtr io = ImGui.GetIO();
        float dt = Math.Max(0f, Math.Min(0.10f, io.DeltaTime));
        UpdateFeedback(dt);
        UpdateAnimations(dt);

        float geometryScale = Math.Max(0.86f, Math.Min(1.30f, DevToolUiSettings.UiScale));
        float orbRadius = 30f * geometryScale;
        float globalBaseNodeRadius = 24.5f * geometryScale;
        float currentBaseNodeRadius = 28.5f * geometryScale;

        float maxNodeRadius = currentBaseNodeRadius * 1.22f;
        float maxOuter = Math.Max(128f, Math.Min(display.X, display.Y) * 0.45f);
        float outerRadius = Math.Min(176f * geometryScale, maxOuter);
        float innerRadius = Math.Max(84f * geometryScale, outerRadius * 0.60f);

        EnsureAnchor(display, orbRadius);

        bool fullCircle;
        float arcCenter;
        float arcSpan;
        ResolveRadialLayout(
            display,
            outerRadius,
            maxNodeRadius,
            out fullCircle,
            out arcCenter,
            out arcSpan);

        int pageSize = fullCircle ? FullCirclePageSize : EdgeArcPageSize;
        ClampPages(pageSize);

        int commonOffset = commonPage * pageSize;
        int currentOffset = currentPage * pageSize;
        int commonCount = PageCount(cachedCommon.Length, commonOffset, pageSize);
        int currentCount = PageCount(cachedCurrentMode.Length, currentOffset, pageSize);

        float globalReveal = EaseOutCubic(Clamp(openAmount / 0.72f, 0f, 1f));
        float currentReveal = EaseOutCubic(Clamp((openAmount - 0.18f) / 0.82f, 0f, 1f));

        // Global appears first. Current follows with a restrained delayed rotation. On close the
        // ordering naturally reverses because currentReveal reaches zero before globalReveal.
        float commonRotation = commonPageDirection * commonPageMotion * 0.30f;
        float modeRotation = currentModeMotion * 0.18f;
        float currentRotation =
            currentPageDirection * currentPageMotion * 0.34f +
            modeRotation;

        float liveInnerRadius = innerRadius * globalReveal;
        float liveOuterRadius = outerRadius * currentReveal;

        FillNodeRadii(
            cachedCommon,
            commonOffset,
            commonCount,
            globalBaseNodeRadius,
            CommonNodeRadii);
        FillNodeRadii(
            cachedCurrentMode,
            currentOffset,
            currentCount,
            currentBaseNodeRadius,
            CurrentNodeRadii);

        FillPositions(
            CommonPositions,
            commonCount,
            anchor,
            liveInnerRadius,
            arcCenter,
            arcSpan,
            fullCircle,
            commonRotation);

        FillPositions(
            CurrentPositions,
            currentCount,
            anchor,
            liveOuterRadius,
            arcCenter,
            arcSpan,
            fullCircle,
            (fullCircle ? 0.14f : 0.055f) + currentRotation);

        Num.Vector2 boundsMin;
        Num.Vector2 boundsMax;
        ComputeBounds(
            orbRadius,
            commonCount,
            currentCount,
            globalReveal,
            currentReveal,
            display,
            out boundsMin,
            out boundsMax);

        Num.Vector2 windowSize = new(
            Math.Max(2f, boundsMax.X - boundsMin.X),
            Math.Max(2f, boundsMax.Y - boundsMin.Y));

        ImGui.SetNextWindowPos(boundsMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        // The radial surface is a transparent draw host. Explicitly zero the window border as
        // well as its background so the collapsed flower reads as a real circular control rather
        // than a circle trapped inside an invisible ImGui rectangle.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Num.Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoInputs;

        if (!ImGui.Begin("##DevToolShortcutRadial", flags))
        {
            ImGui.End();
            ImGui.PopStyleVar(3);
            return;
        }

        // No InvisibleButton canvas: a large rectangular ImGui hit target would steal room clicks
        // between radial nodes. Input is circle-tested manually and exported via OwnsMouse.
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Num.Vector2 mouse = io.MousePos;

        bool orbHovered =
            DistanceSquared(mouse, anchor) <= orbRadius * orbRadius;

        int commonHoverIndex =
            globalReveal > 0.18f
                ? FindHovered(
                    CommonPositions,
                    CommonNodeRadii,
                    commonCount,
                    mouse)
                : -1;

        int currentHoverIndex =
            currentReveal > 0.18f
                ? FindHovered(
                    CurrentPositions,
                    CurrentNodeRadii,
                    currentCount,
                    mouse)
                : -1;

        bool commonHovered = commonHoverIndex >= 0;
        bool currentHovered = currentHoverIndex >= 0;
        bool focusActive = commonHovered || currentHovered;

        float commonAlpha =
            globalReveal *
            (1f - commonPageMotion * 0.34f);
        float currentAlpha =
            currentReveal *
            (1f - Math.Max(currentPageMotion, currentModeMotion) * 0.38f);

        if (globalReveal > 0.04f)
        {
            DrawArc(
                draw,
                anchor,
                liveInnerRadius,
                arcCenter,
                arcSpan,
                fullCircle,
                WithAlpha(GlobalLine, commonAlpha * (focusActive && !commonHovered ? 0.72f : 1f)),
                1.55f);
        }

        if (currentReveal > 0.04f)
        {
            DrawArc(
                draw,
                anchor,
                liveOuterRadius,
                arcCenter,
                arcSpan,
                fullCircle,
                WithAlpha(CurrentLine, currentAlpha * (focusActive && !currentHovered ? 0.72f : 1f)),
                1.85f);

            DrawCurrentRingTicks(
                draw,
                CurrentPositions,
                currentCount,
                CurrentNodeRadii,
                liveOuterRadius,
                currentAlpha);
        }

        if (globalReveal > 0.10f)
        {
            DrawRing(
                draw,
                snapshot,
                cachedCommon,
                commonOffset,
                commonCount,
                CommonPositions,
                CommonNodeRadii,
                commonHoverIndex,
                GlobalFill,
                GlobalLine,
                commonAlpha,
                focusActive,
                commonHovered);
        }

        if (currentReveal > 0.10f)
        {
            DrawRing(
                draw,
                snapshot,
                cachedCurrentMode,
                currentOffset,
                currentCount,
                CurrentPositions,
                CurrentNodeRadii,
                currentHoverIndex,
                CurrentFill,
                CurrentLine,
                currentAlpha,
                focusActive,
                currentHovered);
        }

        if (globalReveal > 0.18f || currentReveal > 0.18f)
        {
            DrawRingLabelsAndPager(
                draw,
                snapshot.ToolMode,
                innerRadius,
                outerRadius,
                arcCenter,
                arcSpan,
                fullCircle,
                pageSize,
                globalReveal,
                currentReveal);
        }

        DrawOrb(
            draw,
            anchor,
            orbRadius,
            orbHovered,
            globalReveal,
            currentReveal);

        float mouseDistance =
            (float)Math.Sqrt(
                DistanceSquared(mouse, anchor));

        bool pageWheelZone =
            expanded &&
            openAmount > 0.74f &&
            mouseDistance >= Math.Max(orbRadius, innerRadius * 0.58f) &&
            mouseDistance <= outerRadius + maxNodeRadius + 24f;

        bool clickedInsideEnvelope =
            expanded &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
            mouseDistance <= outerRadius + maxNodeRadius + 24f;

        ownsMouse =
            orbHovered ||
            orbPressActive ||
            commonHovered ||
            currentHovered ||
            (pageWheelZone && Math.Abs(io.MouseWheel) > 0.01f) ||
            clickedInsideEnvelope;

        HandleOrbInput(
            io,
            display,
            orbRadius,
            orbHovered);

        HandlePaletteInput(
            io,
            mouseDistance,
            orbRadius,
            innerRadius,
            outerRadius,
            maxNodeRadius,
            commonHovered,
            currentHovered,
            pageSize);

        ShortcutVisual hoveredVisual = null;
        bool hoveredIsGlobal = false;
        if (commonHoverIndex >= 0)
        {
            hoveredVisual =
                cachedCommon[
                    commonOffset +
                    commonHoverIndex];
            hoveredIsGlobal = true;
        }
        else if (currentHoverIndex >= 0)
        {
            hoveredVisual =
                cachedCurrentMode[
                    currentOffset +
                    currentHoverIndex];
        }

        UpdateHoverState(
            hoveredVisual?.Descriptor,
            hoveredIsGlobal,
            orbHovered && hoveredVisual == null,
            dt);

        if (hoverSeconds >= TooltipDelaySeconds)
        {
            if (hoverDescriptor != null)
            {
                bool available =
                    IsShortcutAvailable(
                        hoverDescriptor,
                        snapshot);

                ShowShortcutTooltip(
                    hoverDescriptor,
                    hoverGlobal
                        ? DevToolUiSettings.T("全局", "GLOBAL")
                        : DevToolUiSettings.ToolMode(snapshot.ToolMode),
                    available,
                    PageTotal(
                        hoverGlobal
                            ? cachedCommon.Length
                            : cachedCurrentMode.Length,
                        pageSize) > 1,
                    hoverGlobal
                        ? commonPage
                        : currentPage,
                    PageTotal(
                        hoverGlobal
                            ? cachedCommon.Length
                            : cachedCurrentMode.Length,
                        pageSize));
            }
            else if (hoverOrb)
            {
                ShowOrbTooltip(expanded);
            }
        }

        ImGui.End();
        ImGui.PopStyleVar(3);
    }

    internal static void ResetRetainedState()
    {
        expanded = false;
        openAmount = 0f;
        orbPressActive = false;
        orbPressDragged = false;

        commonPage = 0;
        currentPage = 0;
        commonPageMotion = 0f;
        currentPageMotion = 0f;
        commonPageDirection = 0;
        currentPageDirection = 0;
        currentModeMotion = 0f;

        cachedModeValid = false;

        feedbackPulse = 0f;
        feedbackKeys = string.Empty;

        hoverDescriptor = null;
        hoverOrb = false;
        hoverSeconds = 0f;
        ownsMouse = false;
    }

    private static void EnsureShortcutCache(EditorToolMode mode)
    {
        long revision =
            DevToolShortcutRegistry.Revision;

        if (revision != cachedRegistryRevision)
        {
            cachedRegistryRevision = revision;
            cachedCommon =
                BuildVisuals(
                    DevToolShortcutRegistry.GetCommon());
            cachedCurrentMode =
                BuildVisuals(
                    DevToolShortcutRegistry.GetMode(mode));
            cachedMode = mode;
            cachedModeValid = true;

            commonPage = 0;
            currentPage = 0;
            commonPageMotion = 0f;
            currentPageMotion = 0f;
            currentModeMotion = 1f;
            return;
        }

        if (cachedModeValid &&
            cachedMode == mode)
            return;

        cachedCurrentMode =
            BuildVisuals(
                DevToolShortcutRegistry.GetMode(mode));
        cachedMode = mode;
        cachedModeValid = true;

        currentPage = 0;
        currentPageMotion = 0f;
        currentModeMotion = 1f;
    }

    private static ShortcutVisual[] BuildVisuals(
        DevToolShortcutDescriptor[] descriptors)
    {
        if (descriptors == null ||
            descriptors.Length == 0)
            return Array.Empty<ShortcutVisual>();

        ShortcutVisual[] result =
            new ShortcutVisual[descriptors.Length];

        for (int i = 0; i < descriptors.Length; i++)
        {
            DevToolShortcutDescriptor descriptor =
                descriptors[i];

            string key =
                CompactKey(
                    descriptor?.Input);

            result[i] =
                new ShortcutVisual
                {
                    Descriptor = descriptor,
                    KeyText = key,
                    RadiusScale =
                        ResolveRadiusScale(
                            key)
                };
        }

        return result;
    }

    private static float ResolveRadiusScale(
        string key)
    {
        int length =
            key?.Length ?? 0;

        if (length <= 3)
            return 0.88f;
        if (length <= 6)
            return 1.00f;
        if (length <= 9)
            return 1.10f;

        return 1.20f;
    }

    private static void EnsureAnchor(
        Num.Vector2 display,
        float orbRadius)
    {
        if (!anchorInitialized)
        {
            // Keep the collapsed orb above Rain World's lower-left HUD instead of sitting directly
            // on top of karma / food indicators.
            anchor =
                new Num.Vector2(
                    Math.Max(
                        orbRadius + 14f,
                        80f),
                    Math.Max(
                        orbRadius + 14f,
                        display.Y - 188f));
            anchorInitialized = true;
        }

        anchor.X =
            Clamp(
                anchor.X,
                orbRadius + 8f,
                Math.Max(
                    orbRadius + 8f,
                    display.X - orbRadius - 8f));

        anchor.Y =
            Clamp(
                anchor.Y,
                orbRadius + 8f,
                Math.Max(
                    orbRadius + 8f,
                    display.Y - orbRadius - 8f));
    }

    private static void ResolveRadialLayout(
        Num.Vector2 display,
        float outerRadius,
        float maxNodeRadius,
        out bool fullCircle,
        out float arcCenter,
        out float arcSpan)
    {
        float required =
            outerRadius +
            maxNodeRadius +
            18f;

        float left = anchor.X;
        float right = display.X - anchor.X;
        float top = anchor.Y;
        float bottom = display.Y - anchor.Y;

        fullCircle =
            left >= required &&
            right >= required &&
            top >= required &&
            bottom >= required;

        if (fullCircle)
        {
            arcCenter =
                -(float)Math.PI * 0.5f;
            arcSpan =
                (float)Math.PI * 2f;
            return;
        }

        // Point toward the side with the most real screen space, not merely the quadrant containing
        // the anchor. This remains stable when the orb is dragged near only one edge.
        float x =
            right - left;
        float y =
            bottom - top;

        if (Math.Abs(x) < 0.001f &&
            Math.Abs(y) < 0.001f)
            y = -1f;

        arcCenter =
            (float)Math.Atan2(y, x);

        bool nearHorizontalEdge =
            left < required ||
            right < required;
        bool nearVerticalEdge =
            top < required ||
            bottom < required;

        arcSpan =
            nearHorizontalEdge &&
            nearVerticalEdge
                ? (float)Math.PI * 0.80f
                : (float)Math.PI * 1.04f;
    }

    private static void UpdateFeedback(
        float dt)
    {
        int revision =
            EditorShortcutFeedback.Revision;

        if (revision != observedFeedbackRevision)
        {
            observedFeedbackRevision = revision;

            EditorShortcutFeedbackSnapshot feedback =
                EditorShortcutFeedback.Current;

            feedbackKeys =
                feedback?.Keys ??
                string.Empty;

            feedbackPulse =
                string.IsNullOrWhiteSpace(
                    feedbackKeys)
                    ? 0f
                    : 1f;

            if (ContainsKey(
                    feedbackKeys,
                    "Esc"))
                expanded = false;
        }

        if (feedbackPulse > 0f)
            feedbackPulse =
                Math.Max(
                    0f,
                    feedbackPulse -
                    dt * 1.45f);
    }

    private static void UpdateAnimations(
        float dt)
    {
        float target =
            expanded
                ? 1f
                : 0f;

        float openStep =
            1f -
            (float)Math.Exp(
                -11.5f * dt);

        openAmount +=
            (target - openAmount) *
            openStep;

        if (!expanded &&
            openAmount < 0.002f)
            openAmount = 0f;
        else if (expanded &&
                 openAmount > 0.998f)
            openAmount = 1f;

        commonPageMotion =
            Decay(
                commonPageMotion,
                dt,
                7.8f);

        currentPageMotion =
            Decay(
                currentPageMotion,
                dt,
                7.8f);

        currentModeMotion =
            Decay(
                currentModeMotion,
                dt,
                6.5f);
    }

    private static float Decay(
        float value,
        float dt,
        float speed)
    {
        if (value <= 0.001f)
            return 0f;

        return value *
               (float)Math.Exp(
                   -speed * dt);
    }

    private static void HandleOrbInput(
        ImGuiIOPtr io,
        Num.Vector2 display,
        float orbRadius,
        bool orbHovered)
    {
        if (!orbPressActive &&
            orbHovered &&
            ImGui.IsMouseClicked(
                ImGuiMouseButton.Left))
        {
            orbPressActive = true;
            orbPressDragged = false;
        }

        if (orbPressActive &&
            ImGui.IsMouseDown(
                ImGuiMouseButton.Left))
        {
            Num.Vector2 delta =
                io.MouseDelta;

            if (Math.Abs(delta.X) +
                Math.Abs(delta.Y) >
                0.01f)
            {
                anchor += delta;

                if (delta.X * delta.X +
                    delta.Y * delta.Y >
                    0.20f)
                    orbPressDragged = true;

                anchor.X =
                    Clamp(
                        anchor.X,
                        orbRadius + 8f,
                        Math.Max(
                            orbRadius + 8f,
                            display.X - orbRadius - 8f));

                anchor.Y =
                    Clamp(
                        anchor.Y,
                        orbRadius + 8f,
                        Math.Max(
                            orbRadius + 8f,
                            display.Y - orbRadius - 8f));
            }
        }

        if (orbPressActive &&
            ImGui.IsMouseReleased(
                ImGuiMouseButton.Left))
        {
            if (!orbPressDragged &&
                orbHovered)
                expanded = !expanded;

            orbPressActive = false;
            orbPressDragged = false;
        }
    }

    private static void HandlePaletteInput(
        ImGuiIOPtr io,
        float mouseDistance,
        float orbRadius,
        float innerRadius,
        float outerRadius,
        float maxNodeRadius,
        bool commonHovered,
        bool currentHovered,
        int pageSize)
    {
        if (!expanded ||
            openAmount < 0.74f)
            return;

        float radialLimit =
            outerRadius +
            maxNodeRadius +
            24f;

        bool insidePalette =
            mouseDistance <=
            radialLimit;

        if (insidePalette &&
            Math.Abs(io.MouseWheel) >
            0.01f)
        {
            int direction =
                io.MouseWheel > 0f
                    ? -1
                    : 1;

            float split =
                (innerRadius +
                 outerRadius) *
                0.5f;

            if (mouseDistance <= split &&
                PageTotal(
                    cachedCommon.Length,
                    pageSize) > 1)
            {
                ChangePage(
                    ref commonPage,
                    ref commonPageMotion,
                    ref commonPageDirection,
                    direction,
                    PageTotal(
                        cachedCommon.Length,
                        pageSize));
            }
            else if (PageTotal(
                         cachedCurrentMode.Length,
                         pageSize) > 1)
            {
                ChangePage(
                    ref currentPage,
                    ref currentPageMotion,
                    ref currentPageDirection,
                    direction,
                    PageTotal(
                        cachedCurrentMode.Length,
                        pageSize));
            }
        }

        if (ImGui.IsMouseClicked(
                ImGuiMouseButton.Left))
        {
            bool orbHovered =
                mouseDistance <=
                orbRadius;

            if (!orbHovered &&
                !commonHovered &&
                !currentHovered)
                expanded = false;
        }
    }

    private static void ChangePage(
        ref int page,
        ref float motion,
        ref int motionDirection,
        int direction,
        int total)
    {
        int next =
            WrapPage(
                page + direction,
                total);

        if (next == page)
            return;

        page = next;
        motion = 1f;
        motionDirection = direction;
    }

    private static void UpdateHoverState(
        DevToolShortcutDescriptor descriptor,
        bool global,
        bool orb,
        float dt)
    {
        bool same =
            ReferenceEquals(
                hoverDescriptor,
                descriptor) &&
            hoverGlobal == global &&
            hoverOrb == orb;

        if (!same)
        {
            hoverDescriptor = descriptor;
            hoverGlobal = global;
            hoverOrb = orb;
            hoverSeconds = 0f;
            return;
        }

        if (descriptor != null ||
            orb)
            hoverSeconds += dt;
        else
            hoverSeconds = 0f;
    }

    private static void DrawOrb(
        ImDrawListPtr draw,
        Num.Vector2 center,
        float radius,
        bool hovered,
        float globalReveal,
        float currentReveal)
    {
        float pulse = feedbackPulse;
        float pressScale =
            orbPressActive
                ? 0.955f
                : hovered
                    ? 1.045f
                    : 1f;
        float liveRadius = radius * pressScale;

        // Soft halo first: the button should feel detached from the room without turning into a
        // glowing sticker. Hover and shortcut feedback both reuse the same restrained halo.
        float haloAlpha =
            hovered
                ? 0.19f
                : 0.095f;
        draw.AddCircleFilled(
            center,
            liveRadius + 5.5f,
            ImGui.GetColorU32(WithAlpha(OrbAccent, haloAlpha)),
            40);

        if (pulse > 0f)
        {
            draw.AddCircle(
                center,
                liveRadius + 7f + pulse * 8f,
                ImGui.GetColorU32(WithAlpha(FeedbackFill, 0.42f * pulse)),
                40,
                2.2f);
        }

        draw.AddCircleFilled(
            center,
            liveRadius,
            ImGui.GetColorU32(hovered ? OrbFillHover : OrbFill),
            40);

        // Two concentric rims give the control a machined/glass edge. The inner rim gradually
        // becomes more visible while the palette is open, tying the collapsed flower to its rings.
        draw.AddCircle(
            center,
            liveRadius,
            ImGui.GetColorU32(hovered ? OrbAccentHot : OrbBorder),
            40,
            hovered ? 2.35f : 1.65f);

        draw.AddCircle(
            center,
            Math.Max(4f, liveRadius - 5.5f),
            ImGui.GetColorU32(
                WithAlpha(
                    OrbAccent,
                    0.22f +
                    globalReveal * 0.16f +
                    currentReveal * 0.12f)),
            40,
            1.1f);

        // Five restrained rim cuts echo the five petals without the old dotted gear/toothed look.
        for (int i = 0; i < 5; i++)
        {
            float angle =
                -(float)Math.PI * 0.5f +
                i * ((float)Math.PI * 2f / 5f);
            Num.Vector2 direction = Direction(angle);
            Num.Vector2 inner = center + direction * (liveRadius - 1.5f);
            Num.Vector2 outer = center + direction * (liveRadius + 3.5f);
            draw.AddLine(
                inner,
                outer,
                ImGui.GetColorU32(
                    WithAlpha(
                        hovered ? OrbAccentHot : OrbAccent,
                        hovered ? 0.95f : 0.70f)),
                hovered ? 2.0f : 1.5f);
        }

        // Five-petal plum blossom command mark. Overlapping petals produce a compact flower at every
        // supported scale; a dark seed in the middle keeps the silhouette crisp over bright rooms.
        float petalOrbit = liveRadius * 0.165f;
        float petalRadius = Math.Max(3.2f, liveRadius * 0.145f);
        Num.Vector4 petalColor = hovered ? OrbAccentHot : OrbPetal;
        for (int i = 0; i < 5; i++)
        {
            float angle =
                -(float)Math.PI * 0.5f +
                i * ((float)Math.PI * 2f / 5f);
            Num.Vector2 petalCenter =
                center +
                Direction(angle) *
                petalOrbit;

            draw.AddCircleFilled(
                petalCenter + new Num.Vector2(0.8f, 1.0f),
                petalRadius,
                ImGui.GetColorU32(WithAlpha(OrbPetalShadow, 0.72f)),
                18);
            draw.AddCircleFilled(
                petalCenter,
                petalRadius,
                ImGui.GetColorU32(petalColor),
                18);
        }

        float seedRadius = Math.Max(2.7f, liveRadius * 0.085f);
        draw.AddCircleFilled(
            center,
            seedRadius,
            ImGui.GetColorU32(hovered ? OrbFillHover : OrbFill),
            18);
        draw.AddCircle(
            center,
            seedRadius + 0.6f,
            ImGui.GetColorU32(WithAlpha(OrbAccentHot, hovered ? 0.92f : 0.58f)),
            18,
            1.0f);
    }

    private static void DrawRing(
        ImDrawListPtr draw,
        EditorPresentationSnapshot snapshot,
        ShortcutVisual[] shortcuts,
        int offset,
        int count,
        Num.Vector2[] positions,
        float[] radii,
        int hoveredIndex,
        Num.Vector4 baseFill,
        Num.Vector4 border,
        float alpha,
        bool focusActive,
        bool ringFocused)
    {
        for (int i = 0; i < count; i++)
        {
            ShortcutVisual visual =
                shortcuts[offset + i];

            if (visual?.Descriptor == null)
                continue;

            DevToolShortcutDescriptor shortcut =
                visual.Descriptor;

            Num.Vector2 center =
                positions[i];

            float radius =
                radii[i];

            bool hovered =
                i == hoveredIndex;

            bool feedback =
                feedbackPulse > 0f &&
                KeyMatchesFeedback(
                    shortcut.Input,
                    feedbackKeys);

            bool available =
                IsShortcutAvailable(
                    shortcut,
                    snapshot);

            float focusAlpha =
                focusActive &&
                !hovered &&
                !feedback
                    ? ringFocused
                        ? 0.56f
                        : 0.74f
                    : 1f;

            float nodeAlpha =
                alpha *
                focusAlpha *
                (available
                    ? 1f
                    : 0.60f);

            Num.Vector4 fill =
                !available
                    ? DisabledFill
                    : feedback
                        ? FeedbackFill
                        : hovered
                            ? HoverFill
                            : baseFill;

            Num.Vector4 liveBorder =
                available
                    ? border
                    : DisabledBorder;

            float liveRadius =
                radius +
                (hovered
                    ? radius * 0.08f
                    : 0f) +
                (feedback
                    ? radius *
                      0.10f *
                      feedbackPulse
                    : 0f);

            draw.AddCircleFilled(
                center,
                liveRadius,
                ImGui.GetColorU32(
                    WithAlpha(
                        fill,
                        nodeAlpha)),
                28);

            draw.AddCircle(
                center,
                liveRadius,
                ImGui.GetColorU32(
                    WithAlpha(
                        liveBorder,
                        nodeAlpha)),
                28,
                hovered || feedback
                    ? 2.6f
                    : 1.55f);

            draw.AddCircle(
                center,
                Math.Max(
                    4f,
                    liveRadius - 5f),
                ImGui.GetColorU32(
                    WithAlpha(
                        liveBorder,
                        nodeAlpha * 0.26f)),
                28,
                1f);

            // Radial connector is intentionally focus-only. The old always-on spokes made dense
            // pages read as a spider web.
            if (hovered ||
                feedback)
            {
                draw.AddLine(
                    anchor,
                    center,
                    ImGui.GetColorU32(
                        WithAlpha(
                            liveBorder,
                            nodeAlpha * 0.86f)),
                    feedback
                        ? 2.2f
                        : 1.8f);
            }

            DrawKeyText(
                draw,
                center,
                liveRadius,
                visual.KeyText,
                WithAlpha(
                    available
                        ? TextColor
                        : DisabledText,
                    nodeAlpha));
        }
    }

    private static void DrawKeyText(
        ImDrawListPtr draw,
        Num.Vector2 center,
        float radius,
        string value,
        Num.Vector4 color)
    {
        value ??=
            "?";

        float maxWidth =
            radius *
            1.70f;

        Num.Vector2 size =
            ImGui.CalcTextSize(
                value);

        if (size.X <= maxWidth)
        {
            draw.AddText(
                new Num.Vector2(
                    center.X -
                    size.X * 0.5f,
                    center.Y -
                    size.Y * 0.5f),
                ImGui.GetColorU32(
                    color),
                value);
            return;
        }

        int split =
            FindKeySplit(
                value);

        if (split <= 0 ||
            split >= value.Length - 1)
        {
            string clipped =
                value.Length > 10
                    ? value.Substring(
                          0,
                          9) +
                      "..."
                    : value;

            size =
                ImGui.CalcTextSize(
                    clipped);

            draw.AddText(
                new Num.Vector2(
                    center.X -
                    size.X * 0.5f,
                    center.Y -
                    size.Y * 0.5f),
                ImGui.GetColorU32(
                    color),
                clipped);
            return;
        }

        string first =
            value.Substring(
                    0,
                    split)
                .TrimEnd(
                    '+',
                    ' ');

        string second =
            value.Substring(
                    split)
                .TrimStart(
                    '+',
                    ' ');

        Num.Vector2 firstSize =
            ImGui.CalcTextSize(
                first);

        Num.Vector2 secondSize =
            ImGui.CalcTextSize(
                second);

        float line =
            Math.Max(
                firstSize.Y,
                secondSize.Y);

        draw.AddText(
            new Num.Vector2(
                center.X -
                firstSize.X * 0.5f,
                center.Y -
                line),
            ImGui.GetColorU32(
                color),
            first);

        draw.AddText(
            new Num.Vector2(
                center.X -
                secondSize.X * 0.5f,
                center.Y + 1f),
            ImGui.GetColorU32(
                color),
            second);
    }

    private static void DrawCurrentRingTicks(
        ImDrawListPtr draw,
        Num.Vector2[] positions,
        int count,
        float[] nodeRadii,
        float radius,
        float alpha)
    {
        if (count <= 0 ||
            radius < 4f)
            return;

        for (int i = 0; i < count; i++)
        {
            Num.Vector2 delta =
                positions[i] -
                anchor;

            float length =
                (float)Math.Sqrt(
                    delta.X * delta.X +
                    delta.Y * delta.Y);

            if (length < 0.001f)
                continue;

            Num.Vector2 direction =
                delta /
                length;

            Num.Vector2 inner =
                anchor +
                direction *
                Math.Max(
                    4f,
                    radius - 5f);

            Num.Vector2 outer =
                anchor +
                direction *
                (radius + 5f);

            draw.AddLine(
                inner,
                outer,
                ImGui.GetColorU32(
                    WithAlpha(
                        CurrentLine,
                        alpha * 0.72f)),
                1.5f);
        }
    }

    private static void DrawRingLabelsAndPager(
        ImDrawListPtr draw,
        EditorToolMode mode,
        float innerRadius,
        float outerRadius,
        float arcCenter,
        float arcSpan,
        bool fullCircle,
        int pageSize,
        float globalReveal,
        float currentReveal)
    {
        float labelAngle =
            fullCircle
                ? -(float)Math.PI *
                  0.5f
                : arcCenter -
                  arcSpan *
                  0.5f +
                  0.10f;

        Num.Vector2 globalCenter =
            anchor +
            Direction(
                labelAngle) *
            Math.Max(
                30f,
                innerRadius - 38f);

        Num.Vector2 currentCenter =
            anchor +
            Direction(
                labelAngle) *
            Math.Max(
                52f,
                outerRadius - 30f);

        DrawRingLabel(
            draw,
            DevToolUiSettings.T(
                "全局",
                "GLOBAL"),
            globalCenter,
            WithAlpha(
                GlobalLine,
                globalReveal));

        DrawRingLabel(
            draw,
            DevToolUiSettings.ToolMode(
                mode),
            currentCenter,
            WithAlpha(
                CurrentLine,
                currentReveal));

        int commonPages =
            PageTotal(
                cachedCommon.Length,
                pageSize);

        if (commonPages > 1)
        {
            DrawPagerDots(
                draw,
                globalCenter +
                new Num.Vector2(
                    0f,
                    15f),
                commonPages,
                commonPage,
                GlobalLine,
                globalReveal);
        }

        int currentPages =
            PageTotal(
                cachedCurrentMode.Length,
                pageSize);

        if (currentPages > 1)
        {
            DrawPagerDots(
                draw,
                currentCenter +
                new Num.Vector2(
                    0f,
                    15f),
                currentPages,
                currentPage,
                CurrentLine,
                currentReveal);
        }
    }

    private static void DrawRingLabel(
        ImDrawListPtr draw,
        string text,
        Num.Vector2 center,
        Num.Vector4 color)
    {
        if (string.IsNullOrWhiteSpace(
                text))
            return;

        Num.Vector2 size =
            ImGui.CalcTextSize(
                text);

        draw.AddText(
            new Num.Vector2(
                center.X -
                size.X * 0.5f,
                center.Y -
                size.Y * 0.5f),
            ImGui.GetColorU32(
                color),
            text);
    }

    private static void DrawPagerDots(
        ImDrawListPtr draw,
        Num.Vector2 center,
        int pages,
        int current,
        Num.Vector4 color,
        float alpha)
    {
        int visible =
            Math.Min(
                pages,
                7);

        float gap = 8f;
        float total =
            (visible - 1) *
            gap;

        float start =
            center.X -
            total *
            0.5f;

        for (int i = 0; i < visible; i++)
        {
            bool active =
                i == current;

            Num.Vector2 point =
                new(
                    start +
                    i * gap,
                    center.Y);

            if (active)
            {
                draw.AddCircleFilled(
                    point,
                    2.5f,
                    ImGui.GetColorU32(
                        WithAlpha(
                            color,
                            alpha)),
                    10);
            }
            else
            {
                draw.AddCircle(
                    point,
                    2.2f,
                    ImGui.GetColorU32(
                        WithAlpha(
                            color,
                            alpha * 0.62f)),
                    10,
                    1f);
            }
        }
    }

    private static void DrawArc(
        ImDrawListPtr draw,
        Num.Vector2 center,
        float radius,
        float arcCenter,
        float arcSpan,
        bool fullCircle,
        Num.Vector4 color,
        float thickness)
    {
        if (radius < 2f)
            return;

        int segments =
            fullCircle
                ? 48
                : 28;

        float start =
            fullCircle
                ? -(float)Math.PI *
                  0.5f
                : arcCenter -
                  arcSpan *
                  0.5f;

        float span =
            fullCircle
                ? (float)Math.PI *
                  2f
                : arcSpan;

        Num.Vector2 previous =
            center +
            Direction(
                start) *
            radius;

        for (int i = 1;
             i <= segments;
             i++)
        {
            float angle =
                start +
                span *
                (i /
                 (float)segments);

            Num.Vector2 next =
                center +
                Direction(
                    angle) *
                radius;

            draw.AddLine(
                previous,
                next,
                ImGui.GetColorU32(
                    color),
                thickness);

            previous = next;
        }
    }

    private static void FillNodeRadii(
        ShortcutVisual[] visuals,
        int offset,
        int count,
        float baseRadius,
        float[] output)
    {
        for (int i = 0;
             i < count;
             i++)
        {
            ShortcutVisual visual =
                visuals[
                    offset +
                    i];

            output[i] =
                baseRadius *
                (visual?.RadiusScale ??
                 1f);
        }
    }

    private static void FillPositions(
        Num.Vector2[] output,
        int count,
        Num.Vector2 center,
        float radius,
        float arcCenter,
        float arcSpan,
        bool fullCircle,
        float offset)
    {
        if (count <= 0)
            return;

        if (fullCircle)
        {
            float step =
                (float)Math.PI *
                2f /
                count;

            float start =
                -(float)Math.PI *
                0.5f +
                offset;

            for (int i = 0;
                 i < count;
                 i++)
            {
                output[i] =
                    center +
                    Direction(
                        start +
                        step *
                        i) *
                    radius;
            }

            return;
        }

        if (count == 1)
        {
            output[0] =
                center +
                Direction(
                    arcCenter +
                    offset) *
                radius;
            return;
        }

        float startArc =
            arcCenter -
            arcSpan *
            0.5f +
            offset;

        float usableSpan =
            Math.Max(
                0.15f,
                arcSpan -
                Math.Abs(
                    offset) *
                1.5f);

        float stepArc =
            usableSpan /
            (count - 1);

        for (int i = 0;
             i < count;
             i++)
        {
            output[i] =
                center +
                Direction(
                    startArc +
                    stepArc *
                    i) *
                radius;
        }
    }

    private static void ComputeBounds(
        float orbRadius,
        int commonCount,
        int currentCount,
        float globalReveal,
        float currentReveal,
        Num.Vector2 display,
        out Num.Vector2 min,
        out Num.Vector2 max)
    {
        float margin = 16f;

        min =
            new Num.Vector2(
                anchor.X -
                orbRadius -
                margin,
                anchor.Y -
                orbRadius -
                margin);

        max =
            new Num.Vector2(
                anchor.X +
                orbRadius +
                margin,
                anchor.Y +
                orbRadius +
                margin);

        if (globalReveal > 0.02f)
        {
            for (int i = 0;
                 i < commonCount;
                 i++)
            {
                ExpandBounds(
                    ref min,
                    ref max,
                    CommonPositions[i],
                    CommonNodeRadii[i] +
                    margin);
            }
        }

        if (currentReveal > 0.02f)
        {
            for (int i = 0;
                 i < currentCount;
                 i++)
            {
                ExpandBounds(
                    ref min,
                    ref max,
                    CurrentPositions[i],
                    CurrentNodeRadii[i] +
                    margin);
            }
        }

        min.X =
            Clamp(
                min.X,
                0f,
                Math.Max(
                    0f,
                    display.X -
                    2f));

        min.Y =
            Clamp(
                min.Y,
                0f,
                Math.Max(
                    0f,
                    display.Y -
                    2f));

        max.X =
            Clamp(
                max.X,
                min.X + 2f,
                display.X);

        max.Y =
            Clamp(
                max.Y,
                min.Y + 2f,
                display.Y);
    }

    private static void ExpandBounds(
        ref Num.Vector2 min,
        ref Num.Vector2 max,
        Num.Vector2 point,
        float radius)
    {
        min.X =
            Math.Min(
                min.X,
                point.X -
                radius);

        min.Y =
            Math.Min(
                min.Y,
                point.Y -
                radius);

        max.X =
            Math.Max(
                max.X,
                point.X +
                radius);

        max.Y =
            Math.Max(
                max.Y,
                point.Y +
                radius);
    }

    private static int FindHovered(
        Num.Vector2[] positions,
        float[] radii,
        int count,
        Num.Vector2 mouse)
    {
        for (int i = 0;
             i < count;
             i++)
        {
            float radius =
                radii[i];

            if (DistanceSquared(
                    mouse,
                    positions[i]) <=
                radius *
                radius)
                return i;
        }

        return -1;
    }

    private static bool IsShortcutAvailable(
        DevToolShortcutDescriptor shortcut,
        EditorPresentationSnapshot snapshot)
    {
        if (shortcut == null ||
            snapshot == null)
            return false;

        string id =
            shortcut.Id ??
            string.Empty;

        if (string.Equals(
                id,
                "undo",
                StringComparison.OrdinalIgnoreCase))
            return snapshot.CanUndo;

        if (string.Equals(
                id,
                "redo-shift",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                id,
                "redo-y",
                StringComparison.OrdinalIgnoreCase))
            return snapshot.CanRedo;

        if (string.Equals(
                id,
                "objects-delete",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                id,
                "objects-duplicate",
                StringComparison.OrdinalIgnoreCase))
            return snapshot.Inspector?.HasSelection == true;

        if (string.Equals(
                id,
                "objects-place-cancel",
                StringComparison.OrdinalIgnoreCase))
            return snapshot.PlacementActive;

        return true;
    }

    private static void ShowShortcutTooltip(
        DevToolShortcutDescriptor shortcut,
        string scope,
        bool available,
        bool paged,
        int page,
        int pages)
    {
        if (shortcut == null)
            return;

        string description =
            DevToolUiSettings.IsChinese
                ? shortcut.ChineseDescription
                : shortcut.EnglishDescription;

        if (string.IsNullOrWhiteSpace(description))
            description = shortcut.Id;

        BeginShortcutTooltipCard();

        ImGui.TextColored(TooltipTitle, scope ?? string.Empty);
        ImGui.SameLine();
        ImGui.TextColored(
            TooltipHint,
            DevToolUiSettings.T("快捷键", "SHORTCUT"));

        ImGui.Spacing();
        DrawTooltipKeyChip(shortcut.Input);
        ImGui.Spacing();

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 310f);
        ImGui.TextUnformatted(description ?? string.Empty);
        ImGui.PopTextWrapPos();

        if (!available)
        {
            ImGui.Spacing();
            ImGui.TextColored(
                new Num.Vector4(0.95f, 0.58f, 0.44f, 1f),
                DevToolUiSettings.T(
                    "当前状态下不可用",
                    "Unavailable in the current state"));
        }

        if (paged)
        {
            ImGui.Spacing();
            ImGui.TextColored(
                TooltipHint,
                DevToolUiSettings.T(
                    "滚轮切换 · 第 " + (page + 1) + "/" + pages + " 页",
                    "Wheel to change page · " + (page + 1) + "/" + pages));
        }

        EndShortcutTooltipCard();
    }

    private static void ShowOrbTooltip(bool isExpanded)
    {
        BeginShortcutTooltipCard();

        ImGui.TextColored(
            TooltipTitle,
            DevToolUiSettings.T(
                "快捷键梅花轮",
                "SHORTCUT BLOSSOM"));
        ImGui.SameLine();
        ImGui.TextColored(
            TooltipHint,
            DevToolUiSettings.T(
                "全局 + 当前视图",
                "GLOBAL + CURRENT VIEW"));

        ImGui.Spacing();
        DrawTooltipHintRow(
            DevToolUiSettings.T("点击", "CLICK"),
            isExpanded
                ? DevToolUiSettings.T("收起快捷键", "Close shortcuts")
                : DevToolUiSettings.T("展开快捷键", "Open shortcuts"));
        DrawTooltipHintRow(
            DevToolUiSettings.T("拖动", "DRAG"),
            DevToolUiSettings.T("移动按钮", "Move button"));

        if (isExpanded)
        {
            DrawTooltipHintRow(
                DevToolUiSettings.T("滚轮", "WHEEL"),
                DevToolUiSettings.T("切换快捷键页", "Change shortcut page"));
        }

        EndShortcutTooltipCard();
    }

    private static void BeginShortcutTooltipCard()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        Num.Vector2 display = io.DisplaySize;
        Num.Vector2 mouse = io.MousePos;
        const float cardWidth = 340f;

        float x =
            mouse.X > display.X * 0.66f
                ? mouse.X - cardWidth - 28f
                : mouse.X + 28f;
        float y =
            mouse.Y > display.Y * 0.72f
                ? mouse.Y - 118f
                : mouse.Y + 24f;

        x = Clamp(x, 8f, Math.Max(8f, display.X - cardWidth - 8f));
        y = Clamp(y, 8f, Math.Max(8f, display.Y - 92f));

        ImGui.SetNextWindowPos(new Num.Vector2(x, y), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Num.Vector2(12f, 10f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, TooltipBackground);
        ImGui.PushStyleColor(ImGuiCol.Border, TooltipBorder);
        ImGui.BeginTooltip();
    }

    private static void EndShortcutTooltipCard()
    {
        ImGui.EndTooltip();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
    }

    private static void DrawTooltipKeyChip(string text)
    {
        text =
            string.IsNullOrWhiteSpace(text)
                ? "?"
                : text;

        Num.Vector2 textSize = ImGui.CalcTextSize(text);
        Num.Vector2 padding = new(8f, 4f);
        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        Num.Vector2 min = pos;
        Num.Vector2 max =
            pos +
            textSize +
            padding * 2f;

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(TooltipChip),
            5f);
        draw.AddRect(
            min,
            max,
            ImGui.GetColorU32(TooltipChipBorder),
            5f,
            ImDrawFlags.None,
            1f);
        draw.AddText(
            pos + padding,
            ImGui.GetColorU32(TextColor),
            text);

        ImGui.Dummy(max - min);
    }

    private static void DrawTooltipHintRow(
        string action,
        string description)
    {
        Num.Vector2 actionSize =
            ImGui.CalcTextSize(action ?? string.Empty);
        float chipWidth =
            Math.Max(58f, actionSize.X + 16f);
        Num.Vector2 pos =
            ImGui.GetCursorScreenPos();
        float rowHeight =
            Math.Max(
                ImGui.GetTextLineHeight() + 8f,
                26f);

        ImDrawListPtr draw =
            ImGui.GetWindowDrawList();
        Num.Vector2 chipMax =
            new(
                pos.X + chipWidth,
                pos.Y + rowHeight);
        draw.AddRectFilled(
            pos,
            chipMax,
            ImGui.GetColorU32(TooltipChip),
            5f);
        draw.AddRect(
            pos,
            chipMax,
            ImGui.GetColorU32(TooltipChipBorder),
            5f,
            ImDrawFlags.None,
            1f);

        Num.Vector2 textPos =
            new(
                pos.X + (chipWidth - actionSize.X) * 0.5f,
                pos.Y + (rowHeight - actionSize.Y) * 0.5f);
        draw.AddText(
            textPos,
            ImGui.GetColorU32(TooltipTitle),
            action ?? string.Empty);

        ImGui.SetCursorScreenPos(
            new Num.Vector2(
                pos.X + chipWidth + 10f,
                pos.Y + (rowHeight - ImGui.GetTextLineHeight()) * 0.5f));
        ImGui.TextColored(
            TooltipHint,
            description ?? string.Empty);

        ImGui.SetCursorScreenPos(
            new Num.Vector2(
                pos.X,
                pos.Y + rowHeight + 4f));
    }

    private static void ClampPages(
        int pageSize)
    {
        commonPage =
            ClampPage(
                commonPage,
                PageTotal(
                    cachedCommon.Length,
                    pageSize));

        currentPage =
            ClampPage(
                currentPage,
                PageTotal(
                    cachedCurrentMode.Length,
                    pageSize));
    }

    private static int PageTotal(
        int length,
        int pageSize)
    {
        if (length <= 0)
            return 1;

        return Math.Max(
            1,
            (length +
             pageSize -
             1) /
            pageSize);
    }

    private static int PageCount(
        int length,
        int offset,
        int pageSize)
    {
        if (length <= 0 ||
            offset >= length)
            return 0;

        return Math.Min(
            pageSize,
            length - offset);
    }

    private static int ClampPage(
        int page,
        int total)
    {
        if (total <= 1)
            return 0;
        if (page < 0)
            return 0;
        if (page >= total)
            return total - 1;

        return page;
    }

    private static int WrapPage(
        int page,
        int total)
    {
        if (total <= 1)
            return 0;

        while (page < 0)
            page += total;

        while (page >= total)
            page -= total;

        return page;
    }

    private static string CompactKey(
        string input)
    {
        string value =
            string.IsNullOrWhiteSpace(
                input)
                ? "?"
                : input.Trim();

        value =
            value.Replace(
                " Drag",
                string.Empty);

        value =
            value.Replace(
                "Mouse Wheel",
                "Wheel");

        if (value.Length > 13)
            value =
                value.Substring(
                    0,
                    12) +
                "...";

        return value;
    }

    private static int FindKeySplit(
        string value)
    {
        if (string.IsNullOrEmpty(
                value))
            return -1;

        int middle =
            value.Length /
            2;

        int best = -1;
        int bestDistance =
            int.MaxValue;

        for (int i = 1;
             i < value.Length - 1;
             i++)
        {
            if (value[i] != '+' &&
                value[i] != ' ' &&
                value[i] != '/')
                continue;

            int distance =
                Math.Abs(
                    i -
                    middle);

            if (distance >=
                bestDistance)
                continue;

            bestDistance =
                distance;

            best =
                i +
                (value[i] == '+'
                    ? 1
                    : 0);
        }

        return best;
    }

    private static bool KeyMatchesFeedback(
        string input,
        string feedback)
    {
        if (string.IsNullOrWhiteSpace(
                input) ||
            string.IsNullOrWhiteSpace(
                feedback))
            return false;

        string a =
            NormalizeKey(
                input);

        string b =
            NormalizeKey(
                feedback);

        return string.Equals(
                   a,
                   b,
                   StringComparison.OrdinalIgnoreCase) ||
               b.IndexOf(
                   a,
                   StringComparison.OrdinalIgnoreCase) >= 0 ||
               a.IndexOf(
                   b,
                   StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool ContainsKey(
        string source,
        string key)
    {
        if (string.IsNullOrWhiteSpace(
                source) ||
            string.IsNullOrWhiteSpace(
                key))
            return false;

        return NormalizeKey(
                source)
            .IndexOf(
                NormalizeKey(
                    key),
                StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string NormalizeKey(
        string value) =>
        (value ?? string.Empty)
            .Replace(
                " ",
                string.Empty)
            .Replace(
                "Left",
                string.Empty)
            .Replace(
                "Right",
                string.Empty)
            .Trim();

    private static Num.Vector2 Direction(
        float angle) =>
        new(
            (float)Math.Cos(
                angle),
            (float)Math.Sin(
                angle));

    private static float DistanceSquared(
        Num.Vector2 a,
        Num.Vector2 b)
    {
        float x =
            a.X -
            b.X;

        float y =
            a.Y -
            b.Y;

        return x * x +
               y * y;
    }

    private static float EaseOutCubic(
        float value)
    {
        value =
            Clamp(
                value,
                0f,
                1f);

        float inverse =
            1f -
            value;

        return 1f -
               inverse *
               inverse *
               inverse;
    }

    private static Num.Vector4 WithAlpha(
        Num.Vector4 color,
        float alpha) =>
        new(
            color.X,
            color.Y,
            color.Z,
            color.W *
            Clamp(
                alpha,
                0f,
                1f));

    private static float Clamp(
        float value,
        float min,
        float max) =>
        Math.Max(
            min,
            Math.Min(
                max,
                value));
}
