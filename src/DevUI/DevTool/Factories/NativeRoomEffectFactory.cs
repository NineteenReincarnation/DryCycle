using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Factories;

/// <summary>
/// Native RoomEffect construction. Registered providers get first refusal, built-in Rain World/DLC/
/// Watcher effects are constructed directly, and only unknown unregistered ExtEnum IDs use the
/// legacy RoomSettingsPage.Signal(Create) boundary.
/// </summary>
internal static class NativeRoomEffectFactory
{
    internal static bool TryCreate(
        EditorSession session,
        RoomSettings.RoomEffect.Type type,
        out RoomSettings.RoomEffect created)
    {
        created = null;
        if (session?.RoomSettings?.effects == null || type == null)
            return false;

        RoomSettings settings = session.RoomSettings;
        for (int i = 0; i < settings.effects.Count; i++)
        {
            RoomSettings.RoomEffect existing = settings.effects[i];
            if (existing != null && !existing.inherited && existing.type == type)
                return false;
        }

        if (!NativeAuthoringFactoryRegistry.TryCreateRoomEffect(session, type, out created))
        {
            if (!GameDefinedExtEnumCatalog.Contains(typeof(RoomSettings.RoomEffect.Type), type.value))
                return TryCreateLegacy(session, type, out created);

            if (ModManager.Watcher && string.Equals(type.value, "Ripple Settings", System.StringComparison.Ordinal))
            {
                string roomName = session.Room?.abstractRoom?.name ?? string.Empty;
                created = new Watcher.RippleEffectSettings(type, 0f, roomName);
            }
            else
            {
                created = new RoomSettings.RoomEffect(
                    type,
                    RoomSettings.RoomEffect.GetSliderDefault(type, 0),
                    inherited: false);
            }
        }

        if (created == null) return false;
        created.panelPosition = NativeFactoryPlacement.LegacyPanelSlot(settings.effects.Count);

        bool overWrite = false;
        for (int i = settings.effects.Count - 1; i >= 0; i--)
        {
            RoomSettings.RoomEffect existing = settings.effects[i];
            if (existing?.type != type || ReferenceEquals(existing, created)) continue;
            settings.effects.RemoveAt(i);
            overWrite = true;
        }

        created.overWrite |= overWrite;
        if (!ContainsReference(settings.effects, created))
            settings.effects.Add(created);
        return true;
    }

    private static bool TryCreateLegacy(
        EditorSession session,
        RoomSettings.RoomEffect.Type type,
        out RoomSettings.RoomEffect created)
    {
        created = null;
        if (session?.Owner?.activePage is not RoomSettingsPage page)
            return false;

        int beforeCount = session.RoomSettings.effects.Count;
        page.Signal(DevUISignalType.Create, page, type.value);

        for (int i = session.RoomSettings.effects.Count - 1; i >= 0; i--)
        {
            RoomSettings.RoomEffect candidate = session.RoomSettings.effects[i];
            if (candidate != null && !candidate.inherited && candidate.type == type)
            {
                created = candidate;
                return true;
            }
        }

        return session.RoomSettings.effects.Count > beforeCount && created != null;
    }

    private static bool ContainsReference(System.Collections.Generic.List<RoomSettings.RoomEffect> values, RoomSettings.RoomEffect target)
    {
        for (int i = 0; i < values.Count; i++)
            if (ReferenceEquals(values[i], target)) return true;
        return false;
    }
}
