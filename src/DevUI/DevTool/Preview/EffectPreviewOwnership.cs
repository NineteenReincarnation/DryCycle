using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DryCycle.DevUI.DevTool.Preview;

/// <summary>
/// Intercepts Room.AddObject only while a bootstrap probe is executing. Probe-created objects are
/// captured before Rain World mutates update/drawable/camera lists. Outside the narrow synchronous
/// probe scope this hook is transparent and immediately delegates to the normal detour chain.
/// </summary>
internal static class EffectPreviewObjectCapture
{
    private static bool enabled;
    private static CaptureScope activeScope;

    internal static void Enable()
    {
        if (enabled) return;
        On.Room.AddObject += Room_AddObject;
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        activeScope = null;
        On.Room.AddObject -= Room_AddObject;
        enabled = false;
    }

    internal static CaptureResult Capture(global::Room room, Action action)
    {
        if (room == null || action == null)
            return new CaptureResult(false, Array.Empty<UpdatableAndDeletable>(), null);
        if (activeScope != null)
            return new CaptureResult(false, Array.Empty<UpdatableAndDeletable>(),
                new InvalidOperationException("Nested effect preview capture is not supported."));

        CaptureScope scope = new(room);
        activeScope = scope;
        Exception failure = null;
        try
        {
            action();
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            activeScope = null;
        }

        return new CaptureResult(failure == null, scope.Objects.ToArray(), failure);
    }

    private static void Room_AddObject(
        On.Room.orig_AddObject orig,
        global::Room self,
        UpdatableAndDeletable obj)
    {
        CaptureScope scope = activeScope;
        if (scope != null && ReferenceEquals(scope.Room, self))
        {
            if (obj != null)
                scope.Objects.Add(obj);
            return;
        }

        orig(self, obj);
    }

    private sealed class CaptureScope
    {
        internal CaptureScope(global::Room room)
        {
            Room = room;
        }

        internal global::Room Room { get; }
        internal List<UpdatableAndDeletable> Objects { get; } = new();
    }

    internal readonly struct CaptureResult
    {
        internal CaptureResult(bool success, UpdatableAndDeletable[] objects, Exception error)
        {
            Success = success;
            Objects = objects ?? Array.Empty<UpdatableAndDeletable>();
            Error = error;
        }

        internal bool Success { get; }
        internal UpdatableAndDeletable[] Objects { get; }
        internal Exception Error { get; }
    }
}

/// <summary>
/// Owns every runtime artifact that was deliberately committed for one hover preview. Rollback is
/// identity-based: real room objects of the same type are never removed. Camera sprite leasers are
/// tracked separately because Room.CleanOutObjectNotInThisRoom removes drawables from the room but
/// leaves their already-created camera leasers alive until a later camera cleanup frame.
/// </summary>
internal sealed class EffectPreviewOwnershipTransaction
{
    private readonly global::Room room;
    private readonly List<UpdatableAndDeletable> ownedObjects = new();
    private readonly HashSet<IDrawable> ownedDrawables =
        new(ReferenceEqualityComparer<IDrawable>.Instance);
    private readonly List<FieldMutation> fieldMutations = new();

    internal EffectPreviewOwnershipTransaction(global::Room room)
    {
        this.room = room;
    }

    internal int ObjectCount => ownedObjects.Count;
    internal int FieldMutationCount => fieldMutations.Count;

    internal bool CommitObject(UpdatableAndDeletable obj)
    {
        if (room == null || obj == null) return false;

        HashSet<IDrawable> before = SnapshotDrawables(room);
        try
        {
            room.AddObject(obj);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool effect preview object commit failed for '" + obj.GetType().FullName + "': " + error.Message);
            DisposeCapturedObject(obj, room);
            return false;
        }

        bool attached = ContainsReference(room.updateList, obj) || ReferenceEquals(obj.room, room);
        if (!attached)
        {
            DisposeCapturedObject(obj, room);
            return false;
        }

        ownedObjects.Add(obj);
        if (room.drawableObjects != null)
        {
            for (int i = 0; i < room.drawableObjects.Count; i++)
            {
                IDrawable drawable = room.drawableObjects[i];
                if (drawable != null && !before.Contains(drawable))
                    ownedDrawables.Add(drawable);
            }
        }
        return true;
    }

    internal bool ApplyFieldMutation(FieldInfo field, object originalValue, object previewValue)
    {
        if (room == null || field == null || field.IsStatic || field.IsInitOnly || field.IsLiteral)
            return false;

        object current;
        try { current = field.GetValue(room); }
        catch { return false; }

        // Do not overwrite a change made by normal game/editor code between the probe and commit.
        if (!SameValue(current, originalValue))
            return false;

        try
        {
            field.SetValue(room, previewValue);
            fieldMutations.Add(new FieldMutation(field, originalValue, previewValue));
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogDebug(
                "DevTool effect preview could not apply room field '" + field.Name + "': " + error.Message);
            return false;
        }
    }

