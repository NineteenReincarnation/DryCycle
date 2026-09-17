using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Factories;

/// <summary>
/// Creates Rain World/DLC/Watcher RoomEffect models without routing through RoomSettingsPage.Signal.
/// ExtEnum IDs whose declaration lives outside Assembly-CSharp remain on the legacy signal boundary,
/// preserving third-party factories until they receive an explicit native registration surface.
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

        if (!GameDefinedExtEnumCatalog.Contains(typeof(RoomSettings.RoomEffect.Type), type.value))
            return TryCreateLegacy(session, type, out created);

        RoomSettings settings = session.RoomSettings;
        for (int i = 0; i < settings.effects.Count; i++)
        {
            RoomSettings.RoomEffect existing = settings.effects[i];
            if (existing != null && !existing.inherited && existing.type == type)
                return false;
        }

        if (ModManager.Watcher && string.Equals(type.value, "Ripple Settings", System.StringComparison.Ordinal))
        {
            string roomName = session.Room?.abstractRoom?.name ?? string.Empty;
            created = new Watcher.RippleEffectSettings(type, 0f, roomName)
            {
                panelPosition = NativeFactoryPlacement.LegacyPanelSlot(settings.effects.Count)
            };
            settings.effects.Add(created);
            return true;
        }

        created = new RoomSettings.RoomEffect(
            type,
            RoomSettings.RoomEffect.GetSliderDefault(type, 0),
            inherited: false)
        {
            panelPosition = NativeFactoryPlacement.LegacyPanelSlot(settings.effects.Count)
        };

        bool overWrite = false;
        for (int i = settings.effects.Count - 1; i >= 0; i--)
        {
            RoomSettings.RoomEffect existing = settings.effects[i];
            if (existing?.type != type) continue;
            settings.effects.RemoveAt(i);
            overWrite = true;
        }

        created.overWrite = overWrite;
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

        // Legacy custom factories are allowed to replace inherited entries rather than simply append,
        // so identify the resulting local effect by semantic type instead of relying on count alone.
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
}
