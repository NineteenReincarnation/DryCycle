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
/// </summary>
public sealed class LegacyTransactionRecorder
{
    private const string WeatherEditorId = "DryCycle_WeatherSpatial";

    private IEditorStateSnapshot pointerStart;
    private IEditorStateSnapshot externalStart;
    private DevUINode externalOrigin;
    private DevUINode legacyExternalOrigin;
    private int pointerMask;

    internal bool HasPendingTransaction => pointerStart != null || externalStart != null;

    internal void BeforeLegacyUpdate(EditorSession session)
    {
        if (session?.Owner == null || IsWeatherEditorActive(session.Owner.activePage)) return;
        if (EditorInputRouter.WantsMouse) return;

        int current = CurrentPointerMask();
        if (current != 0 && pointerMask == 0)
            pointerStart = LegacySnapshotFactory.CaptureForPointer(session);
    }

    internal void AfterLegacyUpdate(EditorSession session)
    {
        if (session?.Owner == null)
        {
            Reset();
            return;
        }

        if (!IsWeatherEditorActive(session.Owner.activePage))
            SynchronizeExternalEditors(session);

        int current = CurrentPointerMask();
        if (pointerStart != null && current == 0)
            CommitPointer(session);

        pointerMask = current;
    }

    internal void Reset()
    {
        pointerStart = null;
        externalStart = null;
        externalOrigin = null;
        legacyExternalOrigin = null;
        pointerMask = CurrentPointerMask();
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
        externalStart = LegacySnapshotFactory.CaptureForNode(session, origin);
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

    private void SynchronizeExternalEditors(EditorSession session)
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

        DevUINode legacy = legacyExternalOrigin;
        if (legacy == null || !IsLegacyEditorEditing(legacy))
        {
            legacy = FindDeepestMouseNode(session.Owner.activePage);
            if (legacy == null || !IsLegacyEditorEditing(legacy))
            {
                legacyExternalOrigin = null;
                return;
            }
            legacyExternalOrigin = legacy;
        }

        BeginExternal(session, legacy);
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

    private static bool IsWeatherEditorActive(Page page)
    {
        DevUINode editor = FindNode(page, WeatherEditorId);
        return editor is Panel panel && !panel.collapsed;
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
