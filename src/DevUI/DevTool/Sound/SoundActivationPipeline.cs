using System;
using System.Diagnostics;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Sound;

internal enum SoundActivationPhase
{
    Dormant = 0,
    IndexingSamples = 1,
    LoadingGroups = 2,
    Ready = 3,
    Failed = 4
}

internal readonly struct SoundActivationStatusSnapshot
{
    internal SoundActivationStatusSnapshot(
        SoundActivationPhase phase,
        float progress,
        double lastFrameWorkMilliseconds,
        double maxFrameWorkMilliseconds,
        double clickToReadyMilliseconds,
        int processedSamples,
        int totalSamples,
        int processedGroupFiles,
        int totalGroupFiles,
        string detail)
    {
        Phase = phase;
        Progress = progress;
        LastFrameWorkMilliseconds = lastFrameWorkMilliseconds;
        MaxFrameWorkMilliseconds = maxFrameWorkMilliseconds;
        ClickToReadyMilliseconds = clickToReadyMilliseconds;
        ProcessedSamples = processedSamples;
        TotalSamples = totalSamples;
        ProcessedGroupFiles = processedGroupFiles;
        TotalGroupFiles = totalGroupFiles;
        Detail = detail ?? string.Empty;
    }

    internal SoundActivationPhase Phase { get; }
    internal float Progress { get; }
    internal double LastFrameWorkMilliseconds { get; }
    internal double MaxFrameWorkMilliseconds { get; }
    internal double ClickToReadyMilliseconds { get; }
    internal int ProcessedSamples { get; }
    internal int TotalSamples { get; }
    internal int ProcessedGroupFiles { get; }
    internal int TotalGroupFiles { get; }
    internal string Detail { get; }
    internal bool IsActive => Phase != SoundActivationPhase.Dormant;
    internal bool IsReady => Phase == SoundActivationPhase.Ready;
}

/// <summary>
/// Owns the cold-start lifecycle of the Sound workspace.
///
/// Rain World/AssetManager work deliberately stays on the game thread. The important change is
/// that game-thread-only no longer means same-frame-only: expensive discovery and parsing are
/// resumed in small units under a strict frame budget, then immutable/public snapshots are swapped
/// only after each builder has finished.
/// </summary>
internal static class SoundActivationPipeline
{
    private const double ActiveFrameBudgetMilliseconds = 0.85d;

    private static SoundPage requestedPage;
    private static string[] requestedNames;
    private static SoundActivationPhase phase;
    private static long activationStartedTimestamp;
    private static double lastFrameWorkMilliseconds;
    private static double maxFrameWorkMilliseconds;
    private static double clickToReadyMilliseconds;
    private static string detail = string.Empty;

    internal static SoundActivationStatusSnapshot Current => new(
        phase,
        ComputeProgress(),
        lastFrameWorkMilliseconds,
        maxFrameWorkMilliseconds,
        clickToReadyMilliseconds,
        SoundSampleCatalog.ProcessedSampleCount,
        SoundSampleCatalog.TotalSampleCount,
        SoundGroupLibrary.ProcessedFileCount,
        SoundGroupLibrary.TotalFileCount,
        detail);

