using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Objects;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Triggers;

public static class TriggerEditorKeys
{
    public const string ActiveFromCycle = "activeFromCycle";
    public const string ActiveToCycle = "activeToCycle";
    public const string DelaySeconds = "delaySeconds";
    public const string FireChance = "fireChance";
    public const string MultiUse = "multiUse";
    public const string Entrance = "entrance";
    public const string Karma = "karma";
    public const string Position = "position";
    public const string Radius = "radius";
    public const string CreatureType = "creatureType";
}

public static class TriggerEventEditorKeys
{
    public const string SongName = "songName";
    public const string Priority = "priority";
    public const string MaxThreatLevel = "maxThreatLevel";
    public const string DroneTolerance = "droneTolerance";
    public const string Volume = "volume";
    public const string FadeInSeconds = "fadeInSeconds";
    public const string Loop = "loop";
    public const string OneSongPerCycle = "oneSongPerCycle";
    public const string StopAtDeath = "stopAtDeath";
    public const string StopAtGate = "stopAtGate";
    public const string RoomsRange = "roomsRange";
    public const string CyclesRest = "cyclesRest";
    public const string StopMode = "stopMode";
    public const string FadeOutSeconds = "fadeOutSeconds";
    public const string AfterEncounter = "afterEncounter";
    public const string OnlyWhenShowingDirection = "onlyWhenShowingDirection";
    public const string FromCycle = "fromCycle";
}

internal static class TriggerEditorActions
{
    internal static void Select(EditorSession session, int index)
    {
        TriggerEditorState state = TriggerEditorStateHub.Get(session);
        if (state == null) return;
        int count = session?.RoomSettings?.triggers?.Count ?? 0;
        state.SelectedIndex = index >= 0 && index < count ? index : -1;
    }

    internal static bool Create(EditorSession session, string typeName)
    {
        if (session?.RoomSettings?.triggers == null || string.IsNullOrEmpty(typeName)) return false;
        if (session.ToolMode != EditorToolMode.Triggers) session.SetToolMode(EditorToolMode.Triggers);
        if (session.Owner?.activePage is not TriggersPage page) return false;

        RoomSettingsStateSnapshot before = RoomSettingsStateSnapshot.Capture(session.RoomSettings);
        int count = session.RoomSettings.triggers.Count;

        // Keep the vanilla public construction path so ordinary Rain World hooks from other
        // mods still participate. Trigger creation itself is not a destructive toggle.
        page.CreateTriggerRep(new EventTrigger.TriggerType(typeName, false));
        if (session.RoomSettings.triggers.Count <= count) return false;

        EventTrigger created = session.RoomSettings.triggers[session.RoomSettings.triggers.Count - 1];
        if (created is SpotTrigger spot)
        {
            RoomCamera camera = session.Owner.game?.cameras != null && session.Owner.game.cameras.Length > 0
                ? session.Owner.game.cameras[0]
                : null;
            spot.pos = (camera?.pos ?? Vector2.zero) + new Vector2(683f, 384f);
            page.Refresh();
        }

        RoomSettingsStateSnapshot after = RoomSettingsStateSnapshot.Capture(session.RoomSettings);
        if (SnapshotHistoryEntry.TryCreate(
                "Create trigger " + typeName,
                before,
                after,
                out SnapshotHistoryEntry entry))
            session.History.Push(entry);

        TriggerEditorStateHub.Get(session).SelectedIndex = session.RoomSettings.triggers.IndexOf(created);
        return true;
    }

    internal static bool Delete(EditorSession session, int index)
    {
        if (!TryGet(session, index, out EventTrigger trigger)) return false;

        RoomSettingsStateSnapshot before = RoomSettingsStateSnapshot.Capture(session.RoomSettings);
        session.RoomSettings.triggers.RemoveAt(index);
        RefreshPage(session);
        RoomSettingsStateSnapshot after = RoomSettingsStateSnapshot.Capture(session.RoomSettings);

        if (SnapshotHistoryEntry.TryCreate(
                "Delete trigger " + (trigger.type?.value ?? string.Empty),
                before,
                after,
                out SnapshotHistoryEntry entry))
            session.History.Push(entry);

        TriggerEditorState state = TriggerEditorStateHub.Get(session);
        if (state != null)
        {
            int count = session.RoomSettings.triggers.Count;
            state.SelectedIndex = count == 0 ? -1 : Math.Min(index, count - 1);
        }
        return true;
    }

