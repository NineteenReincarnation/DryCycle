using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Factories;

/// <summary>
/// Native construction path for Rain World's built-in trigger model. Built-in trigger creation no
/// longer needs TriggersPage/TriggerPanel to exist. Unknown ExtEnum values deliberately fall back to
/// the legacy page boundary so third-party CreateTriggerRep hooks remain available until they opt in
/// to the native extension API.
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

        if (!IsBuiltin(type))
            return TryCreateLegacy(session, type, out trigger);

        trigger = type == EventTrigger.TriggerType.Spot
            ? new SpotTrigger()
            : new EventTrigger(type);

        if (trigger is SpotTrigger spot)
            spot.pos = NativeFactoryPlacement.WorldCursor(session);

        trigger.panelPosition = NativeFactoryPlacement.LegacyPanelSlot(session.RoomSettings.triggers.Count);
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
}

/// <summary>
/// Native TriggeredEvent factory. The seven Rain World event IDs are pure model constructors; only
/// unknown third-party IDs are delegated to TriggerPanel.AddEvent so an existing mod hook can still
/// supply custom event state.
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
