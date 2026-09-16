using System;
using System.Diagnostics;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;

namespace DryCycle.DevUI.DevTool.Sound;

internal enum SoundActivationPhase
{
    Dormant = 0,
    DiscoveringFileNames = 1,
    IndexingSamples = 2,
    LoadingGroups = 3,
    Ready = 4,
    Failed = 5
}

internal readonly struct SoundActivationStatusSnapshot
{
    internal SoundActivationStatusSnapshot(
        SoundActivationPhase phase,
        float progress,
        double lastFrameWorkMilliseconds,
        double maxFrameWorkMilliseconds,
        double clickToReadyMilliseconds,
        int discoveredFiles,
        int processedFileRoots,
        int totalFileRoots,
        int processedSamples,
        int totalSamples,
        int processedGroupFiles,
        int totalGroupFiles,
        double maxBlockingUnitMilliseconds,
        string maxBlockingUnit,
        string detail)
    {
        Phase = phase;
        Progress = progress;
        LastFrameWorkMilliseconds = lastFrameWorkMilliseconds;
        MaxFrameWorkMilliseconds = maxFrameWorkMilliseconds;
        ClickToReadyMilliseconds = clickToReadyMilliseconds;
        DiscoveredFiles = discoveredFiles;
        ProcessedFileRoots = processedFileRoots;
        TotalFileRoots = totalFileRoots;
        ProcessedSamples = processedSamples;
        TotalSamples = totalSamples;
        ProcessedGroupFiles = processedGroupFiles;
        TotalGroupFiles = totalGroupFiles;
        MaxBlockingUnitMilliseconds = maxBlockingUnitMilliseconds;
        MaxBlockingUnit = maxBlockingUnit ?? string.Empty;
        Detail = detail ?? string.Empty;
    }

    internal SoundActivationPhase Phase { get; }
    internal float Progress { get; }
    internal double LastFrameWorkMilliseconds { get; }
    internal double MaxFrameWorkMilliseconds { get; }
    internal double ClickToReadyMilliseconds { get; }
    internal int DiscoveredFiles { get; }
    internal int ProcessedFileRoots { get; }
    internal int TotalFileRoots { get; }
    internal int ProcessedSamples { get; }
    internal int TotalSamples { get; }
    internal int ProcessedGroupFiles { get; }
    internal int TotalGroupFiles { get; }
    internal double MaxBlockingUnitMilliseconds { get; }
    internal string MaxBlockingUnit { get; }
    internal string Detail { get; }
    internal bool IsActive => Phase != SoundActivationPhase.Dormant;
    internal bool IsReady => Phase == SoundActivationPhase.Ready;
}

