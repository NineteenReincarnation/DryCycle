using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Owns the cold-start lifecycle of the rebuilt Sound workspace without depending on DevInterface
/// presentation state. Filename discovery, sample indexing and group loading are headless services;
/// the native frontend consumes their immutable snapshots directly.
///
/// Vanilla SoundPage hydration is intentionally not performed here. Compatibility presentation is
/// synchronized only when the user actually enters Vanilla/Legacy mode.
/// </summary>
internal static class SoundActivationPipeline
{
    private const double ActiveFrameBudgetMilliseconds = 0.85d;
    private const double PrewarmFrameBudgetMilliseconds = 0.22d;

    private static EditorSession requestedSession;
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

        // Explicit Vanilla/Legacy mode owns its own presentation lifecycle. Native catalog work is
        // deliberately dormant there; compatibility hydration occurs at the boundary instead of
        // keeping a hidden vanilla page authoritative during normal rebuilt operation.
        if (!rebuiltFrontendOwnsPresentation)
            return;

        if (session.ToolMode != EditorToolMode.Sound)
        {
            if (!ShouldPausePrewarm(session))
                StepPrewarm();
            return;
        }

        if (!ReferenceEquals(requestedSession, session))
        {
            BeginActivation(session);
            if (DependenciesReady())
                TryCommitReady(session);
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
                string[] names = SoundFileNameCatalog.CurrentNames ?? Array.Empty<string>();
                requestedNames = names;

                if (!SoundSampleCatalog.IsReadyForNames(names) && remaining > 0d)
                {
                    phase = SoundActivationPhase.IndexingSamples;
                    detail = "Preparing ambient sound list";
                    SoundSampleCatalog.BeginPrewarm();
                    SoundSampleCatalog.StepRefresh(remaining);
                    remaining = Math.Max(0d, ActiveFrameBudgetMilliseconds - ElapsedMilliseconds(frameStarted));
                }

                if (SoundSampleCatalog.IsReadyForNames(names) && !SoundGroupLibrary.IsReady && remaining > 0d)
                {
                    phase = SoundActivationPhase.LoadingGroups;
                    detail = "Loading sound groups";
                    SoundGroupLibrary.EnsureLoaded();
                    SoundGroupLibrary.StepReload(remaining);
                }
            }

            dependenciesReady = DependenciesReady();
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
            TryCommitReady(session);
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
                "DevTool Sound workspace switch " +
                lastPageSwitchMilliseconds.ToString("0.00") + " ms.");
    }

    internal static void Reset()
    {
        requestedSession = null;
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

    private static void BeginActivation(EditorSession session)
    {
        requestedSession = session;
        requestedNames = null;
        activationStartedTimestamp = Stopwatch.GetTimestamp();
        lastFrameWorkMilliseconds = 0d;
        maxFrameWorkMilliseconds = 0d;
        clickToReadyMilliseconds = 0d;
        detail = "Preparing Sound workspace";

        SoundFileNameCatalog.EnsureStarted();
        if (SoundFileNameCatalog.IsReady)
        {
            requestedNames = SoundFileNameCatalog.CurrentNames ?? Array.Empty<string>();
            if (!SoundSampleCatalog.IsReadyForNames(requestedNames))
                SoundSampleCatalog.BeginPrewarm();
        }

        if (!SoundGroupLibrary.IsReady)
            SoundGroupLibrary.EnsureLoaded();

        string[] names = requestedNames ?? SoundFileNameCatalog.CurrentNames ?? Array.Empty<string>();
        phase = !SoundFileNameCatalog.IsReady
            ? SoundActivationPhase.DiscoveringFileNames
            : !SoundSampleCatalog.IsReadyForNames(names)
                ? SoundActivationPhase.IndexingSamples
                : !SoundGroupLibrary.IsReady
                    ? SoundActivationPhase.LoadingGroups
                    : SoundActivationPhase.IndexingSamples;
    }

    private static bool DependenciesReady()
    {
        string[] names = SoundFileNameCatalog.CurrentNames ?? Array.Empty<string>();
        return SoundFileNameCatalog.IsReady &&
               SoundSampleCatalog.IsReadyForNames(names) &&
               SoundGroupLibrary.IsReady;
    }

    /// <summary>
    /// Commits one complete native resource generation before exposing Ready. No DevInterface page
    /// participates in validation or publication.
    /// </summary>
    private static bool TryCommitReady(EditorSession session)
    {
        if (!DependenciesReady())
            return false;

        try
        {
            requestedNames = SoundFileNameCatalog.CurrentNames ?? Array.Empty<string>();

            SoundEditorPresentationHub.Clear();
            EditorRevisionHub.Mark(session, EditorRevisionKind.Sound);
            SoundPresentationChangeHintHub.MarkFull(session);
            SoundEditorPresentationHub.Publish(session);

            if (!ValidatePublishedLibrary(SoundEditorPresentationHub.Current))
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
                    " ms; workspace switch " + lastPageSwitchMilliseconds.ToString("0.00") +
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

    private static bool ValidatePublishedLibrary(EditorSoundPresentationSnapshot presentation)
    {
        if (presentation?.Available != true)
            return false;

        string[] names = SoundFileNameCatalog.CurrentNames ?? Array.Empty<string>();
        EditorSoundSampleSnapshot[] samples = presentation.SampleEntries ?? Array.Empty<EditorSoundSampleSnapshot>();
        if (names.Length == 0)
            return samples.Length == 0;
        if (samples.Length == 0)
            return false;

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

    private static string StatusDetail()
    {
        string value = detail;
        if (lastPageSwitchMilliseconds > 0d)
            value += " · workspace switch " + lastPageSwitchMilliseconds.ToString("0.00") + " ms";

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
        string[] names = requestedNames ?? SoundFileNameCatalog.CurrentNames ?? Array.Empty<string>();
        return phase switch
        {
            SoundActivationPhase.Dormant => 0f,
            SoundActivationPhase.DiscoveringFileNames => 0.24f * SoundFileNameCatalog.Progress,
            SoundActivationPhase.IndexingSamples =>
                SoundSampleCatalog.IsReadyForNames(names)
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
