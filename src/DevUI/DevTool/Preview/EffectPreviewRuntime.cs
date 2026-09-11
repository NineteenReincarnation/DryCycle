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
/// Universal first-stage RoomEffect preview runtime.
///
/// This code deliberately knows nothing about RegionKit, POM, assembly names or third-party
/// namespaces. A preview is represented as an ordinary RoomSettings.RoomEffect inserted at the
/// front of RoomSettings.effects with save=false. Any mod that observes the normal Rain World
/// effect list, GetEffect or GetEffectAmount therefore sees the preview automatically.
///
/// Effects whose visuals are created only from Room.Loaded are intentionally not re-bootstraped
/// here. Re-running Room.Loaded without an ownership journal is unsafe. The later transaction /
/// A-B probe layer can build on this reversible overlay without changing the frontend contract.
/// </summary>
internal static class EffectPreviewRuntime
{
    internal const double IntentTimeoutSeconds = 0.35;
    private const double HoverDelaySeconds = 0.18;
    private const float PreviewAmount = 0.50f;

    private static global::Room activeRoom;
    private static RoomSettings activeSettings;
    private static RoomSettings.RoomEffect previewEffect;
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
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool effect preview begin failed for '" + typeName + "': " + error.Message);
            ClearActiveState();
        }
    }

    private static void End(string reason)
    {
        if (previewEffect == null)
        {
            ClearActiveState();
            return;
        }

        RoomSettings settings = activeSettings;
        global::Room room = activeRoom;
        string typeName = activeType;
        RoomSettings.RoomEffect target = previewEffect;
        bool removed = false;

        try
        {
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

            // Stage-one leak telemetry only. These counts are not used to delete anything because
            // a normal room may legitimately create particles during the hover interval. The later
            // transaction journal will attribute ownership at engine choke points instead.
            int updateNow = room?.updateList?.Count ?? -1;
            int drawableNow = room?.drawableObjects?.Count ?? -1;
            if ((baselineUpdateCount >= 0 && updateNow >= 0 && updateNow != baselineUpdateCount) ||
                (baselineDrawableCount >= 0 && drawableNow >= 0 && drawableNow != baselineDrawableCount))
            {
                Plugin.Logger?.LogDebug(
                    "DevTool effect preview observed runtime list movement for '" + typeName +
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
        activeType = string.Empty;
        baselineEffectCount = 0;
        baselineUpdateCount = -1;
        baselineDrawableCount = -1;
    }
}
