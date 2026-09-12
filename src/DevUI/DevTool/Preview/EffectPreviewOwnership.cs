using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DryCycle.DevUI.DevTool.Preview;

/// <summary>
/// Intercepts Room.AddObject for two narrowly-scoped purposes:
///
/// 1. During a bootstrap probe, objects are captured before Rain World mutates room state.
/// 2. During a live preview, objects spawned synchronously by an exclusively preview-owned runtime
///    object are adopted by the same ownership transaction.
///
/// The second path never keys on a mod, assembly or namespace. Causality is inferred from the call
/// stack only when every live Room object matching the caller's UpdatableAndDeletable type is owned
/// by the preview. Ambiguous calls fail closed and are left to normal Rain World behavior.
/// </summary>
internal static class EffectPreviewObjectCapture
{
    private static bool enabled;
    private static CaptureScope activeScope;
    private static EffectPreviewOwnershipTransaction runtimeOwner;

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
        runtimeOwner = null;
        On.Room.AddObject -= Room_AddObject;
        enabled = false;
    }

    internal static void AttachRuntimeOwner(EffectPreviewOwnershipTransaction owner)
    {
        runtimeOwner = owner;
    }

    internal static void DetachRuntimeOwner(EffectPreviewOwnershipTransaction owner)
    {
        if (ReferenceEquals(runtimeOwner, owner))
            runtimeOwner = null;
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

        EffectPreviewOwnershipTransaction owner = runtimeOwner;
        EffectPreviewRuntimeAddObservation observation = default;
        bool observe = owner != null && owner.TryBeginRuntimeAdd(self, obj, out observation);

        orig(self, obj);

        if (observe)
            owner.CompleteRuntimeAdd(self, obj, observation);
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

internal readonly struct EffectPreviewRuntimeAddObservation
{
    internal EffectPreviewRuntimeAddObservation(HashSet<IDrawable> beforeDrawables)
    {
        BeforeDrawables = beforeDrawables;
    }

    internal HashSet<IDrawable> BeforeDrawables { get; }
}

/// <summary>
/// Owns every runtime artifact deliberately committed for one hover preview. Rollback is strictly
/// identity-based: real room objects of the same type are never removed.
///
/// Ownership propagates to non-physical objects created from an owned object's synchronous update
/// call. A PhysicalObject spawned from the preview is treated as contamination because abstract
/// entity/save-state effects cannot be universally reversed without knowing that mod's semantics.
/// </summary>
internal sealed class EffectPreviewOwnershipTransaction
{
    private readonly global::Room room;
    private readonly EffectPreviewRuntimeBaseline baseline;
    private readonly List<UpdatableAndDeletable> ownedObjects = new();
    private readonly HashSet<UpdatableAndDeletable> ownedObjectSet =
        new(ReferenceEqualityComparer<UpdatableAndDeletable>.Instance);
    private readonly HashSet<IDrawable> ownedDrawables =
        new(ReferenceEqualityComparer<IDrawable>.Instance);
    private readonly List<FieldMutation> fieldMutations = new();

    private bool runtimePropagationActive;
    private bool runtimeContaminated;
    private string contaminationReason = string.Empty;

    internal EffectPreviewOwnershipTransaction(global::Room room)
    {
        this.room = room;
        baseline = EffectPreviewRuntimeBaseline.Capture(room);
    }

    internal int ObjectCount => ownedObjects.Count;
    internal int FieldMutationCount => fieldMutations.Count;
    internal bool RequiresAbort => runtimeContaminated;
    internal string ContaminationReason => contaminationReason;

    internal void ActivateRuntimePropagation()
    {
        if (room == null || runtimePropagationActive) return;
        runtimePropagationActive = true;
        EffectPreviewObjectCapture.AttachRuntimeOwner(this);
    }

    internal void DeactivateRuntimePropagation()
    {
        if (!runtimePropagationActive) return;
        runtimePropagationActive = false;
        EffectPreviewObjectCapture.DetachRuntimeOwner(this);
    }

    internal bool CommitObject(UpdatableAndDeletable obj)
    {
        if (room == null || obj == null) return false;

        // RoomEffect preview is a visual/editor facility. Committing a PhysicalObject can mutate
        // AbstractRoom.entities, creature lists, save-state ownership and consumable state.
        if (obj is PhysicalObject)
        {
            MarkContamination(
                "bootstrap attempted to create physical object " + (obj.GetType().FullName ?? obj.GetType().Name));
            DisposeCapturedObject(obj, room);
            return false;
        }

        HashSet<IDrawable> before = SnapshotDrawables(room);
        try
        {
            room.AddObject(obj);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool effect preview object commit failed for '" + obj.GetType().FullName + "': " + error.Message);
            CleanupFailedCommit(obj);
            return false;
        }

        bool attached = ContainsReference(room.updateList, obj) || ReferenceEquals(obj.room, room);
        if (!attached)
        {
            DisposeCapturedObject(obj, room);
            return false;
        }

        OwnAttachedObject(obj, before);
        return true;
    }

    /// <summary>
    /// Called from the global Room.AddObject hook before the real add. Stack inspection is done only
    /// while an advanced preview is alive, and adoption is allowed only when the caller type is
    /// unambiguous: every live instance of that UAD type in this room must already be preview-owned.
    /// </summary>
    internal bool TryBeginRuntimeAdd(
        global::Room targetRoom,
        UpdatableAndDeletable obj,
        out EffectPreviewRuntimeAddObservation observation)
    {
        observation = default;
        if (!runtimePropagationActive || runtimeContaminated || room == null || obj == null ||
            !ReferenceEquals(room, targetRoom) || ownedObjectSet.Contains(obj))
            return false;

        if (!HasExclusivelyOwnedCaller())
            return false;

        observation = new EffectPreviewRuntimeAddObservation(SnapshotDrawables(room));
        return true;
    }

    internal void CompleteRuntimeAdd(
        global::Room targetRoom,
        UpdatableAndDeletable obj,
        EffectPreviewRuntimeAddObservation observation)
    {
        if (!runtimePropagationActive || room == null || obj == null ||
            !ReferenceEquals(room, targetRoom) || ownedObjectSet.Contains(obj))
            return;

        try
        {
            if (obj is PhysicalObject)
            {
                MarkContamination(
                    "preview-owned runtime object spawned physical object " +
                    (obj.GetType().FullName ?? obj.GetType().Name));
                CleanupFailedCommit(obj);
                return;
            }

            bool attached = ContainsReference(room.updateList, obj) || ReferenceEquals(obj.room, room);
            if (!attached) return;

            OwnAttachedObject(obj, observation.BeforeDrawables ?? SnapshotDrawables(room));
        }
        catch (Exception error)
        {
            MarkContamination("runtime ownership propagation failed: " + error.Message);
            Plugin.Logger?.LogWarning(
                "DevTool effect preview runtime ownership propagation failed for '" +
                obj.GetType().FullName + "': " + error.Message);
        }
    }

    internal bool ApplyFieldMutation(FieldInfo field, object originalValue, object previewValue)
    {
        if (room == null || field == null || field.IsStatic || field.IsInitOnly || field.IsLiteral)
            return false;

        object current;
        try { current = field.GetValue(room); }
        catch { return false; }

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

    internal EffectPreviewRollbackReport Rollback(string reason)
    {
        if (room == null)
            return new EffectPreviewRollbackReport(
                hasStrongLeak: false,
                softBaselineChanged: false,
                runtimeContaminated: runtimeContaminated,
                summary: contaminationReason);

        DeactivateRuntimePropagation();

        int rollbackErrors = 0;
        int fieldLeaks = 0;

        // Keep room manager/back-reference fields intact while owned objects tear themselves down.
        // Some load-time scene objects use those fields from Destroy().
        CleanCameraLeasers();

        for (int i = ownedObjects.Count - 1; i >= 0; i--)
        {
            UpdatableAndDeletable obj = ownedObjects[i];
            if (obj == null) continue;
            try
            {
                obj.Destroy();
                if (ContainsReference(room.updateList, obj))
                    room.CleanOutObjectNotInThisRoom(obj);
                if (ReferenceEquals(obj.room, room))
                    obj.RemoveFromRoom();
            }
            catch (Exception error)
            {
                rollbackErrors++;
                Plugin.Logger?.LogWarning(
                    "DevTool effect preview object rollback failed for '" + obj.GetType().FullName +
                    "' (" + reason + "): " + error.Message);
                try
                {
                    if (ContainsReference(room.updateList, obj))
                        room.CleanOutObjectNotInThisRoom(obj);
                    if (ReferenceEquals(obj.room, room))
                        obj.RemoveFromRoom();
                }
                catch { }
            }
        }

        for (int i = fieldMutations.Count - 1; i >= 0; i--)
        {
            FieldMutation mutation = fieldMutations[i];
            try
            {
                object current = mutation.Field.GetValue(room);
                if (SameValue(current, mutation.PreviewValue))
                    mutation.Field.SetValue(room, mutation.OriginalValue);
                else
                    Plugin.Logger?.LogDebug(
                        "DevTool effect preview left externally changed room field '" +
                        mutation.Field.Name + "' untouched during rollback.");

                object restored = mutation.Field.GetValue(room);
                if (SameValue(restored, mutation.PreviewValue) &&
                    !SameValue(mutation.OriginalValue, mutation.PreviewValue))
                    fieldLeaks++;
            }
            catch (Exception error)
            {
                rollbackErrors++;
                fieldLeaks++;
                Plugin.Logger?.LogWarning(
                    "DevTool effect preview field rollback failed for '" + mutation.Field.Name + "': " + error.Message);
            }
        }

        CleanCameraLeasers();

        int objectLeaks = CountOwnedObjectLeaks();
        int drawableLeaks = CountOwnedDrawableLeaks();
        int leaserLeaks = CountOwnedCameraLeaserLeaks();
        bool strongLeak = rollbackErrors > 0 || fieldLeaks > 0 || objectLeaks > 0 ||
                          drawableLeaks > 0 || leaserLeaks > 0;

        bool softChanged = baseline.TryDescribeDifference(room, out string baselineDifference);
        string summary = BuildRollbackSummary(
            reason,
            rollbackErrors,
            fieldLeaks,
            objectLeaks,
            drawableLeaks,
            leaserLeaks,
            baselineDifference);

        if (softChanged && !strongLeak && !runtimeContaminated)
        {
            Plugin.Logger?.LogDebug(
                "DevTool effect preview rollback observed ordinary room churn (not treated as unsafe): " +
                baselineDifference);
        }

        ownedObjects.Clear();
        ownedObjectSet.Clear();
        ownedDrawables.Clear();
        fieldMutations.Clear();

        return new EffectPreviewRollbackReport(
            strongLeak,
            softChanged,
            runtimeContaminated,
            summary);
    }

    internal static void DisposeCapturedObject(UpdatableAndDeletable obj, global::Room room)
    {
        if (obj == null) return;
        try { obj.Destroy(); }
        catch { }

        try
        {
            if (ContainsReference(room?.updateList, obj))
                room.CleanOutObjectNotInThisRoom(obj);
            if (ReferenceEquals(obj.room, room))
                obj.RemoveFromRoom();
        }
        catch { }
    }

    private void OwnAttachedObject(UpdatableAndDeletable obj, HashSet<IDrawable> beforeDrawables)
    {
        if (obj == null || !ownedObjectSet.Add(obj)) return;
        ownedObjects.Add(obj);

        if (room.drawableObjects == null) return;
        beforeDrawables ??= new HashSet<IDrawable>(ReferenceEqualityComparer<IDrawable>.Instance);
        for (int i = 0; i < room.drawableObjects.Count; i++)
        {
            IDrawable drawable = room.drawableObjects[i];
            if (drawable != null && !beforeDrawables.Contains(drawable))
                ownedDrawables.Add(drawable);
        }

        if (obj is IDrawable direct)
            ownedDrawables.Add(direct);
    }

    private bool HasExclusivelyOwnedCaller()
    {
        StackFrame[] frames;
        try { frames = new StackTrace(2, false).GetFrames(); }
        catch { return false; }
        if (frames == null || frames.Length == 0) return false;

        for (int i = 0; i < frames.Length; i++)
        {
            MethodBase method;
            try { method = frames[i].GetMethod(); }
            catch { continue; }

            Type declaring = method?.DeclaringType;
            if (declaring == null || declaring == typeof(UpdatableAndDeletable) ||
                !typeof(UpdatableAndDeletable).IsAssignableFrom(declaring))
                continue;

            bool found = false;
            bool allOwned = true;
            List<UpdatableAndDeletable> live = room.updateList;
            if (live == null) continue;

            for (int n = 0; n < live.Count; n++)
            {
                UpdatableAndDeletable candidate = live[n];
                if (candidate == null || !declaring.IsAssignableFrom(candidate.GetType()))
                    continue;
                found = true;
                if (!ownedObjectSet.Contains(candidate))
                {
                    allOwned = false;
                    break;
                }
            }

            if (found && allOwned)
                return true;
        }

        return false;
    }

    private void MarkContamination(string reason)
    {
        runtimeContaminated = true;
        if (string.IsNullOrWhiteSpace(contaminationReason))
            contaminationReason = reason ?? "runtime contamination";
    }

    private void CleanupFailedCommit(UpdatableAndDeletable obj)
    {
        if (obj == null) return;
        try
        {
            obj.Destroy();
            if (ContainsReference(room?.updateList, obj))
                room?.CleanOutObjectNotInThisRoom(obj);
            if (ReferenceEquals(obj.room, room))
                obj.RemoveFromRoom();
        }
        catch
        {
            try
            {
                if (ReferenceEquals(obj.room, room)) obj.RemoveFromRoom();
            }
            catch { }
        }
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

    private int CountOwnedObjectLeaks()
    {
        int count = 0;
        foreach (UpdatableAndDeletable obj in ownedObjectSet)
        {
            if (obj == null) continue;
            if (ContainsReference(room.updateList, obj) || ReferenceEquals(obj.room, room))
                count++;
        }
        return count;
    }

    private int CountOwnedDrawableLeaks()
    {
        if (room.drawableObjects == null || ownedDrawables.Count == 0) return 0;
        int count = 0;
        foreach (IDrawable drawable in ownedDrawables)
            if (drawable != null && ContainsReference(room.drawableObjects, drawable)) count++;
        return count;
    }

    private int CountOwnedCameraLeaserLeaks()
    {
        if (room?.game?.cameras == null || ownedDrawables.Count == 0) return 0;
        int count = 0;
        RoomCamera[] cameras = room.game.cameras;
        for (int c = 0; c < cameras.Length; c++)
        {
            List<RoomCamera.SpriteLeaser> leasers = cameras[c]?.spriteLeasers;
            if (leasers == null) continue;
            for (int i = 0; i < leasers.Count; i++)
            {
                IDrawable drawable = leasers[i]?.drawableObject;
                if (drawable != null && ownedDrawables.Contains(drawable)) count++;
            }
        }
        return count;
    }

    private string BuildRollbackSummary(
        string reason,
        int rollbackErrors,
        int fieldLeaks,
        int objectLeaks,
        int drawableLeaks,
        int leaserLeaks,
        string baselineDifference)
    {
        List<string> parts = new();
        if (runtimeContaminated)
            parts.Add(string.IsNullOrWhiteSpace(contaminationReason) ? "runtime contaminated" : contaminationReason);
        if (rollbackErrors > 0) parts.Add("rollback errors=" + rollbackErrors);
        if (fieldLeaks > 0) parts.Add("field leaks=" + fieldLeaks);
        if (objectLeaks > 0) parts.Add("object leaks=" + objectLeaks);
        if (drawableLeaks > 0) parts.Add("drawable leaks=" + drawableLeaks);
        if (leaserLeaks > 0) parts.Add("camera leaser leaks=" + leaserLeaks);
        if (!string.IsNullOrWhiteSpace(baselineDifference)) parts.Add(baselineDifference);
        if (parts.Count == 0) parts.Add("clean rollback");
        if (!string.IsNullOrWhiteSpace(reason)) parts.Add("reason=" + reason);
        return string.Join(", ", parts);
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

/// <summary>
/// A diagnostic baseline captured before advanced bootstrap. Identity/count changes are reported but
/// not automatically treated as unsafe because normal Rain World rooms create transient particles,
/// drips and other objects while the mouse is hovering. Strong ownership leaks are checked
/// separately by EffectPreviewOwnershipTransaction.
/// </summary>
internal sealed class EffectPreviewRuntimeBaseline
{
    private readonly HashSet<UpdatableAndDeletable> updateObjects;
    private readonly HashSet<IDrawable> drawables;
    private readonly HashSet<RoomCamera.SpriteLeaser> cameraLeasers;

    private EffectPreviewRuntimeBaseline(
        HashSet<UpdatableAndDeletable> updateObjects,
        HashSet<IDrawable> drawables,
        HashSet<RoomCamera.SpriteLeaser> cameraLeasers)
    {
        this.updateObjects = updateObjects;
        this.drawables = drawables;
        this.cameraLeasers = cameraLeasers;
    }

    internal static EffectPreviewRuntimeBaseline Capture(global::Room room)
    {
        HashSet<UpdatableAndDeletable> updates =
            new(ReferenceEqualityComparer<UpdatableAndDeletable>.Instance);
        HashSet<IDrawable> roomDrawables =
            new(ReferenceEqualityComparer<IDrawable>.Instance);
        HashSet<RoomCamera.SpriteLeaser> leasers =
            new(ReferenceEqualityComparer<RoomCamera.SpriteLeaser>.Instance);

        if (room?.updateList != null)
            for (int i = 0; i < room.updateList.Count; i++)
                if (room.updateList[i] != null) updates.Add(room.updateList[i]);

        if (room?.drawableObjects != null)
            for (int i = 0; i < room.drawableObjects.Count; i++)
                if (room.drawableObjects[i] != null) roomDrawables.Add(room.drawableObjects[i]);

        RoomCamera[] cameras = room?.game?.cameras;
        if (cameras != null)
        {
            for (int c = 0; c < cameras.Length; c++)
            {
                RoomCamera camera = cameras[c];
                if (camera == null || !ReferenceEquals(camera.room, room) || camera.spriteLeasers == null)
                    continue;
                for (int i = 0; i < camera.spriteLeasers.Count; i++)
                    if (camera.spriteLeasers[i] != null) leasers.Add(camera.spriteLeasers[i]);
            }
        }

        return new EffectPreviewRuntimeBaseline(updates, roomDrawables, leasers);
    }

    internal bool TryDescribeDifference(global::Room room, out string description)
    {
        HashSet<UpdatableAndDeletable> currentUpdates =
            new(ReferenceEqualityComparer<UpdatableAndDeletable>.Instance);
        HashSet<IDrawable> currentDrawables =
            new(ReferenceEqualityComparer<IDrawable>.Instance);
        HashSet<RoomCamera.SpriteLeaser> currentLeasers =
            new(ReferenceEqualityComparer<RoomCamera.SpriteLeaser>.Instance);

        if (room?.updateList != null)
            for (int i = 0; i < room.updateList.Count; i++)
                if (room.updateList[i] != null) currentUpdates.Add(room.updateList[i]);

        if (room?.drawableObjects != null)
            for (int i = 0; i < room.drawableObjects.Count; i++)
                if (room.drawableObjects[i] != null) currentDrawables.Add(room.drawableObjects[i]);

        RoomCamera[] cameras = room?.game?.cameras;
        if (cameras != null)
        {
            for (int c = 0; c < cameras.Length; c++)
            {
                RoomCamera camera = cameras[c];
                if (camera == null || !ReferenceEquals(camera.room, room) || camera.spriteLeasers == null)
                    continue;
                for (int i = 0; i < camera.spriteLeasers.Count; i++)
                    if (camera.spriteLeasers[i] != null) currentLeasers.Add(camera.spriteLeasers[i]);
            }
        }

        CountIdentityDelta(updateObjects, currentUpdates, out int updateAdded, out int updateMissing);
        CountIdentityDelta(drawables, currentDrawables, out int drawableAdded, out int drawableMissing);
        CountIdentityDelta(cameraLeasers, currentLeasers, out int leaserAdded, out int leaserMissing);

        bool changed = updateAdded != 0 || updateMissing != 0 || drawableAdded != 0 ||
                       drawableMissing != 0 || leaserAdded != 0 || leaserMissing != 0;
        description = changed
            ? "baseline delta update +" + updateAdded + "/-" + updateMissing +
              ", drawable +" + drawableAdded + "/-" + drawableMissing +
              ", camera leaser +" + leaserAdded + "/-" + leaserMissing
            : string.Empty;
        return changed;
    }

    private static void CountIdentityDelta<T>(
        HashSet<T> baseline,
        HashSet<T> current,
        out int added,
        out int missing) where T : class
    {
        added = 0;
        missing = 0;
        foreach (T item in current)
            if (!baseline.Contains(item)) added++;
        foreach (T item in baseline)
            if (!current.Contains(item)) missing++;
    }
}

internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
{
    internal static readonly ReferenceEqualityComparer<T> Instance = new();

    public bool Equals(T x, T y) => ReferenceEquals(x, y);
    public int GetHashCode(T obj) => obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
}
