using System;
using System.Collections.Concurrent;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.Sound;
using DryCycle.DevUI.DevTool.Triggers;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Gizmos;

public enum NativeGizmoTargetKind
{
    ObjectPosition = 0,
    SoundPosition = 1,
    SoundRadius = 2,
    SoundDirection = 3,
    TriggerPosition = 4,
    TriggerRadius = 5
}

public enum NativeGizmoCommandKind
{
    Select = 0,
    Begin = 1,
    Update = 2,
    Commit = 3,
    Cancel = 4
}

/// <summary>
/// Detached command emitted by scene-space frontends. Coordinates are Rain World world-space values,
/// never ImGui screen coordinates. Begin/Update/Commit form one history transaction regardless of
/// how many preview updates occur during the drag.
/// </summary>
public readonly struct NativeGizmoCommand
{
    public NativeGizmoCommand(
        NativeGizmoCommandKind kind,
        NativeGizmoTargetKind target,
        int index,
        float x = 0f,
        float y = 0f)
    {
        Kind = kind;
        Target = target;
        Index = index;
        X = x;
        Y = y;
    }

    public NativeGizmoCommandKind Kind { get; }
    public NativeGizmoTargetKind Target { get; }
    public int Index { get; }
    public float X { get; }
    public float Y { get; }
}

public static class NativeGizmoCommandQueue
{
    private static readonly ConcurrentQueue<NativeGizmoCommand> queue = new();

    public static void Enqueue(NativeGizmoCommand command) => queue.Enqueue(command);

    internal static void Process(EditorSession session)
    {
        while (queue.TryDequeue(out NativeGizmoCommand command))
        {
            try
            {
                switch (command.Kind)
                {
                    case NativeGizmoCommandKind.Select:
                        Select(session, command);
                        break;
                    case NativeGizmoCommandKind.Begin:
                        Begin(session, command);
                        break;
                    case NativeGizmoCommandKind.Update:
                        Update(session, command);
                        break;
                    case NativeGizmoCommandKind.Commit:
                        EditorContinuousTransactionHub.Commit(session, Key(command));
                        break;
                    case NativeGizmoCommandKind.Cancel:
                        EditorContinuousTransactionHub.Cancel(session, Key(command));
                        MarkChanged(session, command.Target, command.Index);
                        break;
                }
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool native gizmo command failed: " + error.Message);
            }
        }
    }

    internal static void Clear() =>
        Drain();

    private static void Drain()
    {
        while (queue.TryDequeue(out _)) { }
    }

    private static void Select(EditorSession session, NativeGizmoCommand command)
    {
        if (session == null) return;
        switch (command.Target)
        {
            case NativeGizmoTargetKind.ObjectPosition:
            {
                PlacedObject target = ObjectAt(session, command.Index);
                if (target != null) session.Selection.SelectOnly(target);
                break;
            }
            case NativeGizmoTargetKind.SoundPosition:
            case NativeGizmoTargetKind.SoundRadius:
            case NativeGizmoTargetKind.SoundDirection:
                if (SoundAt(session, command.Index) != null)
                    SoundEditorStateHub.Get(session)?.SetSelectedIndex(command.Index);
                break;
            case NativeGizmoTargetKind.TriggerPosition:
            case NativeGizmoTargetKind.TriggerRadius:
                if (TriggerAt(session, command.Index) != null)
                    TriggerEditorStateHub.Get(session)?.SetSelectedIndex(command.Index);
                break;
        }
    }

    private static void Begin(EditorSession session, NativeGizmoCommand command)
    {
        if (session == null) return;
        IEditorStateSnapshot before = command.Target switch
        {
            NativeGizmoTargetKind.ObjectPosition =>
                SinglePlacedObjectStateSnapshot.Capture(session.RoomSettings, ObjectAt(session, command.Index)),
            NativeGizmoTargetKind.SoundPosition or
            NativeGizmoTargetKind.SoundRadius or
            NativeGizmoTargetKind.SoundDirection =>
                SingleAmbientSoundStateSnapshot.Capture(session.RoomSettings, SoundAt(session, command.Index)),
            NativeGizmoTargetKind.TriggerPosition or
            NativeGizmoTargetKind.TriggerRadius =>
                SingleTriggerStateSnapshot.Capture(session.RoomSettings, TriggerAt(session, command.Index)),
            _ => null
        };

        if (before != null)
            EditorContinuousTransactionHub.Begin(session, Key(command), Label(command.Target), before);
    }

    private static void Update(EditorSession session, NativeGizmoCommand command)
    {
        if (session == null || !EditorContinuousTransactionHub.IsActive(session, Key(command)))
            return;

        bool changed = command.Target switch
        {
            NativeGizmoTargetKind.ObjectPosition => SetObjectPosition(session, command.Index, command.X, command.Y),
            NativeGizmoTargetKind.SoundPosition => SetSoundPosition(session, command.Index, command.X, command.Y),
            NativeGizmoTargetKind.SoundRadius => SetSoundRadius(session, command.Index, command.X),
            NativeGizmoTargetKind.SoundDirection => SetSoundDirection(session, command.Index, command.X, command.Y),
            NativeGizmoTargetKind.TriggerPosition => SetTriggerPosition(session, command.Index, command.X, command.Y),
            NativeGizmoTargetKind.TriggerRadius => SetTriggerRadius(session, command.Index, command.X),
            _ => false
        };

        if (changed)
            MarkChanged(session, command.Target, command.Index);
    }

