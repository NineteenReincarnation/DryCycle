using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.History;

/// <summary>
/// Identity-preserving snapshot migrated from the existing DevUI Ctrl+Z runtime. The same
/// PlacedObject and Data instances are restored so POM/RegionKit objects that retain object
/// references do not break across Undo/Redo.
/// </summary>
public sealed class PlacedObjectState
{
    private readonly RoomSettings settings;
    private readonly PlacedObject target;
    private readonly int index;
    private readonly PlacedObject.Type type;
    private readonly Vector2 pos;
    private readonly bool active;
    private readonly bool deactivatedByWarpFilter;
    private readonly bool save;
    private readonly string[] unrecognizedAttributes;
    private readonly PlacedObject.Data dataReference;
    private readonly string dataSerialized;
    private readonly string fingerprint;

    private PlacedObjectState(RoomSettings settings, PlacedObject target)
    {
        this.settings = settings;
        this.target = target;
        index = settings?.placedObjects?.IndexOf(target) ?? -1;
        type = target?.type;
        pos = target?.pos ?? Vector2.zero;
        active = target?.active ?? false;
        deactivatedByWarpFilter = target?.deactivatedByWarpFilter ?? false;
        save = target?.save ?? false;
        unrecognizedAttributes = Clone(target?.unrecognizedAttributes);
        dataReference = target?.data;
        dataSerialized = target?.data?.ToString() ?? string.Empty;
        fingerprint = BuildFingerprint();
    }

    public PlacedObject Target => target;
    public int Index => index;

    public static PlacedObjectState Capture(RoomSettings settings, PlacedObject target)
    {
        if (settings?.placedObjects == null || target == null) return null;
        return new PlacedObjectState(settings, target);
    }

    public bool SameAs(PlacedObjectState other) =>
        other != null && ReferenceEquals(target, other.target) &&
        string.Equals(fingerprint, other.fingerprint, StringComparison.Ordinal);

    public bool Restore(EditorSession session)
    {
        RoomSettings current = session?.RoomSettings;
        if (!ReferenceEquals(settings, current) || current?.placedObjects == null || target == null)
            return false;

        try
        {
            for (int i = current.placedObjects.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(current.placedObjects[i], target))
                    current.placedObjects.RemoveAt(i);
            }

            if (index >= 0)
            {
                target.type = type;
                target.pos = pos;
                target.active = active;
                target.deactivatedByWarpFilter = deactivatedByWarpFilter;
                target.save = save;
                target.unrecognizedAttributes = Clone(unrecognizedAttributes);
                target.data = dataReference;

                if (dataReference != null)
                {
                    dataReference.owner = target;
                    dataReference.FromString(dataSerialized);
                    try { dataReference.RefreshLiveVisuals(); }
                    catch (Exception error)
                    {
                        Plugin.Logger?.LogWarning("DevTool placed-object live refresh failed: " + error.Message);
                    }
                }

                current.placedObjects.Insert(Mathf.Clamp(index, 0, current.placedObjects.Count), target);
            }

            session.Owner?.activePage?.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool placed-object restore failed: " + error.Message);
            return false;
        }
    }

    private string BuildFingerprint()
    {
        StringBuilder text = new();
        text.Append(target == null ? 0 : RuntimeHelpers.GetHashCode(target)).Append('|')
            .Append(index).Append('|')
            .Append(type?.value ?? string.Empty).Append('|')
            .Append(pos.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(pos.y.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(active ? '1' : '0').Append(deactivatedByWarpFilter ? '1' : '0').Append(save ? '1' : '0').Append('|')
            .Append(dataReference == null ? 0 : RuntimeHelpers.GetHashCode(dataReference)).Append('|')
            .Append(dataSerialized).Append('|');

        if (unrecognizedAttributes != null)
        {
            for (int i = 0; i < unrecognizedAttributes.Length; i++)
                text.Append(unrecognizedAttributes[i] ?? string.Empty).Append('\u001f');
        }
        return text.ToString();
    }

    private static string[] Clone(string[] source)
    {
        if (source == null) return null;
        string[] result = new string[source.Length];
        Array.Copy(source, result, source.Length);
        return result;
    }
}

public sealed class PlacedObjectHistoryEntry : IEditorHistoryEntry
{
    private readonly PlacedObjectState before;
    private readonly PlacedObjectState after;

    public PlacedObjectHistoryEntry(string label, PlacedObjectState before, PlacedObjectState after)
    {
        Label = string.IsNullOrEmpty(label) ? "Edit object" : label;
        this.before = before;
        this.after = after;
    }

    public string Label { get; }
    public bool Undo(EditorSession session) => before?.Restore(session) ?? false;
    public bool Redo(EditorSession session) => after?.Restore(session) ?? false;

    public static bool TryCreate(string label, PlacedObjectState before, PlacedObjectState after, out PlacedObjectHistoryEntry entry)
    {
        entry = null;
        if (before == null || after == null || before.SameAs(after)) return false;
        entry = new PlacedObjectHistoryEntry(label, before, after);
        return true;
    }
}
