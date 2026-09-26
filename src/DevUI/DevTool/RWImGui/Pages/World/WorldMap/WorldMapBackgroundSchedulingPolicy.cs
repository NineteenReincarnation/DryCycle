using System;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Pure scheduling policy shared by world-map background work and the retained surface.
/// Keeping these decisions free of Unity/ImGui state lets hosted CI verify that hidden/OH mode
/// continues bounded thumbnail preparation without ever authorizing an offscreen GPU render.
/// </summary>
internal static class WorldMapBackgroundSchedulingPolicy
{
    internal const int VisibleGeometrySweepIntervalFrames = 4;
    internal const int VisibleSourceRecoveryIntervalFrames = 1;
    internal const int VisibleSnapshotIntervalFrames = 2;
    internal const int DormantSnapshotIntervalFrames = 12;
    internal const int VisibleShortcutPrimeIntervalFrames = 2;
    internal const int VisibleExactShortcutIntervalFrames = 1;
    internal const int DormantShortcutIntervalFrames = 12;
    internal const int DormantCatchupGeometrySweepIntervalFrames = 6;
    internal const int DormantMaintenanceGeometrySweepIntervalFrames = 12;
    internal const double DormantCatchupWindowMilliseconds = 15000d;

    internal static int GeometrySweepIntervalFrames(
        bool canvasVisible,
        bool thumbnailWorkIncomplete,
        double catchupAgeMilliseconds)
    {
        if (canvasVisible)
            return VisibleGeometrySweepIntervalFrames;

        bool catchup =
            thumbnailWorkIncomplete &&
            Math.Max(0d, catchupAgeMilliseconds) <
            DormantCatchupWindowMilliseconds;

        return catchup
            ? DormantCatchupGeometrySweepIntervalFrames
            : DormantMaintenanceGeometrySweepIntervalFrames;
    }

    internal static int SourceRecoveryIntervalFrames(
        bool canvasVisible,
        bool recoveryIncomplete,
        double recoveryAgeMilliseconds) =>
        canvasVisible
            ? VisibleSourceRecoveryIntervalFrames
            : GeometrySweepIntervalFrames(
                canvasVisible: false,
                thumbnailWorkIncomplete: recoveryIncomplete,
                catchupAgeMilliseconds: recoveryAgeMilliseconds);

    internal static int SnapshotIntervalFrames(
        bool overlayVisible) =>
        overlayVisible
            ? VisibleSnapshotIntervalFrames
            : DormantSnapshotIntervalFrames;

    internal static int ShortcutPrimeIntervalFrames(
        bool canvasVisible) =>
        canvasVisible
            ? VisibleShortcutPrimeIntervalFrames
            : DormantShortcutIntervalFrames;

    internal static int ExactShortcutIntervalFrames(
        bool canvasVisible) =>
        canvasVisible
            ? VisibleExactShortcutIntervalFrames
            : DormantShortcutIntervalFrames;

    internal static bool CanRenderRetainedSurface(
        bool canvasVisible,
        bool mapToolActive,
        bool snapshotAvailable,
        float canvasWidth,
        float canvasHeight) =>
        canvasVisible &&
        mapToolActive &&
        snapshotAvailable &&
        canvasWidth >= 2f &&
        canvasHeight >= 2f;
}
