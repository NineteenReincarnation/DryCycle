using System;
using System.Collections.Generic;
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
/// Resource discovery is incremental, but publication is transactional: Ready is not exposed until
/// the authoritative filename array, resolved sample snapshot, sound groups and final Sound
/// presentation all describe the same generation. This prevents a completed progress bar from
/// dropping back into an empty/stale Library view.
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
    private static double lastPageSwitchMilliseconds;
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
        StatusDetail());

    internal static void Step(EditorSession session)
    {
        if (session?.Owner == null)
            return;

        bool rebuiltFrontendOwnsPresentation =
            EditorInputRouter.FrontendAttached &&
            !EditorUiModeState.UseVanilla &&
            !session.LegacyUiVisible;

        // A legacy SoundPage that did not originate from the rebuilt frontend already owns its
        // vanilla constructor state. Do not start a second resource transaction behind it.
        if (!rebuiltFrontendOwnsPresentation &&
            session.ToolMode == EditorToolMode.Sound &&
            session.Owner.activePage is SoundPage legacySoundPage &&
            !ReferenceEquals(requestedPage, legacySoundPage))
            return;

        // Opportunistically prepare the immutable catalogues before Sound is opened. This never
        // publishes a half-built array to a SoundPage.
        if (rebuiltFrontendOwnsPresentation &&
            (session.ToolMode != EditorToolMode.Sound || session.Owner.activePage is not SoundPage))
        {
            if (!ShouldPausePrewarm(session))
                StepPrewarm();
            return;
        }

        if (session.ToolMode != EditorToolMode.Sound || session.Owner.activePage is not SoundPage page)
            return;

        if (!ReferenceEquals(requestedPage, page))
        {
            BeginActivation(page);
            if (DependenciesReady(page))
                TryCommitReady(session, page);

            // Publish the page shell immediately. Any unfinished resource work starts next frame.
            return;
        }

        if (phase == SoundActivationPhase.Ready && !SoundGroupLibrary.IsReady)
        {
            phase = SoundActivationPhase.LoadingGroups;
            activationStartedTimestamp = Stopwatch.GetTimestamp();
            clickToReadyMilliseconds = 0d;
            maxFrameWorkMilliseconds = 0d;
            detail = "Loading sound groups";
        }

        if (phase == SoundActivationPhase.Ready || phase == SoundActivationPhase.Failed)
            return;

        long frameStarted = Stopwatch.GetTimestamp();
        bool dependenciesReady = false;
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
                    detail = "Preparing ambient sound list";
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

            dependenciesReady = DependenciesReady(page);
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

        if (dependenciesReady && phase != SoundActivationPhase.Failed)
            TryCommitReady(session, page);
    }

    internal static double LastPageSwitchMilliseconds => lastPageSwitchMilliseconds;

    internal static bool IsPrewarmed
    {
        get
        {
            string[] names = SoundFileNameCatalog.CurrentNames ?? Array.Empty<string>();
            return SoundFileNameCatalog.IsReady &&
                   SoundSampleCatalog.IsReadyForNames(names) &&
                   SoundGroupLibrary.IsReady;
        }
    }

    internal static void RecordPageSwitch(double milliseconds)
    {
        lastPageSwitchMilliseconds = Math.Max(0d, milliseconds);
        if (DevToolPerformanceMonitor.Enabled)
            Plugin.Logger?.LogInfo(
                "DevTool Sound page switch/constructor " +
                lastPageSwitchMilliseconds.ToString("0.00") + " ms.");
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
        lastPageSwitchMilliseconds = 0d;
        detail = string.Empty;
        SoundFileNameCatalog.ResetRuntimeState();
        SoundSampleCatalog.ResetRuntimeState();
        SoundGroupLibrary.ResetRuntimeState();
    }

    private static void BeginActivation(SoundPage page)
    {
        requestedPage = page;
        requestedNames = null;
        activationStartedTimestamp = Stopwatch.GetTimestamp();
        lastFrameWorkMilliseconds = 0d;
        maxFrameWorkMilliseconds = 0d;
        clickToReadyMilliseconds = 0d;
        detail = "Preparing Sound workspace";

        SoundFileNameCatalog.EnsureStarted();
        if (SoundFileNameCatalog.IsReady)
        {
            PublishFileNamesToPage(page);
            SoundSampleCatalog.BeginRefresh(page);
        }

        if (!SoundGroupLibrary.IsReady)
            SoundGroupLibrary.EnsureLoaded();

        phase = !SoundFileNameCatalog.IsReady
            ? SoundActivationPhase.DiscoveringFileNames
            : !SoundSampleCatalog.IsReadyFor(page)
                ? SoundActivationPhase.IndexingSamples
                : !SoundGroupLibrary.IsReady
                    ? SoundActivationPhase.LoadingGroups
                    : SoundActivationPhase.IndexingSamples;
    }

    private static bool DependenciesReady(SoundPage page) =>
        page != null &&
        SoundFileNameCatalog.IsReady &&
        ReferenceEquals(page.fileNames, SoundFileNameCatalog.CurrentNames) &&
        SoundSampleCatalog.IsReadyFor(page) &&
        SoundGroupLibrary.IsReady;

    /// <summary>
    /// Commits the completed resource generation to the presentation before exposing Ready. A
    /// failed validation remains in the preparing state and is retried next frame instead of
    /// presenting a false 100% completion.
    /// </summary>
    private static bool TryCommitReady(EditorSession session, SoundPage page)
    {
        if (!DependenciesReady(page))
            return false;

        try
        {
            // Clear the old generation first, then mark the new generation and publish it now. The
            // normal presentation pass later in this frame will observe an already-current snapshot.
            SoundEditorPresentationHub.Clear();
            EditorRevisionHub.Mark(session, EditorRevisionKind.Sound);
            SoundPresentationChangeHintHub.MarkFull(session);
            SoundEditorPresentationHub.Publish(session);

            if (!ValidatePublishedLibrary(page, SoundEditorPresentationHub.Current))
            {
                phase = SoundActivationPhase.IndexingSamples;
                detail = "Preparing final sound list";
                return false;
            }

            phase = SoundActivationPhase.Ready;
            detail = "Ready";
            clickToReadyMilliseconds = activationStartedTimestamp == 0L
                ? 0d
                : ElapsedMilliseconds(activationStartedTimestamp);

            if (DevToolPerformanceMonitor.Enabled)
            {
                Plugin.Logger?.LogInfo(
                    "DevTool Sound activation ready in " + clickToReadyMilliseconds.ToString("0.00") +
                    " ms; page switch/constructor " + lastPageSwitchMilliseconds.ToString("0.00") +
                    " ms; max bootstrap frame " + maxFrameWorkMilliseconds.ToString("0.00") +
                    " ms; worst indivisible unit " +
                    MaxIndivisibleUnitMilliseconds().ToString("0.00") + " ms (" +
                    MaxIndivisibleUnitName() + ").");
            }
            return true;
        }
        catch (Exception error)
        {
            phase = SoundActivationPhase.Failed;
            detail = error.Message;
            Plugin.Logger?.LogWarning("DevTool Sound final publication failed: " + error);
            return false;
        }
    }

    private static bool ValidatePublishedLibrary(
        SoundPage page,
        EditorSoundPresentationSnapshot presentation)
    {
        if (page == null || presentation?.Available != true)
            return false;

        string[] names = page.fileNames ?? Array.Empty<string>();
        EditorSoundSampleSnapshot[] samples = presentation.SampleEntries ?? Array.Empty<EditorSoundSampleSnapshot>();
        if (names.Length == 0)
            return samples.Length == 0;
        if (samples.Length == 0)
            return false;

        // SampleCatalog intentionally de-duplicates names case-insensitively. Validate membership,
        // not raw array length, so harmless duplicate physical filenames cannot prevent readiness.
        HashSet<string> published = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < samples.Length; i++)
        {
            string sample = samples[i]?.Sample;
            if (!string.IsNullOrWhiteSpace(sample))
                published.Add(sample);
        }

        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!published.Contains(name)) return false;
        }
        return true;
    }

    private static bool ShouldPausePrewarm(EditorSession session)
    {
        if (session == null) return true;
        if (session.LegacyTransactions.HasPendingTransaction ||
            session.Owner?.draggedNode != null ||
            session.PlacementActive)
            return true;

        return global::UnityEngine.Input.anyKey;
    }

    private static void StepPrewarm()
    {
        long frameStarted = Stopwatch.GetTimestamp();
        double remaining = PrewarmFrameBudgetMilliseconds;

        SoundFileNameCatalog.EnsureStarted();
        if (!SoundFileNameCatalog.IsReady)
        {
            SoundFileNameCatalog.Step(remaining);
            remaining = Math.Max(0d, PrewarmFrameBudgetMilliseconds - ElapsedMilliseconds(frameStarted));
            if (!SoundFileNameCatalog.IsReady || remaining <= 0d) return;
        }

        string[] names = SoundFileNameCatalog.CurrentNames ?? Array.Empty<string>();
        if (!SoundSampleCatalog.IsReadyForNames(names))
        {
            SoundSampleCatalog.BeginPrewarm();
            SoundSampleCatalog.StepRefresh(remaining);
            remaining = Math.Max(0d, PrewarmFrameBudgetMilliseconds - ElapsedMilliseconds(frameStarted));
            if (!SoundSampleCatalog.IsReadyForNames(names) || remaining <= 0d) return;
        }

        if (!SoundGroupLibrary.IsReady)
        {
            SoundGroupLibrary.EnsureLoaded();
            SoundGroupLibrary.StepReload(remaining);
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

        // Keep the vanilla fallback page coherent without making it authoritative for discovery.
        page.RefreshFilesPage();
    }

    private static string StatusDetail()
    {
        string value = detail;
        if (lastPageSwitchMilliseconds > 0d)
            value += " · page switch " + lastPageSwitchMilliseconds.ToString("0.00") + " ms";

        double worst = MaxIndivisibleUnitMilliseconds();
        if (worst > 0d)
            value += " · worst unit " + worst.ToString("0.00") + " ms · " + MaxIndivisibleUnitName();
        return value;
    }

    private static double MaxIndivisibleUnitMilliseconds() =>
        Math.Max(
            SoundFileNameCatalog.MaxUnitMilliseconds,
            Math.Max(SoundSampleCatalog.MaxBlockingUnitMilliseconds, SoundGroupLibrary.MaxBlockingUnitMilliseconds));

    private static string MaxIndivisibleUnitName()
    {
        double file = SoundFileNameCatalog.MaxUnitMilliseconds;
        double sample = SoundSampleCatalog.MaxBlockingUnitMilliseconds;
        double group = SoundGroupLibrary.MaxBlockingUnitMilliseconds;
        if (group >= sample && group >= file) return SoundGroupLibrary.MaxBlockingUnit;
        if (sample >= file) return SoundSampleCatalog.MaxBlockingUnit;
        return SoundFileNameCatalog.MaxUnit;
    }

    private static float ComputeProgress()
    {
        return phase switch
        {
            SoundActivationPhase.Dormant => 0f,
            SoundActivationPhase.DiscoveringFileNames => 0.24f * SoundFileNameCatalog.Progress,
            SoundActivationPhase.IndexingSamples =>
                SoundSampleCatalog.IsReadyFor(requestedPage)
                    ? 0.98f
                    : 0.24f + 0.44f * SoundSampleCatalog.Progress,
            SoundActivationPhase.LoadingGroups => 0.68f + 0.30f * SoundGroupLibrary.Progress,
            SoundActivationPhase.Ready => 1f,
            SoundActivationPhase.Failed => 1f,
            _ => 0f
        };
    }

    private static double ElapsedMilliseconds(long startedTimestamp) =>
        (Stopwatch.GetTimestamp() - startedTimestamp) * 1000d / Stopwatch.Frequency;
}
