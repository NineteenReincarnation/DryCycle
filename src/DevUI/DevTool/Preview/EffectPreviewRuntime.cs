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
            // After a real click, keep this row suppressed until the pointer actually leaves it.
            // This prevents the freshly-created real effect from immediately receiving another
            // temporary preview overlay just because the mouse is still resting on the same row.
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
/// Stage two may bootstrap load-time runtime objects through the generic HookGen A/B probe and the
/// exact-name constructor convention. Every stage-two artifact is owned by one transaction and is
/// rolled back by identity when hover ends.
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
    private static int baselineUpdateCount = -1;
    private static int baselineDrawableCount = -1;

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
                // RoomSettings.Save honors this field, so even an unexpected save path has a
                // persistence barrier in addition to explicit rollback before editor Save.
                save = false,
                overWrite = false
            };

            baselineEffectCount = settings.effects.Count;
            baselineUpdateCount = room.updateList?.Count ?? -1;
            baselineDrawableCount = room.drawableObjects?.Count ?? -1;

            // GetEffect/GetEffectAmount return the first matching entry. Front insertion means
            // preview also works when this room already inherits or locally contains the same type,
            // without altering that real effect underneath it.
            settings.effects.Insert(0, effect);

            activeRoom = room;
            activeSettings = settings;
            previewEffect = effect;
            activeType = typeName;
            ownership = new EffectPreviewOwnershipTransaction(room);

            try
            {
                EffectPreviewBootstrapper.Bootstrap(room, settings, effect, ownership);
            }
            catch (Exception error)
            {
                // Bootstrap is optional. A fault here must degrade to stage-one state reading, not
                // cancel the hover preview or leave partially committed runtime objects behind.
                Plugin.Logger?.LogWarning(
                    "DevTool effect preview bootstrap failed for '" + typeName + "': " + error.Message);
                ownership.Rollback("bootstrap failure");
                ownership = new EffectPreviewOwnershipTransaction(room);
                LoadedHookReplayProbe.EnsurePreviewFirst(settings.effects, effect);
            }
        }
        catch (Exception error)
        {
            try { ownership?.Rollback("begin failure"); }
            catch { }
            if (settings?.effects != null && previewEffect != null)
                LoadedHookReplayProbe.RemoveExact(settings.effects, previewEffect);
            Plugin.Logger?.LogWarning("DevTool effect preview begin failed for '" + typeName + "': " + error.Message);
            ClearActiveState();
        }
    }

    private static void End(string reason)
    {
        RoomSettings settings = activeSettings;
        global::Room room = activeRoom;
        string typeName = activeType;
        RoomSettings.RoomEffect target = previewEffect;

        if (target == null)
        {
            try { ownership?.Rollback(reason); }
            catch { }
            ClearActiveState();
            return;
        }

        bool removed = false;
        try
        {
            // Runtime objects may need GetEffect/GetEffectAmount during Destroy(), so keep the
            // temporary effect visible until all owned objects and manager fields have rolled back.
            ownership?.Rollback(reason);

            List<RoomSettings.RoomEffect> effects = settings?.effects;
            if (effects != null)
            {
                for (int i = effects.Count - 1; i >= 0; i--)
                {
                    if (!ReferenceEquals(effects[i], target)) continue;
                    effects.RemoveAt(i);
                    removed = true;
                }

                // Never repair by type name. A third-party mod is allowed to mutate the list while
                // preview is active; deleting by type could destroy a real document effect.
                if (removed && effects.Count != baselineEffectCount)
                {
                    Plugin.Logger?.LogDebug(
                        "DevTool effect preview list changed while active: " + typeName +
                        " (baseline=" + baselineEffectCount + ", now=" + effects.Count +
                        ", reason=" + reason + ")");
                }
            }

            if (!removed)
            {
                Plugin.Logger?.LogDebug(
                    "DevTool effect preview was already detached before rollback: " + typeName +
                    " (reason=" + reason + ")");
            }

            // Counts are checked only after the ownership transaction has rolled back. A mismatch
            // now is a real leak/surprising third-party mutation rather than an expected preview
            // object that is still alive.
            int updateNow = room?.updateList?.Count ?? -1;
            int drawableNow = room?.drawableObjects?.Count ?? -1;
            if ((baselineUpdateCount >= 0 && updateNow >= 0 && updateNow != baselineUpdateCount) ||
                (baselineDrawableCount >= 0 && drawableNow >= 0 && drawableNow != baselineDrawableCount))
            {
                Plugin.Logger?.LogWarning(
                    "DevTool effect preview rollback leak check changed for '" + typeName +
                    "': update " + baselineUpdateCount + "->" + updateNow +
                    ", drawable " + baselineDrawableCount + "->" + drawableNow + ".");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool effect preview rollback failed for '" + typeName + "': " + error.Message);
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
        activeRoom = null;
        activeSettings = null;
        previewEffect = null;
        ownership = null;
        activeType = string.Empty;
        baselineEffectCount = 0;
        baselineUpdateCount = -1;
        baselineDrawableCount = -1;
    }
}
