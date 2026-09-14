using System;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.History;

/// <summary>
/// Creates the "member did not exist" side of a collection history transaction. The matching
/// present-side snapshot is captured after creation. Keeping absence as an explicit snapshot lets
/// create/undo/redo stay member-scoped instead of falling back to whole RoomSettings serialization.
/// </summary>
internal static class AbsentMemberSnapshots
{
    internal static IEditorStateSnapshot AmbientSound(RoomSettings settings, global::AmbientSound target) =>
        settings == null || target == null ? null : new AmbientSoundAbsentSnapshot(settings, target);

    internal static IEditorStateSnapshot Trigger(RoomSettings settings, global::EventTrigger target) =>
        settings == null || target == null ? null : new TriggerAbsentSnapshot(settings, target);

    private sealed class AmbientSoundAbsentSnapshot : IEditorStateSnapshot
    {
        private readonly RoomSettings settings;
        private readonly global::AmbientSound target;

        internal AmbientSoundAbsentSnapshot(RoomSettings settings, global::AmbientSound target)
        {
            this.settings = settings;
            this.target = target;
        }

        public string Kind => "AmbientSound:" + RuntimeHelpers.GetHashCode(target);
        public string Fingerprint => "0";

        public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
            ReferenceEquals(session?.RoomSettings, settings)
                ? SingleAmbientSoundStateSnapshot.Capture(settings, target)
                : null;

        public bool Restore(EditorSession session)
        {
            if (!ReferenceEquals(session?.RoomSettings, settings) || settings?.ambientSounds == null)
                return false;

            settings.ambientSounds.Remove(target);
            if (session.Owner?.activePage is SoundPage page)
                page.Refresh();
            return true;
        }
    }

    private sealed class TriggerAbsentSnapshot : IEditorStateSnapshot
    {
        private readonly RoomSettings settings;
        private readonly global::EventTrigger target;

        internal TriggerAbsentSnapshot(RoomSettings settings, global::EventTrigger target)
        {
            this.settings = settings;
            this.target = target;
        }

        public string Kind => "EventTrigger:" + RuntimeHelpers.GetHashCode(target);
        public string Fingerprint => "0";

        public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
            ReferenceEquals(session?.RoomSettings, settings)
                ? SingleTriggerStateSnapshot.Capture(settings, target)
                : null;

        public bool Restore(EditorSession session)
        {
            if (!ReferenceEquals(session?.RoomSettings, settings) || settings?.triggers == null)
                return false;

            settings.triggers.Remove(target);
            if (session.Owner?.activePage is TriggersPage page)
                page.Refresh();
            return true;
        }
    }
}
