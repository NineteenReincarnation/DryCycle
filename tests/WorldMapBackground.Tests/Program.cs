using System;
using DryCycle.DevUI.DevTool.RWImGui;

internal static class Program
{
    private static int assertions;

    private static int Main()
    {
        try
        {
            GeometryCadence();
            SnapshotCadence();
            SourceRecoveryCadence();
            ShortcutCadence();
            SurfaceGate();

            Console.WriteLine(
                "PASS: " + assertions +
                " assertions; hidden/OH World Map keeps bounded thumbnail catch-up while never authorizing retained GPU rendering.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void GeometryCadence()
    {
        Check(
            WorldMapBackgroundSchedulingPolicy.GeometrySweepIntervalFrames(
                canvasVisible: true,
                thumbnailWorkIncomplete: true,
                catchupAgeMilliseconds: 0d) == 4,
            "Visible World Map uses the interactive background cadence.");

        Check(
            WorldMapBackgroundSchedulingPolicy.GeometrySweepIntervalFrames(
                canvasVisible: false,
                thumbnailWorkIncomplete: true,
                catchupAgeMilliseconds: 0d) == 6,
            "Hidden/OH World Map continues thumbnail catch-up instead of stopping background work.");

        Check(
            WorldMapBackgroundSchedulingPolicy.GeometrySweepIntervalFrames(
                canvasVisible: false,
                thumbnailWorkIncomplete: true,
                catchupAgeMilliseconds: 14999d) == 6,
            "Hidden catch-up cadence remains active throughout the bounded warm-up window.");

        Check(
            WorldMapBackgroundSchedulingPolicy.GeometrySweepIntervalFrames(
                canvasVisible: false,
                thumbnailWorkIncomplete: true,
                catchupAgeMilliseconds: 15000d) == 12,
            "Long-running unresolved rooms fall back to low-frequency hidden maintenance.");

        Check(
            WorldMapBackgroundSchedulingPolicy.GeometrySweepIntervalFrames(
                canvasVisible: false,
                thumbnailWorkIncomplete: false,
                catchupAgeMilliseconds: 0d) == 12,
            "Completed hidden maps use maintenance cadence instead of visible-map cadence.");
    }

    private static void SnapshotCadence()
    {
        Check(
            WorldMapBackgroundSchedulingPolicy.SnapshotIntervalFrames(
                overlayVisible: true) == 2,
            "Visible World Map keeps responsive detached snapshot publication.");

        Check(
            WorldMapBackgroundSchedulingPolicy.SnapshotIntervalFrames(
                overlayVisible: false) == 12,
            "OH/hidden World Map reduces detached snapshot publication overhead.");
    }

    private static void SourceRecoveryCadence()
    {
        Check(
            WorldMapBackgroundSchedulingPolicy.SourceRecoveryIntervalFrames(
                canvasVisible: true,
                recoveryIncomplete: true,
                recoveryAgeMilliseconds: 0d) == 1,
            "Visible World Map source recovery remains every-frame for fast first-open loading.");

        Check(
            WorldMapBackgroundSchedulingPolicy.SourceRecoveryIntervalFrames(
                canvasVisible: false,
                recoveryIncomplete: true,
                recoveryAgeMilliseconds: 1000d) == 6,
            "Hidden/OH source recovery continues at bounded catch-up cadence.");

        Check(
            WorldMapBackgroundSchedulingPolicy.SourceRecoveryIntervalFrames(
                canvasVisible: false,
                recoveryIncomplete: false,
                recoveryAgeMilliseconds: 20000d) == 12,
            "Hidden/OH completed source recovery falls back to low-frequency maintenance.");
    }

    private static void ShortcutCadence()
    {
        Check(
            WorldMapBackgroundSchedulingPolicy.ShortcutPrimeIntervalFrames(
                canvasVisible: true) == 2,
            "Visible shortcut presentation keeps its responsive two-frame cadence.");

        Check(
            WorldMapBackgroundSchedulingPolicy.ShortcutPrimeIntervalFrames(
                canvasVisible: false) == 12,
            "Hidden/OH shortcut presentation uses low-frequency maintenance cadence.");

        Check(
            WorldMapBackgroundSchedulingPolicy.ExactShortcutIntervalFrames(
                canvasVisible: true) == 1,
            "Visible exact shortcut updates remain every-frame.");

        Check(
            WorldMapBackgroundSchedulingPolicy.ExactShortcutIntervalFrames(
                canvasVisible: false) == 12,
            "Hidden/OH exact room-file shortcut scans are throttled.");
    }

    private static void SurfaceGate()
    {
        Check(
            !WorldMapBackgroundSchedulingPolicy.CanRenderRetainedSurface(
                canvasVisible: false,
                mapToolActive: true,
                snapshotAvailable: true,
                canvasWidth: 1280f,
                canvasHeight: 720f),
            "Hidden/OH mode must never authorize an offscreen retained GPU render.");

        Check(
            !WorldMapBackgroundSchedulingPolicy.CanRenderRetainedSurface(
                canvasVisible: true,
                mapToolActive: false,
                snapshotAvailable: true,
                canvasWidth: 1280f,
                canvasHeight: 720f),
            "Retained GPU rendering is forbidden outside the World Map tool.");

        Check(
            !WorldMapBackgroundSchedulingPolicy.CanRenderRetainedSurface(
                canvasVisible: true,
                mapToolActive: true,
                snapshotAvailable: false,
                canvasWidth: 1280f,
                canvasHeight: 720f),
            "Retained GPU rendering requires an available presentation snapshot.");

        Check(
            !WorldMapBackgroundSchedulingPolicy.CanRenderRetainedSurface(
                canvasVisible: true,
                mapToolActive: true,
                snapshotAvailable: true,
                canvasWidth: 1f,
                canvasHeight: 720f),
            "Retained GPU rendering rejects invalid canvas dimensions.");

        Check(
            WorldMapBackgroundSchedulingPolicy.CanRenderRetainedSurface(
                canvasVisible: true,
                mapToolActive: true,
                snapshotAvailable: true,
                canvasWidth: 1280f,
                canvasHeight: 720f),
            "Visible, active World Map with a valid snapshot and canvas may render the retained surface.");
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
