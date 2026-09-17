using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;
using DryCycle.DevUI.DevTool.Sound;
using DryCycle.DevUI.DevTool.Triggers;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Imports selection only from a genuinely live legacy Sound/Trigger backend. Native presentation
/// and authoring never depend on DevUINode/Panel types; this compatibility bridge is the sole
/// translator when Vanilla, a legacy transaction or an opaque third-party backend still drives one
/// of those nodes.
/// </summary>
internal static class LegacySoundTriggerSelectionBridge
{
    internal static void Synchronize(EditorSession session)
    {
        if (session?.Owner == null || !LegacySelectionCanWrite(session))
            return;

        switch (session.ToolMode)
        {
            case EditorToolMode.Sound:
                SynchronizeSound(session);
                break;
            case EditorToolMode.Triggers:
                SynchronizeTrigger(session);
                break;
        }
    }

    private static bool LegacySelectionCanWrite(EditorSession session)
    {
        if (!EditorInputRouter.FrontendAttached || EditorUiModeState.UseVanilla || session.LegacyUiVisible)
            return true;
        if (session.LegacyTransactions.HasPendingTransaction)
            return true;

        Page page = session.Owner?.activePage;
        return page != null && LegacyDevUiQuiescenceController.HasExternalCompatibilityNodes(page);
    }

    private static void SynchronizeSound(EditorSession session)
    {
        if (session.RoomSettings?.ambientSounds == null) return;

        DevUINode node = session.Owner.draggedNode;
        if (node == null && session.Owner.activePage is SoundPage page)
            node = page.draggedObject;

        while (node != null)
        {
            if (node is AmbientSoundPanel panel && panel.sound != null)
            {
                int index = session.RoomSettings.ambientSounds.IndexOf(panel.sound);
                if (index >= 0)
                    SoundEditorStateHub.Get(session)?.SetSelectedIndex(index);
                return;
            }
            node = node.parentNode;
        }
    }

    private static void SynchronizeTrigger(EditorSession session)
    {
        if (session.RoomSettings?.triggers == null) return;

        DevUINode node = session.Owner.draggedNode;
        if (node == null && session.Owner.activePage is TriggersPage page)
            node = page.draggedObject;

        while (node != null)
        {
            if (node is TriggerPanel panel && panel.trigger != null)
            {
                int index = session.RoomSettings.triggers.IndexOf(panel.trigger);
                if (index >= 0)
                    TriggerEditorStateHub.Get(session)?.SetSelectedIndex(index);
                return;
            }
            node = node.parentNode;
        }
    }
}
