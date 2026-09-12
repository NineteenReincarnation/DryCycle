using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Preview;

/// <summary>
/// Captures scene state that can change synchronously while a load-time RoomEffect runtime object is
/// being bootstrapped, but is not represented by Room.updateList ownership.
///
/// The journal is deliberately temporal rather than mod-specific: it snapshots the cameras viewing
/// the room immediately before advanced bootstrap, then seals the delta immediately after bootstrap
/// returns, before another normal game frame can run. This makes synchronous camera-field changes
/// and direct Futile layer insertions attributable to the preview without knowing who authored them.
/// Runtime changes after the seal are never guessed; a field that no longer equals the sealed value
/// is left untouched and reported as ambiguous instead of overwriting legitimate game state.
/// </summary>
internal sealed class EffectPreviewSceneStateJournal
{
    private static readonly FieldInfo[] CameraFields = DiscoverCameraFields();

    private readonly global::Room room;
    private readonly List<CameraSnapshot> cameras = new();
    private readonly List<CameraFieldMutation> cameraMutations = new();
    private readonly List<FNode> ownedLayerRoots = new();
    private bool sealedState;

    private EffectPreviewSceneStateJournal(global::Room room)
    {
        this.room = room;
        CaptureCameras();
    }

    internal static EffectPreviewSceneStateJournal Capture(global::Room room) =>
        room == null ? null : new EffectPreviewSceneStateJournal(room);

    internal int CameraMutationCount => cameraMutations.Count;
    internal int FutileRootCount => ownedLayerRoots.Count;

    internal void Seal()
    {
        if (sealedState || room == null) return;
        sealedState = true;

        for (int i = 0; i < cameras.Count; i++)
        {
            CameraSnapshot snapshot = cameras[i];
            RoomCamera camera = snapshot.Camera;
            if (camera == null || !ReferenceEquals(camera.room, room))
                continue;

            foreach (KeyValuePair<FieldInfo, object> pair in snapshot.FieldValues)
            {
                object current;
                try { current = pair.Key.GetValue(camera); }
                catch { continue; }
                if (SameValue(pair.Value, current)) continue;
                cameraMutations.Add(new CameraFieldMutation(camera, pair.Key, pair.Value, current));
            }

            FContainer[] layers = camera.SpriteLayers;
            if (layers == null) continue;
            int layerCount = Math.Min(layers.Length, snapshot.LayerChildren.Length);
            for (int layerIndex = 0; layerIndex < layerCount; layerIndex++)
            {
                FContainer layer = layers[layerIndex];
                if (layer == null) continue;
                HashSet<FNode> baseline = snapshot.LayerChildren[layerIndex];
                int childCount;
                try { childCount = layer.GetChildCount(); }
                catch { continue; }

                for (int childIndex = 0; childIndex < childCount; childIndex++)
                {
                    FNode child;
                    try { child = layer.GetChildAt(childIndex); }
                    catch { continue; }
                    if (child == null || baseline.Contains(child) || ContainsReference(ownedLayerRoots, child))
                        continue;
                    ownedLayerRoots.Add(child);
                }
            }
        }
    }

    internal EffectPreviewSceneRollbackReport Rollback(string reason)
    {
        if (room == null)
            return EffectPreviewSceneRollbackReport.Clean;

        int nodeLeaks = 0;
        int fieldLeaks = 0;
        int ambiguousFields = 0;

        for (int i = ownedLayerRoots.Count - 1; i >= 0; i--)
        {
            FNode node = ownedLayerRoots[i];
            if (node == null) continue;
            try { node.RemoveFromContainer(); }
            catch { nodeLeaks++; }

            try
            {
                if (node.container != null)
                    nodeLeaks++;
            }
            catch
            {
                nodeLeaks++;
            }
        }

        for (int i = cameraMutations.Count - 1; i >= 0; i--)
        {
            CameraFieldMutation mutation = cameraMutations[i];
            if (mutation.Camera == null || mutation.Field == null) continue;

            try
            {
                object current = mutation.Field.GetValue(mutation.Camera);
                if (SameValue(current, mutation.PreviewValue))
                {
                    mutation.Field.SetValue(mutation.Camera, mutation.OriginalValue);
                    object restored = mutation.Field.GetValue(mutation.Camera);
                    if (!SameValue(restored, mutation.OriginalValue))
                        fieldLeaks++;
                }
                else if (!SameValue(current, mutation.OriginalValue))
                {
                    // Something changed the same camera field after the synchronous bootstrap
                    // window. We cannot know whether it was the preview or normal gameplay, so do
                    // not overwrite it. Mark the effect unsafe for future advanced previews.
                    ambiguousFields++;
                }
            }
            catch
            {
                fieldLeaks++;
            }
        }

        bool strong = nodeLeaks > 0 || fieldLeaks > 0 || ambiguousFields > 0;
        string summary = strong
            ? "scene-state rollback: nodes=" + nodeLeaks +
              ", fields=" + fieldLeaks +
              ", ambiguousCameraFields=" + ambiguousFields +
              (string.IsNullOrWhiteSpace(reason) ? string.Empty : " during " + reason)
            : string.Empty;

        ownedLayerRoots.Clear();
        cameraMutations.Clear();
        cameras.Clear();
        return new EffectPreviewSceneRollbackReport(strong, summary);
    }

