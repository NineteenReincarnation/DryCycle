using DryCycle.DevUI.DevTool.Dialog;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.Relationships;
using DryCycle.DevUI.DevTool.Room;
using DryCycle.DevUI.DevTool.Sound;
using DryCycle.DevUI.DevTool.Triggers;

namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Single ownership point for DevTool backend subsystem coordination.
///
/// DevToolRuntime owns hooks and frame order. Feature modules own their local queues, presentation
/// caches and workspace state. This coordinator is the only Core layer that is allowed to fan out
/// across every feature module for command processing and lifecycle reset.
///
/// Extension scopes are deliberately absent here: DevTool Extension API lifetime belongs to the
/// external mod that registered the scope, not to the editor UI/runtime lifetime.
/// </summary>
internal static class DevToolSubsystemCoordinator
{
    internal static void ProcessPendingCommands(EditorSession session)
    {
        EditorUiCommandQueue.Process(session);
        RoomEditorCommandQueue.Process(session);
        SoundEditorCommandQueue.Process(session);
        TriggerEditorCommandQueue.Process(session);
        MapEditorCommandQueue.Process(session);
        DialogEditorCommandQueue.Process(session);
        RelationshipEditorCommandQueue.Process(session);
    }

    internal static void ClearDetailPresentations()
    {
        RoomEditorPresentationHub.Clear();
        SoundEditorPresentationHub.Clear();
        TriggerEditorPresentationHub.Clear();
        MapEditorPresentationHub.Clear();
        DialogEditorPresentationHub.Clear();
        RelationshipEditorPresentationHub.Clear();
    }

    /// <summary>
    /// Clears all backend state owned by a DevTool runtime activation.
    /// This is intentionally idempotent and does not unregister public extension scopes.
    /// </summary>
    internal static void ResetRuntimeState()
    {
        ClearCommandQueues();
        EditorPresentationHub.Clear();
        ClearDetailPresentations();
        ResetWorkspaceState();
        ResetPresentationHints();
        EditorRevisionHub.Reset();
        DevToolSessionHub.Reset();
        DevToolPerformanceMonitor.SetEnabled(false);
        DevToolPerformanceMonitor.Reset();
    }

    private static void ClearCommandQueues()
    {
        EditorUiCommandQueue.Clear();
        RoomEditorCommandQueue.Clear();
        SoundEditorCommandQueue.Clear();
        TriggerEditorCommandQueue.Clear();
        MapEditorCommandQueue.Clear();
        DialogEditorCommandQueue.Clear();
        RelationshipEditorCommandQueue.Clear();
    }

    private static void ResetWorkspaceState()
    {
        SoundEditorStateHub.Reset();
        TriggerEditorStateHub.Reset();
        MapEditorStateHub.Reset();
        DialogEditorStateHub.Reset();
        RelationshipEditorStateHub.Reset();
    }

    private static void ResetPresentationHints()
    {
        RelationshipPresentationChangeHintHub.Reset();
        ObjectPresentationChangeHintHub.Reset();
        RoomPresentationChangeHintHub.Reset();
        SoundPresentationChangeHintHub.Reset();
        TriggerPresentationChangeHintHub.Reset();
    }
}