    internal void Rollback(string reason)
    {
        if (room == null) return;

        // Keep room manager/back-reference fields intact while owned objects tear themselves down.
        // Some load-time scene objects use those fields from Destroy(), so restoring first can make
        // their own cleanup path observe an impossible half-rolled-back room.
        CleanCameraLeasers();

        for (int i = ownedObjects.Count - 1; i >= 0; i--)
        {
            UpdatableAndDeletable obj = ownedObjects[i];
            if (obj == null) continue;
            try
            {
                obj.Destroy();
                if (ContainsReference(room.updateList, obj) || ReferenceEquals(obj.room, room))
                    room.CleanOutObjectNotInThisRoom(obj);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning(
                    "DevTool effect preview object rollback failed for '" + obj.GetType().FullName +
                    "' (" + reason + "): " + error.Message);
                try
                {
                    if (ReferenceEquals(obj.room, room)) obj.RemoveFromRoom();
                }
                catch { }
            }
        }
        ownedObjects.Clear();

        for (int i = fieldMutations.Count - 1; i >= 0; i--)
        {
            FieldMutation mutation = fieldMutations[i];
            try
            {
                object current = mutation.Field.GetValue(room);
                // A preview must never clobber a legitimate value written after bootstrap. Restore
                // only while the room still contains exactly the value that this transaction set.
                if (SameValue(current, mutation.PreviewValue))
                    mutation.Field.SetValue(room, mutation.OriginalValue);
                else
                    Plugin.Logger?.LogDebug(
                        "DevTool effect preview left externally changed room field '" +
                        mutation.Field.Name + "' untouched during rollback.");
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning(
                    "DevTool effect preview field rollback failed for '" + mutation.Field.Name + "': " + error.Message);
            }
        }
        fieldMutations.Clear();

        // Clean once more after Room.CleanOutObjectNotInThisRoom in case a drawable was detached
        // from room.drawableObjects only during object removal.
        CleanCameraLeasers();
        ownedDrawables.Clear();
    }

    internal static void DisposeCapturedObject(UpdatableAndDeletable obj, global::Room room)
    {
        if (obj == null) return;
        try { obj.Destroy(); }
        catch { }

        try
        {
            // Captured objects never passed through Room.AddObject. Some constructors nevertheless
            // assign room themselves; clear only that unattached back-reference.
            if (ReferenceEquals(obj.room, room) && !ContainsReference(room?.updateList, obj))
                obj.RemoveFromRoom();
        }
        catch { }
    }

    private void CleanCameraLeasers()
    {
        if (ownedDrawables.Count == 0 || room?.game?.cameras == null) return;

        RoomCamera[] cameras = room.game.cameras;
        for (int c = 0; c < cameras.Length; c++)
        {
            RoomCamera camera = cameras[c];
            if (camera == null || !ReferenceEquals(camera.room, room) || camera.spriteLeasers == null)
                continue;

            for (int i = camera.spriteLeasers.Count - 1; i >= 0; i--)
            {
                RoomCamera.SpriteLeaser leaser = camera.spriteLeasers[i];
                if (leaser?.drawableObject == null || !ownedDrawables.Contains(leaser.drawableObject))
                    continue;

                try { leaser.CleanSpritesAndRemove(); }
                catch { }
                camera.spriteLeasers.RemoveAt(i);
            }
        }
    }

    private static HashSet<IDrawable> SnapshotDrawables(global::Room target)
    {
        HashSet<IDrawable> result = new(ReferenceEqualityComparer<IDrawable>.Instance);
        if (target?.drawableObjects == null) return result;
        for (int i = 0; i < target.drawableObjects.Count; i++)
        {
            IDrawable drawable = target.drawableObjects[i];
            if (drawable != null) result.Add(drawable);
        }
        return result;
    }

    private static bool ContainsReference<T>(IList<T> values, T target) where T : class
    {
        if (values == null || target == null) return false;
        for (int i = 0; i < values.Count; i++)
            if (ReferenceEquals(values[i], target)) return true;
        return false;
    }

    internal static bool SameValue(object a, object b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        Type type = a.GetType();
        if (type.IsValueType || a is string)
            return a.Equals(b);
        return false;
    }

    private readonly struct FieldMutation
    {
        internal FieldMutation(FieldInfo field, object originalValue, object previewValue)
        {
            Field = field;
            OriginalValue = originalValue;
            PreviewValue = previewValue;
        }

        internal FieldInfo Field { get; }
        internal object OriginalValue { get; }
        internal object PreviewValue { get; }
    }
}

internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
{
    internal static readonly ReferenceEqualityComparer<T> Instance = new();

    public bool Equals(T x, T y) => ReferenceEquals(x, y);
    public int GetHashCode(T obj) => obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
}
