using System;
using System.Reflection;
using DevInterface;
using DryCycle.DevUI.Controls;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.History;

/// <summary>
/// Compatibility recorder for legacy DevInterface and RegionKit/POM controls. It does not
/// own an Undo stack. It only brackets legacy pointer/text interactions into before/after
/// snapshots and pushes the resulting entry into the session's document history.
///
/// Stable frames never search the complete hidden DevUI tree. Pointer-origin discovery happens on
/// the input edge that can actually start a legacy edit, and the one special external text editor is
/// retained once discovered. This keeps compatibility recording dormant when no legacy interaction
/// is in progress instead of turning it into a second per-frame tree walker.
/// </summary>
public sealed class LegacyTransactionRecorder
{
    private const string WeatherEditorId = "DryCycle_WeatherSpatial";
    private const int MissingWeatherEditorProbeInterval = 60;

    private IEditorStateSnapshot pointerStart;
    private IEditorStateSnapshot externalStart;
    private DevUINode pointerOrigin;
    private DevUINode externalOrigin;
    private DevUINode legacyExternalOrigin;
    private int pointerMask;

    private Page observedWeatherPage;
    private Panel weatherEditorPanel;
    private int weatherEditorProbeCountdown;
    private bool weatherEditorActiveThisUpdate;

    internal bool HasPendingTransaction => pointerStart != null || externalStart != null;

    internal void BeforeLegacyUpdate(EditorSession session)
    {
        if (session?.Owner == null) return;

        int current = CurrentPointerMask();
        bool pointerPressedThisFrame = current != 0 && pointerMask == 0;
        weatherEditorActiveThisUpdate = IsWeatherEditorActive(
            session.Owner.activePage,
            forceProbe: pointerPressedThisFrame);

        if (weatherEditorActiveThisUpdate || EditorInputRouter.WantsMouse)
            return;

        if (pointerPressedThisFrame)
        {
            // One hit-test traversal is justified on the gesture edge because the node under the
            // pointer defines the transaction scope. The result is retained through AfterLegacyUpdate
            // and also serves legacy text-focus detection, avoiding a second traversal that frame.
            pointerOrigin = FindDeepestMouseNode(session.Owner.activePage);
            if (pointerOrigin != null)
                pointerStart = CaptureForNode(session, pointerOrigin);
        }
    }

    internal void AfterLegacyUpdate(EditorSession session)
    {
        if (session?.Owner == null)
        {
            Reset();
            return;
        }

        int current = CurrentPointerMask();
        bool pointerPressedThisFrame = current != 0 && pointerMask == 0;

        if (!weatherEditorActiveThisUpdate)
            SynchronizeExternalEditors(session, pointerPressedThisFrame ? pointerOrigin : null);

        if (pointerStart != null && current == 0)
            CommitPointer(session);

        if (current == 0)
            pointerOrigin = null;
        pointerMask = current;
    }

    internal void Reset()
    {
        pointerStart = null;
        externalStart = null;
        pointerOrigin = null;
        externalOrigin = null;
        legacyExternalOrigin = null;
        pointerMask = CurrentPointerMask();

        observedWeatherPage = null;
        weatherEditorPanel = null;
        weatherEditorProbeCountdown = 0;
        weatherEditorActiveThisUpdate = false;
    }

    private void CommitPointer(EditorSession session)
    {
        IEditorStateSnapshot before = pointerStart;
        pointerStart = null;
        CommitPair(session, before, "Legacy pointer edit");
    }

    private void BeginExternal(EditorSession session, DevUINode origin)
    {
        if (origin == null) return;
        externalStart = CaptureForNode(session, origin);
        externalOrigin = origin;
    }

    private void CommitExternal(EditorSession session, DevUINode origin)
    {
        if (externalStart == null || !ReferenceEquals(externalOrigin, origin)) return;

        IEditorStateSnapshot before = externalStart;
        externalStart = null;
        externalOrigin = null;
        CommitPair(session, before, "Legacy text edit");

        // Clicking away from a text field can commit it in the same mouse gesture that
        // started a pointer transaction. Rebase so the text edit is not recorded twice.
        if (pointerStart != null)
            pointerStart = pointerStart.CaptureCurrent(session);
    }