    internal static void Step(EditorSession session)
    {
        if (session?.Owner == null)
            return;

        // Keep completed caches alive while other workspaces are active. A workspace switch must
        // never throw away the expensive Sound indexes and make the next visit cold again.
        if (session.ToolMode != EditorToolMode.Sound || session.Owner.activePage is not SoundPage page)
            return;

        string[] names = page.fileNames ?? Array.Empty<string>();
        if (!ReferenceEquals(requestedPage, page) || !ReferenceEquals(requestedNames, names))
            BeginActivation(page, names);

        // Explicit group refreshes can happen while the overall Sound activation is already Ready.
        // Re-enter the group stage without invalidating the sample catalog.
        if (phase == SoundActivationPhase.Ready && !SoundGroupLibrary.IsReady)
        {
            phase = SoundActivationPhase.LoadingGroups;
            activationStartedTimestamp = Stopwatch.GetTimestamp();
            clickToReadyMilliseconds = 0d;
            maxFrameWorkMilliseconds = 0d;
        }

        if (phase == SoundActivationPhase.Ready || phase == SoundActivationPhase.Failed)
            return;

        long frameStarted = Stopwatch.GetTimestamp();
        try
        {
            double remaining = ActiveFrameBudgetMilliseconds;

            if (!SoundSampleCatalog.IsReadyFor(page))
            {
                phase = SoundActivationPhase.IndexingSamples;
                detail = "Indexing ambient sound resources";
                SoundSampleCatalog.BeginRefresh(page);
                SoundSampleCatalog.StepRefresh(remaining);
                remaining = Math.Max(0d, ActiveFrameBudgetMilliseconds - ElapsedMilliseconds(frameStarted));
            }

            if (SoundSampleCatalog.IsReadyFor(page) && !SoundGroupLibrary.IsReady && remaining > 0d)
            {
                phase = SoundActivationPhase.LoadingGroups;
                detail = "Loading sound groups";
                SoundGroupLibrary.EnsureLoaded();
                SoundGroupLibrary.StepReload(remaining);
            }

            if (SoundSampleCatalog.IsReadyFor(page) && SoundGroupLibrary.IsReady)
                CompleteActivation(session);
        }
        catch (Exception error)
        {
            phase = SoundActivationPhase.Failed;
            detail = error.Message;
            Plugin.Logger?.LogWarning("DevTool Sound activation failed: " + error);
        }
        finally
        {
            lastFrameWorkMilliseconds = ElapsedMilliseconds(frameStarted);
            if (lastFrameWorkMilliseconds > maxFrameWorkMilliseconds)
                maxFrameWorkMilliseconds = lastFrameWorkMilliseconds;
        }
    }

    internal static void Reset()
    {
        requestedPage = null;
        requestedNames = null;
        phase = SoundActivationPhase.Dormant;
        activationStartedTimestamp = 0L;
        lastFrameWorkMilliseconds = 0d;
        maxFrameWorkMilliseconds = 0d;
        clickToReadyMilliseconds = 0d;
        detail = string.Empty;
        SoundSampleCatalog.ResetRuntimeState();
        SoundGroupLibrary.ResetRuntimeState();
    }

    private static void BeginActivation(SoundPage page, string[] names)
    {
        requestedPage = page;
        requestedNames = names;
        phase = SoundActivationPhase.IndexingSamples;
        activationStartedTimestamp = Stopwatch.GetTimestamp();
        lastFrameWorkMilliseconds = 0d;
        maxFrameWorkMilliseconds = 0d;
        clickToReadyMilliseconds = 0d;
        detail = "Preparing Sound workspace";

        SoundSampleCatalog.BeginRefresh(page);
        // Group definitions contain sample metadata, so rebuild them against the catalog that is
        // about to become authoritative. BeginReload only prepares a resumable builder; it performs
        // no XML parsing here.
        SoundGroupLibrary.BeginReload(force: true);
    }

    private static void CompleteActivation(EditorSession session)
    {
        phase = SoundActivationPhase.Ready;
        detail = "Ready";
        clickToReadyMilliseconds = activationStartedTimestamp == 0L
            ? 0d
            : ElapsedMilliseconds(activationStartedTimestamp);

        // The first lightweight Sound presentation may have been published while the bootstrap was
        // still in progress. Clear its retained identity so the next presentation pass consumes the
        // newly published full catalog/group snapshots instead of treating the temporary arrays as
        // stable forever.
        SoundEditorPresentationHub.Clear();
        EditorRevisionHub.Mark(session, EditorRevisionKind.Sound);
        SoundPresentationChangeHintHub.MarkFull(session);

        if (DevToolPerformanceMonitor.Enabled)
        {
            Plugin.Logger?.LogInfo(
                "DevTool Sound activation ready in " + clickToReadyMilliseconds.ToString("0.00") +
                " ms; max bootstrap frame " + maxFrameWorkMilliseconds.ToString("0.00") + " ms.");
        }
    }

    private static float ComputeProgress()
    {
        return phase switch
        {
            SoundActivationPhase.Dormant => 0f,
            SoundActivationPhase.IndexingSamples => 0.62f * SoundSampleCatalog.Progress,
            SoundActivationPhase.LoadingGroups => 0.62f + 0.36f * SoundGroupLibrary.Progress,
            SoundActivationPhase.Ready => 1f,
            SoundActivationPhase.Failed => 1f,
            _ => 0f
        };
    }

    private static double ElapsedMilliseconds(long startedTimestamp) =>
        (Stopwatch.GetTimestamp() - startedTimestamp) * 1000d / Stopwatch.Frequency;
}
