using System;
using System.Runtime.CompilerServices;
using DevInterface;

namespace DryCycle.DevUI.DevTool.History;

/// <summary>
/// Resolves legacy controls whose complete edit domain is one member of a RoomSettings collection.
/// These snapshots sit in front of the document-level compatibility fallback: known Sound/Trigger
/// controls get O(1)-sized transactions, while unknown third-party controls still retain the safe
/// whole-document snapshot path.
/// </summary>
internal static class LegacyMemberSnapshotFactory
{
    internal static IEditorStateSnapshot CaptureForNode(EditorSession session, DevUINode origin)
    {
        if (session?.RoomSettings == null || origin == null)
            return null;

        AmbientSoundPanel soundPanel = FindAncestor<AmbientSoundPanel>(origin);
        if (soundPanel?.sound != null)
            return SingleAmbientSoundStateSnapshot.Capture(session.RoomSettings, soundPanel.sound);

        TriggerPanel triggerPanel = FindAncestor<TriggerPanel>(origin);
        if (triggerPanel?.trigger != null)
            return SingleTriggerStateSnapshot.Capture(session.RoomSettings, triggerPanel.trigger);

        return null;
    }

    private static T FindAncestor<T>(DevUINode node) where T : DevUINode
    {
        DevUINode current = node;
        while (current != null)
        {
            if (current is T typed)
                return typed;
            current = current.parentNode;
        }
        return null;
    }
}

/// <summary>
/// Lossless transaction for one AmbientSound. Presence and list position are part of the snapshot,
/// so dragging an AmbientSoundPanel into vanilla's TrashBin remains undoable without serializing the
/// complete RoomSettings document.
/// </summary>
internal sealed class SingleAmbientSoundStateSnapshot : IEditorStateSnapshot
{
    private static readonly string[] SoundSeparator = { "><" };

    private readonly RoomSettings settings;
    private readonly AmbientSound target;
    private readonly int index;
    private readonly bool present;
    private readonly string serialized;
    private readonly bool inherited;
    private readonly bool overWrite;

    private SingleAmbientSoundStateSnapshot(
        RoomSettings settings,
        AmbientSound target,
        int index,
        bool present,
        string serialized,
        bool inherited,
        bool overWrite)
    {
        this.settings = settings;
        this.target = target;
        this.index = index;
        this.present = present;
        this.serialized = serialized ?? string.Empty;
        this.inherited = inherited;
        this.overWrite = overWrite;

        Fingerprint = present
            ? "1|" + index + "|" + (inherited ? "1" : "0") + "|" + (overWrite ? "1" : "0") + "|" + this.serialized
            : "0";
    }

    public string Kind =>
        "AmbientSound:" + (target == null ? 0 : RuntimeHelpers.GetHashCode(target));

    public string Fingerprint { get; }

    internal static SingleAmbientSoundStateSnapshot Capture(RoomSettings settings, AmbientSound target)
    {
        if (settings?.ambientSounds == null || target == null)
            return null;

        int index = settings.ambientSounds.IndexOf(target);
        bool present = index >= 0;
        string serialized = present ? target.ToString() : string.Empty;
        return new SingleAmbientSoundStateSnapshot(
            settings,
            target,
            index,
            present,
            serialized,
            target.inherited,
            target.overWrite);
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        ReferenceEquals(session?.RoomSettings, settings)
            ? Capture(settings, target)
            : null;

    public bool Restore(EditorSession session)
    {
        if (!ReferenceEquals(session?.RoomSettings, settings) || settings?.ambientSounds == null || target == null)
            return false;

        try
        {
            if (!present)
            {
                settings.ambientSounds.Remove(target);
            }
            else
            {
                int currentIndex = settings.ambientSounds.IndexOf(target);
                if (currentIndex >= 0)
                    settings.ambientSounds.RemoveAt(currentIndex);

                int insertIndex = Math.Max(0, Math.Min(index, settings.ambientSounds.Count));
                settings.ambientSounds.Insert(insertIndex, target);

                target.FromString(serialized.Split(SoundSeparator, StringSplitOptions.None));
                target.inherited = inherited;
                target.overWrite = overWrite;
            }

            if (session.Owner?.activePage is SoundPage soundPage)
                soundPage.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool single-sound restore failed: " + error.Message);
            return false;
        }
    }
}

/// <summary>
/// Lossless transaction for one EventTrigger. The serialized trigger format already preserves
/// subtype-specific data (Spot position/radius, SeeCreature type, event data and unknown attrs).
/// Presence/list position are captured separately so vanilla TrashBin delete/undo remains exact.
/// </summary>
internal sealed class SingleTriggerStateSnapshot : IEditorStateSnapshot
{
    private static readonly string[] TriggerSeparator = { "<tA>" };

    private readonly RoomSettings settings;
    private readonly EventTrigger target;
    private readonly int index;
    private readonly bool present;
    private readonly string serialized;

    private SingleTriggerStateSnapshot(
        RoomSettings settings,
        EventTrigger target,
        int index,
        bool present,
        string serialized)
    {
        this.settings = settings;
        this.target = target;
        this.index = index;
        this.present = present;
        this.serialized = serialized ?? string.Empty;
        Fingerprint = present ? "1|" + index + "|" + this.serialized : "0";
    }

    public string Kind =>
        "EventTrigger:" + (target == null ? 0 : RuntimeHelpers.GetHashCode(target));

    public string Fingerprint { get; }

    internal static SingleTriggerStateSnapshot Capture(RoomSettings settings, EventTrigger target)
    {
        if (settings?.triggers == null || target == null)
            return null;

        int index = settings.triggers.IndexOf(target);
        bool present = index >= 0;
        return new SingleTriggerStateSnapshot(
            settings,
            target,
            index,
            present,
            present ? target.ToString() : string.Empty);
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        ReferenceEquals(session?.RoomSettings, settings)
            ? Capture(settings, target)
            : null;

    public bool Restore(EditorSession session)
    {
        if (!ReferenceEquals(session?.RoomSettings, settings) || settings?.triggers == null || target == null)
            return false;

        try
        {
            if (!present)
            {
                settings.triggers.Remove(target);
            }
            else
            {
                int currentIndex = settings.triggers.IndexOf(target);
                if (currentIndex >= 0)
                    settings.triggers.RemoveAt(currentIndex);

                int insertIndex = Math.Max(0, Math.Min(index, settings.triggers.Count));
                settings.triggers.Insert(insertIndex, target);

                // EventTrigger.FromString only creates an event when the serialized value is not
                // NONE. Clear first so undoing an "add event" operation can faithfully restore NONE.
                target.tEvent = null;
                target.FromString(serialized.Split(TriggerSeparator, StringSplitOptions.None));
            }

            if (session.Owner?.activePage is TriggersPage triggersPage)
                triggersPage.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool single-trigger restore failed: " + error.Message);
            return false;
        }
    }
}
