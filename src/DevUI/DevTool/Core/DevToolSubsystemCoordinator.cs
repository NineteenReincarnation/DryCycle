using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Dialog;
using DryCycle.DevUI.DevTool.Gizmos;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.Relationships;
using DryCycle.DevUI.DevTool.Room;
using DryCycle.DevUI.DevTool.Sound;
using DryCycle.DevUI.DevTool.Triggers;
using DryCycle.DevUI.DevTool.World;

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
        // Import legacy selection before native queues run. A native Select command in this same
        // frame therefore remains authoritative instead of being overwritten by an older dragged
        // Panel after command processing.
        LegacySoundTriggerSelectionBridge.Synchronize(session);

        EditorUiCommandQueue.Process(session);
        RoomEditorCommandQueue.Process(session);
        SoundEditorCommandQueue.Process(session);
        TriggerEditorCommandQueue.Process(session);
        NativeGizmoCommandQueue.Process(session);
        NativeObjectGizmoEditCommandQueue.Process(session);
        MapEditorCommandQueue.Process(session);
        if (PlayerMapActivityGate.ShouldProcess)
        {
            MapEditorPresentationHub.Publish(session);
            PlayerMapCommandQueue.Process(session);
        }
        DialogEditorCommandQueue.Process(session);
        RelationshipEditorCommandQueue.Process(session);

        SoundActivationPipeline.Step(session);

        // Native resource discovery never writes into legacy page metadata. Only explicit
        // Vanilla/Legacy presentation receives compatibility projections of completed headless
        // catalogues.
        LegacySoundPageHydrator.Step(session);
        LegacyTriggerPageHydrator.Step(session);

        // Native scene-space tools consume only this detached camera snapshot. Publish after command
        // processing so camera/tool changes observed this frame are visible to RWImGui immediately.
        EditorViewportPresentationHub.Publish(session);

        UniversalDevUiCommandQueue.Process(session);

        if (DevUiDiagnosticsPolicy.Enabled && session?.Owner != null)
            DevUiDiagnosticsPublisher.Publish(session.Owner);
    }

    /// <summary>
    /// History snapshots can restore collection membership without going back through the original
    /// authoring action. Reconcile real runtime state once after a successful Undo/Redo so Sound
    /// players follow the restored AmbientSound model while scalar edits continue to update live by
    /// reference without any rebuild.
    /// </summary>
    internal static void ReconcileRuntimeAfterHistoryRestore(EditorSession session)
    {
        NativeSoundRuntimeReconciler.Reconcile(session);
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
    /// This is intentionally idempotent and does not unregister public extension/native factory
    /// scopes: provider lifetime belongs to the owning mod/process, not a DevUI session.
    /// </summary>
    internal static void ResetRuntimeState()
    {
        ClearCommandQueues();
        EditorContinuousTransactionHub.Reset();
        LegacyObjectSandbox.Reset();
        NativeObjectRuntimeReconciler.ResetRuntimeState();
        LegacySoundPageHydrator.Reset();
        LegacyTriggerPageHydrator.Reset();
        EditorViewportPresentationHub.Clear();
        EditorPresentationHub.Clear();
        ClearDetailPresentations();
        ResetWorkspaceState();
        ResetPresentationHints();
        WorldRoomAttractionRegistry.ResetIfClean();
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
        NativeGizmoCommandQueue.Clear();
        NativeObjectGizmoEditCommandQueue.Clear();
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
        TriggerSongCatalog.ResetRuntimeState();
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