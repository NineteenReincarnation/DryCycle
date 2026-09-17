using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Factories;

/// <summary>
/// Native model factory for Rain World/DLC/Watcher PlacedObject types. Game-defined ExtEnum IDs are
/// constructed directly through PlacedObject.GenerateEmptyData; external IDs are delegated to an
/// isolated ObjectsPage fallback so third-party CreateObjRep hooks still work until that object type
/// registers a native factory.
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

        if (!GameDefinedExtEnumCatalog.Contains(typeof(PlacedObject.Type), type.value))
            return TryCreateLegacy(session, type, worldPosition, out created);

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

        if (created?.type == PlacedObject.Type.TerrainHandle && session.Room?.terrain == null)
        {
            // This is the one model/runtime side effect vanilla CreateObjRep performs at creation.
            // Keep it here rather than tying TerrainHandle authoring to a representation constructor.
            session.Room.AddObject(new TerrainCurve(session.Room));
        }
    }

    private static bool TryCreateLegacy(
        EditorSession session,
        PlacedObject.Type type,
        Vector2 worldPosition,
        out PlacedObject created)
    {
        created = null;
        if (session?.Owner == null)
            return false;

        ObjectsPage page = session.Owner.activePage as ObjectsPage;
        bool temporary = page == null;
        if (temporary)
            page = new ObjectsPage(session.Owner, "DevTool_LegacyFactorySandbox", null, "Objects");

        try
        {
            int before = session.RoomSettings.placedObjects.Count;
            page.CreateObjRep(type, null);
            if (session.RoomSettings.placedObjects.Count <= before)
                return false;

            created = session.RoomSettings.placedObjects[session.RoomSettings.placedObjects.Count - 1];
            if (created == null)
                return false;

            created.pos = worldPosition;
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool legacy object factory fallback failed for '" + (type.value ?? string.Empty) + "': " + error.Message);
            created = null;
            return false;
        }
        finally
        {
            if (temporary)
            {
                try { page.ClearSprites(); }
                catch { }
            }
        }
    }
}