    internal static bool SetValue(EditorSession session, int index, string key, EditorPropertyValue value)
    {
        if (!TryGet(session, index, out EventTrigger trigger) || string.IsNullOrEmpty(key)) return false;

        return Mutate(session, "Change trigger " + key, () =>
        {
            switch (key)
            {
                case TriggerEditorKeys.ActiveFromCycle:
                    if (value.Kind != EditorPropertyKind.Integer) return false;
                    int from = Mathf.Clamp(value.Integer, 0, 80);
                    if (trigger.activeToCycle >= 0) from = Math.Min(from, trigger.activeToCycle);
                    trigger.activeFromCycle = from;
                    return true;

                case TriggerEditorKeys.ActiveToCycle:
                    if (value.Kind != EditorPropertyKind.Integer) return false;
                    if (value.Integer < 0)
                    {
                        trigger.activeToCycle = -1;
                        return true;
                    }
                    trigger.activeToCycle = Mathf.Clamp(value.Integer, trigger.activeFromCycle, 79);
                    return true;

                case TriggerEditorKeys.DelaySeconds:
                    if (value.Kind != EditorPropertyKind.Float) return false;
                    trigger.delay = Mathf.RoundToInt(Mathf.Clamp(value.X, 0f, 120f) * 40f);
                    return true;

                case TriggerEditorKeys.FireChance:
                    if (value.Kind != EditorPropertyKind.Float) return false;
                    trigger.fireChance = Mathf.Clamp01(value.X);
                    return true;

                case TriggerEditorKeys.MultiUse:
                    if (value.Kind != EditorPropertyKind.Boolean) return false;
                    trigger.multiUse = value.Boolean;
                    return true;

                case TriggerEditorKeys.Entrance:
                    if (value.Kind != EditorPropertyKind.Integer) return false;
                    int maxEntrance = (session.Room?.abstractRoom?.connections?.Length ?? 0) - 1;
                    trigger.entrance = Mathf.Clamp(value.Integer, -1, Math.Max(-1, maxEntrance));
                    return true;

                case TriggerEditorKeys.Karma:
                    if (value.Kind != EditorPropertyKind.Integer) return false;
                    trigger.karma = Mathf.Clamp(value.Integer, 0, 4);
                    return true;

                case TriggerEditorKeys.Position:
                    if (value.Kind != EditorPropertyKind.Vector2 || trigger is not SpotTrigger spotPosition) return false;
                    spotPosition.pos = new Vector2(value.X, value.Y);
                    return true;

                case TriggerEditorKeys.Radius:
                    if (value.Kind != EditorPropertyKind.Float || trigger is not SpotTrigger spotRadius) return false;
                    float radius = Mathf.Max(0f, value.X);
                    Vector2 direction = spotRadius.radHandlePosition.sqrMagnitude > 0.0001f
                        ? spotRadius.radHandlePosition.normalized
                        : Vector2.up;
                    spotRadius.rad = radius;
                    spotRadius.radHandlePosition = direction * radius;
                    return true;

                case TriggerEditorKeys.CreatureType:
                    if (value.Kind != EditorPropertyKind.String || trigger is not SeeCreatureTrigger see || string.IsNullOrEmpty(value.Text))
                        return false;
                    see.creatureType = new CreatureTemplate.Type(value.Text, false);
                    return true;

                default:
                    return false;
            }
        });
    }

    internal static bool ToggleSlugcat(EditorSession session, int index, string slugcatName)
    {
        if (!TryGet(session, index, out EventTrigger trigger) || trigger.slugcats == null || string.IsNullOrEmpty(slugcatName))
            return false;

        return Mutate(session, "Change trigger slugcats", () =>
        {
            SlugcatStats.Name name = new(slugcatName, false);
            int found = -1;
            for (int i = 0; i < trigger.slugcats.Count; i++)
            {
                if (trigger.slugcats[i] == name)
                {
                    found = i;
                    break;
                }
            }

            if (found >= 0) trigger.slugcats.RemoveAt(found);
            else trigger.slugcats.Add(name);
            trigger.RefreshTimelineList();
            return true;
        });
    }