/// <summary>
/// Owns the cold-start lifecycle of the Sound workspace.
///
/// Rain World/AssetManager work deliberately stays on the game thread. Game-thread-only does not
/// mean same-frame-only: discovery and parsing are resumed in small units under a frame budget.
/// The ambient filename catalog can prewarm at a lower budget before Sound is ever clicked, and the
/// vanilla SoundPage constructor consumes only the already-published snapshot (or an empty shell).
/// </summary>
internal static class SoundActivationPipeline
{
    private const double ActiveFrameBudgetMilliseconds = 0.85d;
    private const double PrewarmFrameBudgetMilliseconds = 0.22d;

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
        SoundFileNameCatalog.ProcessedEntries,
        SoundFileNameCatalog.ProcessedRoots,
        SoundFileNameCatalog.TotalRoots,
        SoundSampleCatalog.ProcessedSampleCount,
        SoundSampleCatalog.TotalSampleCount,
        SoundGroupLibrary.ProcessedFileCount,
        SoundGroupLibrary.TotalFileCount,
        MaxIndivisibleUnitMilliseconds(),
        MaxIndivisibleUnitName(),
        detail);

    internal static void Step(EditorSession session)
    {
        if (session?.Owner == null)
            return;

        bool rebuiltFrontendOwnsPresentation =
            EditorInputRouter.FrontendAttached &&
            !EditorUiModeState.UseVanilla &&
            !session.LegacyUiVisible;

        // Start the exact file-name discovery before Sound is clicked. This is deliberately tiny
        // background-on-the-main-thread work, not a worker thread: one filesystem iterator step at a
        // time, with the same runtime/thread-safety assumptions as vanilla AssetManager.
        if (rebuiltFrontendOwnsPresentation)
        {
            SoundFileNameCatalog.EnsureStarted();
            if (session.ToolMode != EditorToolMode.Sound || session.Owner.activePage is not SoundPage)
            {
                SoundFileNameCatalog.Step(PrewarmFrameBudgetMilliseconds);
                return;
            }
        }

        if (session.ToolMode != EditorToolMode.Sound || session.Owner.activePage is not SoundPage page)
            return;

        if (!ReferenceEquals(requestedPage, page))
            BeginActivation(page);

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
        bool completedThisFrame = false;
        try
        {
            double remaining = ActiveFrameBudgetMilliseconds;

            if (!SoundFileNameCatalog.IsReady)
            {
                phase = SoundActivationPhase.DiscoveringFileNames;
                detail = "Discovering ambient sound files";
                SoundFileNameCatalog.Step(remaining);
                remaining = Math.Max(0d, ActiveFrameBudgetMilliseconds - ElapsedMilliseconds(frameStarted));
            }

            if (SoundFileNameCatalog.IsReady)
            {
                PublishFileNamesToPage(page);

                if (!SoundSampleCatalog.IsReadyFor(page) && remaining > 0d)
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
            }

            completedThisFrame =
                SoundFileNameCatalog.IsReady &&
                SoundSampleCatalog.IsReadyFor(page) &&
                SoundGroupLibrary.IsReady;
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

        if (completedThisFrame && phase != SoundActivationPhase.Failed)
            CompleteActivation(session);
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
        SoundFileNameCatalog.ResetRuntimeState();
        SoundSampleCatalog.ResetRuntimeState();
        SoundGroupLibrary.ResetRuntimeState();
    }

    private static void BeginActivation(SoundPage page)
    {
        requestedPage = page;
        requestedNames = null;
        phase = SoundFileNameCatalog.IsReady
            ? SoundActivationPhase.IndexingSamples
            : SoundActivationPhase.DiscoveringFileNames;
        activationStartedTimestamp = Stopwatch.GetTimestamp();
        lastFrameWorkMilliseconds = 0d;
        maxFrameWorkMilliseconds = 0d;
        clickToReadyMilliseconds = 0d;
        detail = "Preparing Sound workspace";

        SoundFileNameCatalog.EnsureStarted();
        SoundGroupLibrary.BeginReload(force: true);
        if (SoundFileNameCatalog.IsReady)
        {
            PublishFileNamesToPage(page);
            SoundSampleCatalog.BeginRefresh(page);
        }
    }

    private static void PublishFileNamesToPage(SoundPage page)
    {
        string[] names = SoundFileNameCatalog.CurrentNames ?? Array.Empty<string>();
        if (ReferenceEquals(requestedNames, names) && ReferenceEquals(page.fileNames, names))
            return;

        page.fileNames = names;
        requestedNames = names;

        int maxPerPage = Math.Max(1, page.maxFilesPerPage);
        page.totalFilePages = 1 + (int)(names.Length / (float)maxPerPage + 0.5f);
        if (page.currFilesPage < 0 || page.currFilesPage >= page.totalFilePages)
            page.currFilesPage = 0;

        // This only materializes the at-most-28 legacy buttons after the expensive filename walk has
        // completed. It preserves the vanilla page contract for legacy fallback without rebuilding
        // the complete file catalogue in the constructor.
        page.RefreshFilesPage();
    }

    private static void CompleteActivation(EditorSession session)
    {
        phase = SoundActivationPhase.Ready;
        detail = "Ready";
        clickToReadyMilliseconds = activationStartedTimestamp == 0L
            ? 0d
            : ElapsedMilliseconds(activationStartedTimestamp);

        SoundEditorPresentationHub.Clear();
        EditorRevisionHub.Mark(session, EditorRevisionKind.Sound);
        SoundPresentationChangeHintHub.MarkFull(session);

        if (DevToolPerformanceMonitor.Enabled)
        {
            Plugin.Logger?.LogInfo(
                "DevTool Sound activation ready in " + clickToReadyMilliseconds.ToString("0.00") +
                " ms; max bootstrap frame " + maxFrameWorkMilliseconds.ToString("0.00") +
                " ms; worst indivisible unit " +
                MaxIndivisibleUnitMilliseconds().ToString("0.00") + " ms (" +
                MaxIndivisibleUnitName() + ").");
        }
    }

    private static double MaxIndivisibleUnitMilliseconds() =>
        Math.Max(SoundFileNameCatalog.MaxUnitMilliseconds, SoundGroupLibrary.MaxBlockingUnitMilliseconds);

    private static string MaxIndivisibleUnitName() =>
        SoundGroupLibrary.MaxBlockingUnitMilliseconds > SoundFileNameCatalog.MaxUnitMilliseconds
            ? SoundGroupLibrary.MaxBlockingUnit
            : SoundFileNameCatalog.MaxUnit;

    private static float ComputeProgress()
    {
        return phase switch
        {
            SoundActivationPhase.Dormant => 0f,
            SoundActivationPhase.DiscoveringFileNames => 0.24f * SoundFileNameCatalog.Progress,
            SoundActivationPhase.IndexingSamples => 0.24f + 0.44f * SoundSampleCatalog.Progress,
            SoundActivationPhase.LoadingGroups => 0.68f + 0.30f * SoundGroupLibrary.Progress,
            SoundActivationPhase.Ready => 1f,
            SoundActivationPhase.Failed => 1f,
            _ => 0f
        };
    }

    private static double ElapsedMilliseconds(long startedTimestamp) =>
        (Stopwatch.GetTimestamp() - startedTimestamp) * 1000d / Stopwatch.Frequency;
}
