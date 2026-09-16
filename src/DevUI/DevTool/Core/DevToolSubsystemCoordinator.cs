using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Dialog;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
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
        if (PlayerMapActivityGate.ShouldProcess)
        {
            // WorldTopology commands are consumed by MapEditorCommandQueue above. Publish their
            // resulting exact node-to-node graph before a Player Map Build/Render command uses it,
            // so "edit connection + render" in one UI frame cannot see the previous topology.
            MapEditorPresentationHub.Publish(session);
            PlayerMapCommandQueue.Process(session);
        }
        DialogEditorCommandQueue.Process(session);
        RelationshipEditorCommandQueue.Process(session);

        // Sound cold-start work has one backend owner. It runs after commands so a SetToolMode or
        // explicit group refresh issued this frame is visible immediately, but before presentation
        // publication so completed snapshots can be consumed in the same frame.
        SoundActivationPipeline.Step(session);

        // The universal compatibility queue follows the same backend command phase as native
        // workspaces. Presentation getters must never execute mutations as a side effect of Draw.
        UniversalDevUiCommandQueue.Process(session);

        // Compatibility diagnostics are explicitly opt-in and have exactly one publication owner.
        // The publisher performs migration audit, mirror capture and downstream detached audit
        // snapshots in a defined order. Production editor frames pay none of this reflection/type-
        // inventory cost when diagnostics are disabled.
        if (DevUiDiagnosticsPolicy.Enabled && session?.Owner != null)
            DevUiDiagnosticsPublisher.Publish(session.Owner);
    }

    internal static void ClearDetailPresentations()
    {
        RoomEditorPresentationHub.Clear();
        SoundEditorPresentationHub.Clear();
        TriggerEditorPresentationHub.Clear();
        MapEditorPresentationHub.Clear();
        DialogEditorPresentationHub.Clear();
        RelationshipEditorPresentationHub.Clear();
        UniversalDevUiPresentationHub.Clear();
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
        PlayerMapCommandQueue.Clear();
        DialogEditorCommandQueue.Clear();
        RelationshipEditorCommandQueue.Clear();
        UniversalDevUiCommandQueue.Clear();
    }

    private static void ResetWorkspaceState()
    {
        PlayerMapActivityGate.Reset();
        SoundActivationPipeline.Reset();
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