    private void CaptureCameras()
    {
        RoomCamera[] live = room?.game?.cameras;
        if (live == null) return;

        for (int i = 0; i < live.Length; i++)
        {
            RoomCamera camera = live[i];
            if (camera == null || !ReferenceEquals(camera.room, room))
                continue;

            Dictionary<FieldInfo, object> fields = new();
            for (int f = 0; f < CameraFields.Length; f++)
            {
                FieldInfo field = CameraFields[f];
                try { fields[field] = field.GetValue(camera); }
                catch { }
            }

            FContainer[] layers = camera.SpriteLayers;
            HashSet<FNode>[] layerChildren = layers == null
                ? Array.Empty<HashSet<FNode>>()
                : new HashSet<FNode>[layers.Length];

            for (int layerIndex = 0; layerIndex < layerChildren.Length; layerIndex++)
            {
                HashSet<FNode> children = new(ReferenceEqualityComparer<FNode>.Instance);
                FContainer layer = layers[layerIndex];
                if (layer != null)
                {
                    int count;
                    try { count = layer.GetChildCount(); }
                    catch { count = 0; }
                    for (int childIndex = 0; childIndex < count; childIndex++)
                    {
                        try
                        {
                            FNode child = layer.GetChildAt(childIndex);
                            if (child != null) children.Add(child);
                        }
                        catch { }
                    }
                }
                layerChildren[layerIndex] = children;
            }

            cameras.Add(new CameraSnapshot(camera, fields, layerChildren));
        }
    }

    private static FieldInfo[] DiscoverCameraFields()
    {
        List<FieldInfo> result = new();
        for (Type type = typeof(RoomCamera); type != null && type != typeof(object); type = type.BaseType)
        {
            FieldInfo[] fields;
            try
            {
                fields = type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch
            {
                continue;
            }

            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.IsStatic || field.IsInitOnly || field.IsLiteral || !SafeCameraFieldType(field.FieldType))
                    continue;
                result.Add(field);
            }
        }
        return result.ToArray();
    }

    private static bool SafeCameraFieldType(Type type)
    {
        if (type == null) return false;
        if (type.IsValueType || type == typeof(string)) return true;

        // Collections are intentionally not snapshotted by reference: their identity normally stays
        // constant while contents change, so restoring the reference would not undo anything and
        // could create a false sense of safety.
        if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            return false;

        if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return true;
        if (typeof(FNode).IsAssignableFrom(type)) return true;
        if (typeof(UpdatableAndDeletable).IsAssignableFrom(type)) return true;
        if (typeof(IDrawable).IsAssignableFrom(type)) return true;
        if (type == typeof(RoomSettings.RoomEffect.Type)) return true;
        return false;
    }

    private static bool SameValue(object a, object b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        Type type = a.GetType();
        return type.IsValueType || a is string ? a.Equals(b) : false;
    }

    private static bool ContainsReference<T>(List<T> values, T target) where T : class
    {
        if (values == null || target == null) return false;
        for (int i = 0; i < values.Count; i++)
            if (ReferenceEquals(values[i], target)) return true;
        return false;
    }

    private sealed class CameraSnapshot
    {
        internal CameraSnapshot(
            RoomCamera camera,
            Dictionary<FieldInfo, object> fieldValues,
            HashSet<FNode>[] layerChildren)
        {
            Camera = camera;
            FieldValues = fieldValues ?? new Dictionary<FieldInfo, object>();
            LayerChildren = layerChildren ?? Array.Empty<HashSet<FNode>>();
        }

        internal RoomCamera Camera { get; }
        internal Dictionary<FieldInfo, object> FieldValues { get; }
        internal HashSet<FNode>[] LayerChildren { get; }
    }

    private readonly struct CameraFieldMutation
    {
        internal CameraFieldMutation(RoomCamera camera, FieldInfo field, object originalValue, object previewValue)
        {
            Camera = camera;
            Field = field;
            OriginalValue = originalValue;
            PreviewValue = previewValue;
        }

        internal RoomCamera Camera { get; }
        internal FieldInfo Field { get; }
        internal object OriginalValue { get; }
        internal object PreviewValue { get; }
    }
}

internal readonly struct EffectPreviewSceneRollbackReport
{
    internal EffectPreviewSceneRollbackReport(bool hasLeak, string summary)
    {
        HasLeak = hasLeak;
        Summary = summary ?? string.Empty;
    }

    internal bool HasLeak { get; }
    internal string Summary { get; }

    internal static EffectPreviewSceneRollbackReport Clean => new(false, string.Empty);
}