    private static bool SetObjectPosition(EditorSession session, int index, float x, float y)
    {
        PlacedObject target = ObjectAt(session, index);
        if (target == null) return false;
        Vector2 next = new(x, y);
        if ((target.pos - next).sqrMagnitude <= 0.000001f) return false;
        target.pos = next;
        return true;
    }

    private static bool SetSoundPosition(EditorSession session, int index, float x, float y)
    {
        if (SoundAt(session, index) is not SpotSound sound || sound.inherited) return false;
        Vector2 next = new(x, y);
        if ((sound.pos - next).sqrMagnitude <= 0.000001f) return false;
        sound.pos = next;
        return true;
    }

    private static bool SetSoundRadius(EditorSession session, int index, float radius)
    {
        if (SoundAt(session, index) is not SpotSound sound || sound.inherited) return false;
        float next = Mathf.Max(0f, radius);
        if (Mathf.Approximately(sound.rad, next)) return false;
        Vector2 direction = sound.radHandlePosition.sqrMagnitude > 0.0001f
            ? sound.radHandlePosition.normalized
            : Vector2.right;
        sound.rad = next;
        sound.radHandlePosition = direction * next;
        return true;
    }

    private static bool SetSoundDirection(EditorSession session, int index, float x, float y)
    {
        if (SoundAt(session, index) is not DirectionalSound sound || sound.inherited) return false;
        Vector2 raw = new(x, y);
        Vector2 next = raw.sqrMagnitude > 0.0001f ? raw.normalized : Vector2.down;
        if ((sound.direction - next).sqrMagnitude <= 0.000001f) return false;
        sound.direction = next;
        return true;
    }

    private static bool SetTriggerPosition(EditorSession session, int index, float x, float y)
    {
        if (TriggerAt(session, index) is not SpotTrigger trigger) return false;
        Vector2 next = new(x, y);
        if ((trigger.pos - next).sqrMagnitude <= 0.000001f) return false;
        trigger.pos = next;
        return true;
    }

    private static bool SetTriggerRadius(EditorSession session, int index, float radius)
    {
        if (TriggerAt(session, index) is not SpotTrigger trigger) return false;
        float next = Mathf.Max(0f, radius);
        if (Mathf.Approximately(trigger.rad, next)) return false;
        Vector2 direction = trigger.radHandlePosition.sqrMagnitude > 0.0001f
            ? trigger.radHandlePosition.normalized
            : Vector2.right;
        trigger.rad = next;
        trigger.radHandlePosition = direction * next;
        return true;
    }

    private static void MarkChanged(EditorSession session, NativeGizmoTargetKind target, int index)
    {
        switch (target)
        {
            case NativeGizmoTargetKind.ObjectPosition:
                EditorRevisionHub.Mark(session, EditorRevisionKind.Objects);
                ObjectPresentationChangeHintHub.MarkMember(session, ObjectAt(session, index));
                break;
            case NativeGizmoTargetKind.SoundPosition:
            case NativeGizmoTargetKind.SoundRadius:
            case NativeGizmoTargetKind.SoundDirection:
                EditorRevisionHub.Mark(session, EditorRevisionKind.Sound);
                SoundPresentationChangeHintHub.MarkMember(session, index);
                break;
            case NativeGizmoTargetKind.TriggerPosition:
            case NativeGizmoTargetKind.TriggerRadius:
                EditorRevisionHub.Mark(session, EditorRevisionKind.Triggers);
                TriggerPresentationChangeHintHub.MarkMember(session, index);
                break;
        }
    }

    private static PlacedObject ObjectAt(EditorSession session, int index) =>
        session?.RoomSettings?.placedObjects != null && index >= 0 && index < session.RoomSettings.placedObjects.Count
            ? session.RoomSettings.placedObjects[index]
            : null;

    private static AmbientSound SoundAt(EditorSession session, int index) =>
        session?.RoomSettings?.ambientSounds != null && index >= 0 && index < session.RoomSettings.ambientSounds.Count
            ? session.RoomSettings.ambientSounds[index]
            : null;

    private static EventTrigger TriggerAt(EditorSession session, int index) =>
        session?.RoomSettings?.triggers != null && index >= 0 && index < session.RoomSettings.triggers.Count
            ? session.RoomSettings.triggers[index]
            : null;

    private static string Key(NativeGizmoCommand command) =>
        "NativeGizmo:" + (int)command.Target + ":" + command.Index;

    private static string Label(NativeGizmoTargetKind target) => target switch
    {
        NativeGizmoTargetKind.ObjectPosition => "Move object",
        NativeGizmoTargetKind.SoundPosition => "Move sound",
        NativeGizmoTargetKind.SoundRadius => "Change sound radius",
        NativeGizmoTargetKind.SoundDirection => "Change sound direction",
        NativeGizmoTargetKind.TriggerPosition => "Move trigger",
        NativeGizmoTargetKind.TriggerRadius => "Change trigger radius",
        _ => "Gizmo edit"
    };
}
