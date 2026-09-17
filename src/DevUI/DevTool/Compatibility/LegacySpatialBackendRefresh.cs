using System;
using System.Collections.Generic;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Transitional backend refresh for the one migrated workspace that still depends on vanilla
/// world-space representations: Objects.
///
/// Sound and Trigger no longer pass through this compatibility layer. Built-in Sound runtime audio
/// is reconciled by NativeSoundRuntimeReconciler and its spatial editing is owned by Native Gizmo;
/// Trigger has no hidden runtime backend at all. Their vanilla Panel/Handle trees are therefore
/// presentation-only state recreated solely when explicit Vanilla/Legacy mode is requested.
/// </summary>
internal static class LegacySpatialBackendRefresh
{
    internal static bool TryRefreshObjects(ObjectsPage page)
    {
        if (page?.owner?.room == null || page.RoomSettings?.placedObjects == null || page.initRefresh)
            return false;

        try
        {
            ReconcileObjectRepresentations(page);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool minimal Objects backend refresh failed: " + error.Message);
            return false;
        }
    }

    private static void ReconcileObjectRepresentations(ObjectsPage page)
    {
        List<PlacedObject> objects = page.RoomSettings.placedObjects;
        HashSet<PlacedObject> represented = new(ReferenceComparer<PlacedObject>.Instance);

        // ObjectsPage.CreateObjRep registers each representation as a direct tempNode. Retain the
        // representation instance for every still-live model member so POM/RegionKit/vanilla handle
        // state, drag state and sprite allocations are not churned by unrelated edits.
        for (int i = page.subNodes.Count - 1; i >= 0; i--)
        {
            if (page.subNodes[i] is not PlacedObjectRepresentation representation)
                continue;

            PlacedObject placedObject = representation.pObj;
            if (placedObject == null ||
                !ContainsReference(objects, placedObject) ||
                !represented.Add(placedObject))
            {
                RemoveDynamicNode(page, representation, i);
            }
        }

        // Only missing model members need a compatibility representation. Use vanilla's public
        // factory path rather than constructing base representations ourselves so ordinary hooks and
        // custom object factories continue to participate exactly as they do in ObjectsPage.Refresh.
        for (int i = 0; i < objects.Count; i++)
        {
            PlacedObject placedObject = objects[i];
            if (placedObject?.type == null || represented.Contains(placedObject))
                continue;

            page.CreateObjRep(placedObject.type, placedObject);
            represented.Add(placedObject);
        }
    }

    private static void RemoveDynamicNode(Page page, DevUINode node, int subNodeIndex)
    {
        node.ClearSprites();
        page.subNodes.RemoveAt(subNodeIndex);
        page.tempNodes?.Remove(node);

        if (page is ObjectsPage objects && ReferenceEquals(objects.draggedObject, node))
            objects.draggedObject = null;
    }

    private static bool ContainsReference<T>(List<T> values, T target) where T : class
    {
        if (values == null || target == null) return false;
        for (int i = 0; i < values.Count; i++)
            if (ReferenceEquals(values[i], target)) return true;
        return false;
    }

    private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new();

        public bool Equals(T x, T y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) =>
            obj == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
