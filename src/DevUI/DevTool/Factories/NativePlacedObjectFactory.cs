using System;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Factories;

/// <summary>
/// Native model factory for Rain World/DLC/Watcher PlacedObject types. Registered native providers
/// get first refusal, game-defined ExtEnum IDs then construct directly through
/// PlacedObject.GenerateEmptyData, and external IDs delegate to the isolated headless compatibility
/// host so CreateObjRep-based third-party creation stays supported without owning a legacy page here.
/// </summary>
internal static class NativePlacedObjectFactory
{
    internal static bool TryCreate(
        EditorSession session,
        PlacedObject.Type type,
        Vector2 worldPosition,
        out PlacedObject created)
    {
        created = null;
        if (session?.RoomSettings?.placedObjects == null || type == null)
            return false;

        if (NativeAuthoringFactoryRegistry.TryCreatePlacedObject(session, type, worldPosition, out created))
        {
            if (created == null) return false;
            created.pos = worldPosition;
            if (!ContainsReference(session.RoomSettings.placedObjects, created))
                session.RoomSettings.placedObjects.Add(created);
            return true;
        }

        if (!GameDefinedExtEnumCatalog.Contains(typeof(PlacedObject.Type), type.value))
            return HeadlessObjectCompatibilityHost.TryCreateExternalObject(
                session,
                type,
                worldPosition,
                out created);

        try
        {
            created = new PlacedObject(type, null)
            {
                pos = worldPosition
            };

            ApplyNativeCreationDefaults(session, created);
            session.RoomSettings.placedObjects.Add(created);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool native object factory failed for '" + (type.value ?? string.Empty) + "': " + error.Message);
            created = null;
            return false;
        }
    }

    private static void ApplyNativeCreationDefaults(EditorSession session, PlacedObject created)
    {
        if (created?.type == PlacedObject.Type.LightFixture &&
            created.data is PlacedObject.LightFixtureData light)
        {
            // Vanilla remembers this in ObjectsPage UI state. Native authoring has no page state, so
            // carry forward the most recently authored fixture from the document instead.
            for (int i = session.RoomSettings.placedObjects.Count - 1; i >= 0; i--)
            {
                if (session.RoomSettings.placedObjects[i]?.data is not PlacedObject.LightFixtureData previous)
                    continue;
                light.type = previous.type;
                break;
            }
        }

        if (ModManager.Watcher &&
            created?.data is Watcher.FloatingDebrisData floatingDebris)
        {
            // Vanilla randomized this from FloatingDebrisRepresentation's constructor. Native
            // Objects never materializes that representation, so new authored debris needs the seed
            // before its runtime is reconciled.
            floatingDebris.seed = UnityEngine.Random.Range(0, int.MaxValue);
        }

        if (ModManager.Watcher &&
            created?.type == Watcher.WatcherEnums.PlacedObjectType.UrbanCandleHolder &&
            created.data is Watcher.UrbanCandleHolder.UrbanCandleHolderData holder)
        {
            // Vanilla's representation constructor randomized this when first materialized. Native
            // Objects never constructs that representation, so preserve the authored default here.
            holder.seed = (int)(UnityEngine.Random.value * 111111f);
        }

        if (created?.type == PlacedObject.Type.TerrainHandle && session.Room?.terrain == null)
        {
            // This is the one model/runtime side effect vanilla CreateObjRep performs at creation.
            // Keep it here rather than tying TerrainHandle authoring to a representation constructor.
            session.Room.AddObject(new TerrainCurve(session.Room));
        }
    }

    private static bool ContainsReference(System.Collections.Generic.List<PlacedObject> values, PlacedObject target)
    {
        for (int i = 0; i < values.Count; i++)
            if (ReferenceEquals(values[i], target)) return true;
        return false;
    }
}
