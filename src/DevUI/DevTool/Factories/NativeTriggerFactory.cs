using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Factories;

/// <summary>
/// Native construction path for trigger models. Registered native providers get first refusal;
/// Rain World's built-ins are created directly, and unknown external ExtEnum values fall back to
/// the legacy page boundary so existing third-party hooks remain available during migration.
/// </summary>
internal static class NativeTriggerFactory
{
    internal static bool TryCreate(
        EditorSession session,
        EventTrigger.TriggerType type,
        out EventTrigger trigger)
    {
        trigger = null;
        if (session?.RoomSettings?.triggers == null || type == null)
            return false;

        if (!NativeAuthoringFactoryRegistry.TryCreateTrigger(session, type, out trigger))
        {
            if (!IsBuiltin(type))
                return TryCreateLegacy(session, type, out trigger);

            trigger = type == EventTrigger.TriggerType.Spot
                ? new SpotTrigger()
                : new EventTrigger(type);
        }

        if (trigger == null) return false;
        if (trigger is SpotTrigger spot && spot.pos == UnityEngine.Vector2.zero)
            spot.pos = NativeFactoryPlacement.WorldCursor(session);

        trigger.panelPosition = NativeFactoryPlacement.LegacyPanelSlot(session.RoomSettings.triggers.Count);
        if (!ContainsReference(session.RoomSettings.triggers, trigger))
            session.RoomSettings.triggers.Add(trigger);
        return true;
    }

    private static bool IsBuiltin(EventTrigger.TriggerType type) =>
        type == EventTrigger.TriggerType.Spot ||
        type == EventTrigger.TriggerType.SeeCreature ||
        type == EventTrigger.TriggerType.PreRegionBump ||
        type == EventTrigger.TriggerType.RegionBump;

    private static bool TryCreateLegacy(
        EditorSession session,
        EventTrigger.TriggerType type,
        out EventTrigger trigger)
    {
        trigger = null;
        if (session?.Owner?.activePage is not TriggersPage page)
            return false;

        int before = session.RoomSettings.triggers.Count;
        page.CreateTriggerRep(type);
        if (session.RoomSettings.triggers.Count <= before)
            return false;

        trigger = session.RoomSettings.triggers[session.RoomSettings.triggers.Count - 1];
        return trigger != null;
    }

    private static bool ContainsReference(System.Collections.Generic.List<EventTrigger> values, EventTrigger target)
    {
        for (int i = 0; i < values.Count; i++)
            if (ReferenceEquals(values[i], target)) return true;
        return false;
    }
}

/// <summary>
/// Native TriggeredEvent factory. Registered providers can supply a first-class model for custom
/// event IDs; Rain World's built-ins are direct constructors, and only unknown unregistered IDs are
/// delegated to TriggerPanel.AddEvent as an explicit Legacy fallback.
/// </summary>
internal static class NativeTriggeredEventFactory
{
    internal static bool TryAssign(
        EditorSession session,
        EventTrigger trigger,
        TriggeredEvent.EventType eventType)
    {
        if (trigger == null || eventType == null)
            return false;

        TriggeredEvent created;
        if (NativeAuthoringFactoryRegistry.TryCreateTriggeredEvent(session, trigger, eventType, out created))
        {
            trigger.tEvent = created;
            return created != null;
        }

        if (!IsBuiltin(eventType))
            return TryAssignLegacy(session, trigger, eventType);

        trigger.tEvent = CreateBuiltin(eventType);
        ApplyDefaultMultiUse(trigger, eventType);
        return trigger.tEvent != null;
    }

    private static TriggeredEvent CreateBuiltin(TriggeredEvent.EventType eventType)
    {
        if (eventType == TriggeredEvent.EventType.MusicEvent)
            return new MusicEvent();
        if (eventType == TriggeredEvent.EventType.StopMusicEvent)
            return new StopMusicEvent();
        if (eventType == TriggeredEvent.EventType.ShowProjectedImageEvent)
            return new ShowProjectedImageEvent();
        return new TriggeredEvent(eventType);
    }

    private static bool IsBuiltin(TriggeredEvent.EventType eventType) =>
        eventType == TriggeredEvent.EventType.MusicEvent ||
        eventType == TriggeredEvent.EventType.StopMusicEvent ||
        eventType == TriggeredEvent.EventType.PoleMimicsSubtleReveal ||
        eventType == TriggeredEvent.EventType.ShowProjectedImageEvent ||
        eventType == TriggeredEvent.EventType.PickUpObjectInstruction ||
        eventType == TriggeredEvent.EventType.RoomSpecificTextMessage ||
        eventType == TriggeredEvent.EventType.BringPlayerGuideToRoom;

    private static void ApplyDefaultMultiUse(
        EventTrigger trigger,
        TriggeredEvent.EventType eventType)
    {
        if (eventType == TriggeredEvent.EventType.MusicEvent)
            trigger.multiUse = false;
        else if (eventType == TriggeredEvent.EventType.StopMusicEvent ||
                 eventType == TriggeredEvent.EventType.ShowProjectedImageEvent)
            trigger.multiUse = true;
    }

    private static bool TryAssignLegacy(
        EditorSession session,
        EventTrigger trigger,
        TriggeredEvent.EventType eventType)
    {
        if (session?.Owner?.activePage is not TriggersPage page || page.subNodes == null)
            return false;

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not TriggerPanel panel || !ReferenceEquals(panel.trigger, trigger))
                continue;

            panel.AddEvent(eventType);
            return trigger.tEvent != null;
        }

        return false;
    }
}