    internal static bool SetEventType(EditorSession session, int index, string eventTypeName)
    {
        if (!TryGet(session, index, out EventTrigger trigger) || string.IsNullOrEmpty(eventTypeName)) return false;
        if (session.Owner?.activePage is not TriggersPage page) return false;

        return Mutate(session, "Set trigger event " + eventTypeName, () =>
        {
            TriggerPanel panel = FindPanel(page, trigger);
            TriggeredEvent.EventType eventType = new(eventTypeName, false);
            if (panel != null)
            {
                // Reuse vanilla's event factory so built-in defaults (especially multiUse)
                // and ordinary hooks on TriggerPanel.AddEvent remain intact.
                panel.AddEvent(eventType);
                return trigger.tEvent != null;
            }

            trigger.tEvent = CreateEventFallback(eventType);
            ApplyDefaultMultiUse(trigger, eventType);
            return trigger.tEvent != null;
        }, refreshPage: false);
    }

    internal static bool ClearEvent(EditorSession session, int index)
    {
        if (!TryGet(session, index, out EventTrigger trigger) || trigger.tEvent == null) return false;
        return Mutate(session, "Remove trigger event", () =>
        {
            trigger.tEvent = null;
            return true;
        });
    }

    internal static bool SetEventValue(EditorSession session, int index, string key, EditorPropertyValue value)
    {
        if (!TryGet(session, index, out EventTrigger trigger) || trigger.tEvent == null || string.IsNullOrEmpty(key))
            return false;

        return Mutate(session, "Change event " + key, () =>
        {
            if (trigger.tEvent is MusicEvent music)
                return SetMusicEventValue(music, key, value);
            if (trigger.tEvent is StopMusicEvent stop)
                return SetStopMusicEventValue(stop, key, value);
            if (trigger.tEvent is ShowProjectedImageEvent image)
                return SetProjectedImageEventValue(image, key, value);
            return false;
        });
    }

    private static bool SetMusicEventValue(MusicEvent music, string key, EditorPropertyValue value)
    {
        switch (key)
        {
            case TriggerEventEditorKeys.SongName:
                if (value.Kind != EditorPropertyKind.String || string.IsNullOrEmpty(value.Text)) return false;
                music.songName = value.Text;
                return true;
            case TriggerEventEditorKeys.Priority:
                if (value.Kind != EditorPropertyKind.Float) return false;
                music.prio = Mathf.Clamp01(value.X);
                return true;
            case TriggerEventEditorKeys.MaxThreatLevel:
                if (value.Kind != EditorPropertyKind.Float) return false;
                music.maxThreatLevel = Mathf.Clamp01(value.X);
                return true;
            case TriggerEventEditorKeys.DroneTolerance:
                if (value.Kind != EditorPropertyKind.Float) return false;
                music.droneTolerance = Mathf.Clamp01(value.X);
                return true;
            case TriggerEventEditorKeys.Volume:
                if (value.Kind != EditorPropertyKind.Float) return false;
                music.volume = Mathf.Clamp01(value.X);
                return true;
            case TriggerEventEditorKeys.FadeInSeconds:
                if (value.Kind != EditorPropertyKind.Float) return false;
                music.fadeInTime = Mathf.Clamp(value.X, 0f, 15f) * 40f;
                return true;
            case TriggerEventEditorKeys.Loop:
                if (value.Kind != EditorPropertyKind.Boolean) return false;
                music.loop = value.Boolean;
                return true;
            case TriggerEventEditorKeys.OneSongPerCycle:
                if (value.Kind != EditorPropertyKind.Boolean) return false;
                music.oneSongPerCycle = value.Boolean;
                return true;
            case TriggerEventEditorKeys.StopAtDeath:
                if (value.Kind != EditorPropertyKind.Boolean) return false;
                music.stopAtDeath = value.Boolean;
                return true;
            case TriggerEventEditorKeys.StopAtGate:
                if (value.Kind != EditorPropertyKind.Boolean) return false;
                music.stopAtGate = value.Boolean;
                return true;
            case TriggerEventEditorKeys.RoomsRange:
                if (value.Kind != EditorPropertyKind.Integer) return false;
                music.roomsRange = value.Integer < 0 ? -1 : Mathf.Clamp(value.Integer, 0, 39);
                return true;
            case TriggerEventEditorKeys.CyclesRest:
                if (value.Kind != EditorPropertyKind.Integer) return false;
                music.cyclesRest = value.Integer < 0 ? -1 : Mathf.Clamp(value.Integer, 0, 79);
                return true;
            default:
                return false;
        }
    }