    private void SynchronizeExternalEditors(EditorSession session, DevUINode pressedOrigin)
    {
        DryCycleTextField focused = DryCycleInputFocus.Focused;
        if (focused != null &&
            (!ReferenceEquals(focused.owner, session.Owner) || !ReferenceEquals(focused.Page, session.Owner.activePage)))
            focused = null;

        if (externalStart != null)
        {
            if (externalOrigin is DryCycleTextField dryCycleField)
            {
                if (!ReferenceEquals(dryCycleField, focused))
                    CommitExternal(session, dryCycleField);
            }
            else if (legacyExternalOrigin != null &&
                     ReferenceEquals(externalOrigin, legacyExternalOrigin) &&
                     !IsLegacyEditorEditing(legacyExternalOrigin))
            {
                DevUINode finished = legacyExternalOrigin;
                legacyExternalOrigin = null;
                CommitExternal(session, finished);
            }
        }

        if (externalStart != null) return;

        if (focused != null)
        {
            BeginExternal(session, focused);
            return;
        }

        if (legacyExternalOrigin != null)
        {
            if (IsLegacyEditorEditing(legacyExternalOrigin))
            {
                BeginExternal(session, legacyExternalOrigin);
                return;
            }
            legacyExternalOrigin = null;
        }

        // The legacy SolarShade text box can only enter editing from an input gesture. Reuse the
        // node already hit-tested before the vanilla update, then inspect its ancestors after vanilla
        // had a chance to flip the private _editing flag. Stable frames do no tree search at all.
        DevUINode legacy = FindEditingLegacyEditorAncestor(pressedOrigin);
        if (legacy == null) return;

        legacyExternalOrigin = legacy;
        BeginExternal(session, legacy);
    }

    private bool IsWeatherEditorActive(Page page, bool forceProbe)
    {
        if (!ReferenceEquals(observedWeatherPage, page))
        {
            observedWeatherPage = page;
            weatherEditorPanel = null;
            weatherEditorProbeCountdown = 0;
        }

        if (weatherEditorPanel != null)
        {
            // A retained panel belongs to one concrete DevUI/page lifetime. If an owner/page switch
            // invalidates that relationship, drop it and probe again instead of keeping a stale node.
            if (ReferenceEquals(weatherEditorPanel.owner?.activePage, page))
                return !weatherEditorPanel.collapsed;

            weatherEditorPanel = null;
            weatherEditorProbeCountdown = 0;
        }

        if (!forceProbe && weatherEditorProbeCountdown > 0)
        {
            weatherEditorProbeCountdown--;
            return false;
        }

        weatherEditorProbeCountdown = MissingWeatherEditorProbeInterval;
        DevUINode found = FindNode(page, WeatherEditorId);
        weatherEditorPanel = found as Panel;
        return weatherEditorPanel != null && !weatherEditorPanel.collapsed;
    }

    private static DevUINode FindEditingLegacyEditorAncestor(DevUINode node)
    {
        DevUINode current = node;
        while (current != null)
        {
            if (IsLegacyEditorEditing(current))
                return current;
            current = current.parentNode;
        }
        return null;
    }

    private static IEditorStateSnapshot CaptureForNode(EditorSession session, DevUINode origin)
    {
        // Keep specialized member capture separate from the generic factory so the latter remains a
        // conservative compatibility API. If no exact member scope is known, fall back unchanged.
        return LegacyMemberSnapshotFactory.CaptureForNode(session, origin) ??
               LegacySnapshotFactory.CaptureForNode(session, origin);
    }

    private static void CommitPair(EditorSession session, IEditorStateSnapshot before, string label)
    {
        if (before == null || session == null) return;
        IEditorStateSnapshot after = before.CaptureCurrent(session);
        if (SnapshotHistoryEntry.TryCreate(label, before, after, out SnapshotHistoryEntry entry))
            session.History.Push(entry);
    }

    private static int CurrentPointerMask()
    {
        int result = 0;
        if (global::UnityEngine.Input.GetMouseButton(0)) result |= 1;
        if (global::UnityEngine.Input.GetMouseButton(1)) result |= 2;
        return result;
    }

    private static DevUINode FindNode(DevUINode root, string id)
    {
        if (root == null) return null;
        if (string.Equals(root.IDstring, id, StringComparison.Ordinal)) return root;
        for (int i = 0; i < root.subNodes.Count; i++)
        {
            DevUINode found = FindNode(root.subNodes[i], id);
            if (found != null) return found;
        }
        return null;
    }

    private static DevUINode FindDeepestMouseNode(DevUINode root)
    {
        if (root == null) return null;
        for (int i = root.subNodes.Count - 1; i >= 0; i--)
        {
            DevUINode found = FindDeepestMouseNode(root.subNodes[i]);
            if (found != null) return found;
        }
        return IsMouseOver(root) ? root : null;
    }

    private static bool IsMouseOver(DevUINode node)
    {
        if (node is Handle handle) return handle.MouseOver;
        if (node is RectangularDevUINode rectangular) return rectangular.MouseOver;
        return false;
    }

    private static bool IsLegacyEditorEditing(DevUINode node)
    {
        if (node == null) return false;
        Type type = node.GetType();
        string fullName = type.FullName ?? string.Empty;
        if (!fullName.EndsWith("SolarShadeZoneRepresentation+ZoneTextInput", StringComparison.Ordinal))
            return false;

        try
        {
            FieldInfo field = type.GetField("_editing", BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null && field.FieldType == typeof(bool) && (bool)field.GetValue(node);
        }
        catch
        {
            return false;
        }
    }
}
