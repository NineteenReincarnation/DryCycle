using System;
using System.Collections.Generic;
using System.Diagnostics;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Preview;

/// <summary>
/// Thread-safe bridge from the optional RWImGui frontend to the game-thread preview runtime.
/// The frontend reports only a RoomEffect.Type name. It never mutates RoomSettings itself.
/// </summary>
public static class EffectPreviewIntentHub
{
    private static readonly object Gate = new();
    private static string hoveredType = string.Empty;
    private static string suppressedType = string.Empty;
    private static long lastSeenTicks;
    private static bool cancelRequested;

    public static void Hover(string typeName)
    {
        long now = Stopwatch.GetTimestamp();
        typeName ??= string.Empty;

        lock (Gate)
        {
            if (!string.IsNullOrEmpty(suppressedType))
            {
                if (string.Equals(suppressedType, typeName, StringComparison.Ordinal))
                {
                    hoveredType = string.Empty;
                    lastSeenTicks = now;
                    return;
                }

                suppressedType = string.Empty;
            }

            hoveredType = typeName;
            lastSeenTicks = now;
        }
    }

    public static void ClearHover()
    {
        lock (Gate)
        {
            hoveredType = string.Empty;
            suppressedType = string.Empty;
            lastSeenTicks = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Called before the frontend queues a real AddEffect command for the hovered row.
    /// The game thread consumes this request before command processing and removes the exact
    /// temporary effect object first.
    /// </summary>
    public static void SuppressForCommit(string typeName)
    {
        lock (Gate)
        {
            hoveredType = string.Empty;
            suppressedType = typeName ?? string.Empty;
            lastSeenTicks = Stopwatch.GetTimestamp();
            cancelRequested = true;
        }
    }

    public static string ActiveType => EffectPreviewRuntime.ActiveType;
    public static bool IsActive => EffectPreviewRuntime.IsActive;

    internal static IntentSnapshot Read()
    {
        lock (Gate)
        {
            long seen = lastSeenTicks;
            double ageSeconds = seen <= 0
                ? double.MaxValue
                : (Stopwatch.GetTimestamp() - seen) / (double)Stopwatch.Frequency;

            return new IntentSnapshot(
                ageSeconds <= EffectPreviewRuntime.IntentTimeoutSeconds ? hoveredType : string.Empty,
                ageSeconds);
        }
    }

    internal static bool ConsumeCancelRequest()
    {
        lock (Gate)
        {
            bool value = cancelRequested;
            cancelRequested = false;
            return value;
        }
    }

    internal static void Reset()
    {
        lock (Gate)
        {
            hoveredType = string.Empty;
            suppressedType = string.Empty;
            lastSeenTicks = 0;
            cancelRequested = false;
        }
    }

    internal readonly struct IntentSnapshot
    {
        internal IntentSnapshot(string typeName, double ageSeconds)
        {
            TypeName = typeName ?? string.Empty;
            AgeSeconds = ageSeconds;
        }

        internal string TypeName { get; }
        internal double AgeSeconds { get; }
    }
}

/// <summary>
/// Universal RoomEffect preview runtime.
///
/// Stage one inserts an ordinary temporary RoomEffect at the front of RoomSettings.effects so any
/// vanilla or mod code that reads GetEffect/GetEffectAmount sees it without a compatibility API.
/// Stage two may bootstrap load-time runtime objects through generic IL/HookGen inference. Runtime
/// objects and their synchronously spawned descendants are owned by one transaction and rolled back
/// by identity when hover ends.
///
/// Advanced preview safety is learned at runtime per RoomEffect.Type. A failed/contaminating type is
/// blocked only from stage two for the remainder of the plugin session; stage-one preview continues.
/// </summary>
internal static class EffectPreviewRuntime
{
    internal const double IntentTimeoutSeconds = 0.35;
    private const double HoverDelaySeconds = 0.18;
    private const float PreviewAmount = 0.50f;

    private static bool enabled;
    private static global::Room activeRoom;
    private static RoomSettings activeSettings;
    private static RoomSettings.RoomEffect previewEffect;
    private static EffectPreviewOwnershipTransaction ownership;
    private static string activeType = string.Empty;
    private static string pendingType = string.Empty;
    private static long pendingSinceTicks;
    private static int baselineEffectCount;

    internal static bool IsActive => previewEffect != null;
    internal static string ActiveType => activeType ?? string.Empty;

    internal static bool IsPreviewEffect(RoomSettings.RoomEffect effect) =>
        effect != null && previewEffect != null && ReferenceEquals(effect, previewEffect);

    internal static void Enable()
    {
        if (enabled) return;
        EffectPreviewObjectCapture.Enable();
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        Reset();
        EffectPreviewSafetyRegistry.Clear();
        EffectPreviewObjectCapture.Disable();
        enabled = false;
    }

    internal static void BeforeDevUiUpdate(global::DevInterface.DevUI ui)
    {
        if (EffectPreviewIntentHub.ConsumeCancelRequest())
        {
            End("commit");
            ClearPending();
            return;
        }

        if (!IsActive) return;

        EffectPreviewIntentHub.IntentSnapshot intent = EffectPreviewIntentHub.Read();
        bool invalidOwner = ui == null ||
                            !ReferenceEquals(ui.room, activeRoom) ||
                            !ReferenceEquals(ui.room?.roomSettings, activeSettings);
        bool hidden = EditorUiModeState.UseVanilla || EditorUiModeState.OverlayHidden;
        bool switched = string.IsNullOrEmpty(intent.TypeName) ||
                        !string.Equals(intent.TypeName, activeType, StringComparison.Ordinal);

        if (invalidOwner || hidden || switched)
            End(invalidOwner ? "owner changed" : hidden ? "frontend hidden" : "hover changed");
    }

    internal static void AfterDevUiUpdate(global::DevInterface.DevUI ui)
    {
        EditorSession session = DevToolSessionHub.Current;
        if (session == null || ui == null || !ReferenceEquals(session.Owner, ui) ||
            session.ToolMode != EditorToolMode.Room || session.RoomSettings?.effects == null ||
            EditorUiModeState.UseVanilla || EditorUiModeState.OverlayHidden)
        {
            End("editor unavailable");
            ClearPending();
            return;
        }

        EffectPreviewIntentHub.IntentSnapshot intent = EffectPreviewIntentHub.Read();
        string requested = intent.TypeName;
        if (string.IsNullOrEmpty(requested))
        {
            End("hover expired");
            ClearPending();
            return;
        }

        if (IsActive && string.Equals(activeType, requested, StringComparison.Ordinal))
            return;

        if (!string.Equals(pendingType, requested, StringComparison.Ordinal))
        {
            pendingType = requested;
            pendingSinceTicks = Stopwatch.GetTimestamp();
            return;
        }

        double pendingSeconds = (Stopwatch.GetTimestamp() - pendingSinceTicks) / (double)Stopwatch.Frequency;
        if (pendingSeconds < HoverDelaySeconds)
            return;

        End("switch preview");
        Begin(session, requested);
        ClearPending();
    }

    internal static void OnGameUpdate(global::RainWorldGame game)
    {
        if (!IsActive) return;

        // A preview-owned runtime controller may synchronously spawn a PhysicalObject or hit another
        // universally non-reversible path. Room.AddObject marks the transaction contaminated during
        // that frame; abort as soon as the game update returns to this safe outer boundary.
        if (ownership?.RequiresAbort == true)
        {
            string detail = ownership.ContaminationReason;
            End(string.IsNullOrWhiteSpace(detail) ? "unsafe runtime propagation" : detail);
            ClearPending();
            return;
        }

        // DevUI.Update is not guaranteed to run a final time when devtools are closed. The game
        // update hook is an emergency rollback path so a hover preview cannot leak into gameplay.
        if (!DevToolSessionHub.IsCurrentSessionLive ||
            game == null || activeRoom?.game == null || !ReferenceEquals(activeRoom.game, game))
        {
            End("devtools closed");
            ClearPending();
        }
    }

    internal static void EndForPersistentOperation(string reason)
    {
        End(reason ?? "persistent operation");
        ClearPending();
    }

    internal static void Reset()
    {
        End("runtime reset");
        ClearPending();
        EffectPreviewIntentHub.Reset();
    }

    private static void Begin(EditorSession session, string typeName)
    {
        RoomSettings settings = session?.RoomSettings;
        global::Room room = session?.Room;
        if (settings?.effects == null || room == null || string.IsNullOrWhiteSpace(typeName)) return;

        try
        {
            RoomSettings.RoomEffect.Type type = new(typeName, false);
            RoomSettings.RoomEffect effect = new(type, PreviewAmount, false)
            {
                save = false,
                overWrite = false
            };

            baselineEffectCount = settings.effects.Count;

            // GetEffect/GetEffectAmount return the first matching entry. Front insertion means
            // preview also works when this room already inherits or locally contains the same type,
            // without altering that real effect underneath it.
            settings.effects.Insert(0, effect);

            activeRoom = room;
            activeSettings = settings;
            previewEffect = effect;
            activeType = typeName;
            ownership = new EffectPreviewOwnershipTransaction(room);

            bool blocked = EffectPreviewSafetyRegistry.IsAdvancedPreviewBlocked(typeName, out _);
            if (!blocked)
            {
                try
                {
                    EffectPreviewBootstrapper.Bootstrap(room, settings, effect, ownership);

                    // The primary bootstrap intentionally rejects ambiguous scalar constructor
                    // parameters. If it produced nothing, run the narrower IL-backed scalar
                    // inference layer before giving up on advanced preview.
                    if (ownership.ObjectCount == 0 && !ownership.RequiresAbort)
                    {
                        EffectPreviewExtendedRecipeBootstrap.TryBootstrap(
                            room,
                            effect,
                            ownership);
                    }

                    if (ownership.RequiresAbort)
                    {
                        EffectPreviewRollbackReport report = ownership.Rollback("unsafe bootstrap result");
                        EffectPreviewSafetyRegistry.ObserveRollback(typeName, report);
                        ownership = new EffectPreviewOwnershipTransaction(room);
                        LoadedHookReplayProbe.EnsurePreviewFirst(settings.effects, effect);
                    }
                    else
                    {
                        // Any descendants created later from preview-owned update code now inherit
                        // the same transaction through the generic Room.AddObject choke point.
                        ownership.ActivateRuntimePropagation();
                    }
                }
                catch (Exception error)
                {
                    Plugin.Logger?.LogWarning(
                        "DevTool effect preview bootstrap failed for '" + typeName + "': " + error.Message);
                    EffectPreviewSafetyRegistry.MarkUnsafe(typeName, "bootstrap exception: " + error.Message);

                    EffectPreviewRollbackReport report = ownership.Rollback("bootstrap failure");
                    EffectPreviewSafetyRegistry.ObserveRollback(typeName, report);
                    ownership = new EffectPreviewOwnershipTransaction(room);
                    LoadedHookReplayProbe.EnsurePreviewFirst(settings.effects, effect);
                }
            }
        }
        catch (Exception error)
        {
            try
            {
                EffectPreviewRollbackReport report = ownership?.Rollback("begin failure") ??
                                                     EffectPreviewRollbackReport.Clean;
                EffectPreviewSafetyRegistry.ObserveRollback(typeName, report);
            }
            catch { }

            if (settings?.effects != null && previewEffect != null)
                LoadedHookReplayProbe.RemoveExact(settings.effects, previewEffect);

            Plugin.Logger?.LogWarning("DevTool effect preview begin failed for '" + typeName + "': " + error.Message);
            EffectPreviewSafetyRegistry.MarkUnsafe(typeName, "preview begin exception: " + error.Message);
            ClearActiveState();
        }
    }

    private static void End(string reason)
    {
        RoomSettings settings = activeSettings;
        string typeName = activeType;
        RoomSettings.RoomEffect target = previewEffect;

        if (target == null)
        {
            try
            {
                EffectPreviewRollbackReport report = ownership?.Rollback(reason) ??
                                                     EffectPreviewRollbackReport.Clean;
                EffectPreviewSafetyRegistry.ObserveRollback(typeName, report);
            }
            catch { }
            ClearActiveState();
            return;
        }

        bool removed = false;
        try
        {
            // Runtime objects may need GetEffect/GetEffectAmount during Destroy(), so keep the
            // temporary effect visible until all owned objects and manager fields have rolled back.
            EffectPreviewRollbackReport report = ownership?.Rollback(reason) ??
                                                 EffectPreviewRollbackReport.Clean;

            List<RoomSettings.RoomEffect> effects = settings?.effects;
            if (effects != null)
            {
                for (int i = effects.Count - 1; i >= 0; i--)
                {
                    if (!ReferenceEquals(effects[i], target)) continue;
                    effects.RemoveAt(i);
                    removed = true;
                }

                if (removed && effects.Count != baselineEffectCount)
                {
                    Plugin.Logger?.LogDebug(
                        "DevTool effect preview list changed while active: " + typeName +
                        " (baseline=" + baselineEffectCount + ", now=" + effects.Count +
                        ", reason=" + reason + ")");
                }

                bool exactPreviewStillPresent = false;
                for (int i = 0; i < effects.Count; i++)
                {
                    if (!ReferenceEquals(effects[i], target)) continue;
                    exactPreviewStillPresent = true;
                    break;
                }
                if (exactPreviewStillPresent)
                {
                    EffectPreviewSafetyRegistry.MarkUnsafe(typeName, "temporary RoomEffect survived rollback");
                }
            }

            if (!removed)
            {
                Plugin.Logger?.LogDebug(
                    "DevTool effect preview was already detached before rollback: " + typeName +
                    " (reason=" + reason + ")");
            }

            EffectPreviewSafetyRegistry.ObserveRollback(typeName, report);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool effect preview rollback failed for '" + typeName + "': " + error.Message);
            EffectPreviewSafetyRegistry.MarkUnsafe(typeName, "rollback exception: " + error.Message);
        }
        finally
        {
            ClearActiveState();
        }
    }

    private static void ClearPending()
    {
        pendingType = string.Empty;
        pendingSinceTicks = 0;
    }

    private static void ClearActiveState()
    {
        try { ownership?.DeactivateRuntimePropagation(); }
        catch { }
        activeRoom = null;
        activeSettings = null;
        previewEffect = null;
        ownership = null;
        activeType = string.Empty;
        baselineEffectCount = 0;
    }
}