    private static bool SetStopMusicEventValue(StopMusicEvent stop, string key, EditorPropertyValue value)
    {
        switch (key)
        {
            case TriggerEventEditorKeys.SongName:
                if (value.Kind != EditorPropertyKind.String || string.IsNullOrEmpty(value.Text)) return false;
                stop.songName = value.Text;
                return true;
            case TriggerEventEditorKeys.Priority:
                if (value.Kind != EditorPropertyKind.Float) return false;
                stop.prio = Mathf.Clamp01(value.X);
                return true;
            case TriggerEventEditorKeys.FadeOutSeconds:
                if (value.Kind != EditorPropertyKind.Float) return false;
                stop.fadeOutTime = Mathf.Clamp(value.X, 0f, 30f) * 40f;
                return true;
            case TriggerEventEditorKeys.StopMode:
                if (value.Kind != EditorPropertyKind.String || string.IsNullOrEmpty(value.Text)) return false;
                stop.type = new StopMusicEvent.Type(value.Text, false);
                return true;
            default:
                return false;
        }
    }

    private static bool SetProjectedImageEventValue(ShowProjectedImageEvent image, string key, EditorPropertyValue value)
    {
        switch (key)
        {
            case TriggerEventEditorKeys.AfterEncounter:
                if (value.Kind != EditorPropertyKind.Boolean) return false;
                image.afterEncounter = value.Boolean;
                return true;
            case TriggerEventEditorKeys.OnlyWhenShowingDirection:
                if (value.Kind != EditorPropertyKind.Boolean) return false;
                image.onlyWhenShowingDirection = value.Boolean;
                return true;
            case TriggerEventEditorKeys.FromCycle:
                if (value.Kind != EditorPropertyKind.Integer) return false;
                image.fromCycle = Mathf.Max(0, value.Integer);
                return true;
            default:
                return false;
        }
    }

    private static TriggerPanel FindPanel(TriggersPage page, EventTrigger trigger)
    {
        if (page?.subNodes == null || trigger == null) return null;
        for (int i = 0; i < page.subNodes.Count; i++)
            if (page.subNodes[i] is TriggerPanel panel && ReferenceEquals(panel.trigger, trigger)) return panel;
        return null;
    }

    private static TriggeredEvent CreateEventFallback(TriggeredEvent.EventType eventType)
    {
        if (eventType == TriggeredEvent.EventType.MusicEvent) return new MusicEvent();
        if (eventType == TriggeredEvent.EventType.StopMusicEvent) return new StopMusicEvent();
        if (eventType == TriggeredEvent.EventType.ShowProjectedImageEvent) return new ShowProjectedImageEvent();
        return new TriggeredEvent(eventType);
    }

    private static void ApplyDefaultMultiUse(EventTrigger trigger, TriggeredEvent.EventType eventType)
    {
        if (trigger == null || eventType == null) return;
        if (eventType == TriggeredEvent.EventType.MusicEvent) trigger.multiUse = false;
        else if (eventType == TriggeredEvent.EventType.StopMusicEvent ||
                 eventType == TriggeredEvent.EventType.ShowProjectedImageEvent) trigger.multiUse = true;
    }

    private static bool Mutate(EditorSession session, string label, Func<bool> mutation, bool refreshPage = true)
    {
        if (session?.RoomSettings == null || mutation == null) return false;
        RoomSettingsStateSnapshot before = RoomSettingsStateSnapshot.Capture(session.RoomSettings);
        if (before == null || !mutation()) return false;
        if (refreshPage) RefreshPage(session);
        RoomSettingsStateSnapshot after = RoomSettingsStateSnapshot.Capture(session.RoomSettings);
        if (SnapshotHistoryEntry.TryCreate(label, before, after, out SnapshotHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    private static bool TryGet(EditorSession session, int index, out EventTrigger trigger)
    {
        trigger = null;
        if (session?.RoomSettings?.triggers == null || index < 0 || index >= session.RoomSettings.triggers.Count)
            return false;
        trigger = session.RoomSettings.triggers[index];
        return trigger != null;
    }

    private static void RefreshPage(EditorSession session)
    {
        try { session?.Owner?.activePage?.Refresh(); }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool trigger refresh failed: " + error.Message);
        }
    }
}
