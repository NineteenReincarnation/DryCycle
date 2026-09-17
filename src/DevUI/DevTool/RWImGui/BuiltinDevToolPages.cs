using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Dialog;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Relationships;
using DryCycle.DevUI.DevTool.Room;
using DryCycle.DevUI.DevTool.Sound;
using DryCycle.DevUI.DevTool.Triggers;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal sealed class RoomDevToolPage : DevToolFrontendPageBase
{
    public override string Id => "room";
    public override EditorToolMode Mode => EditorToolMode.Room;
    public override int NavigationOrder => 100;
    public override string NavigationLabel => DevToolUiSettings.T("房间", "Room");
    public override string NavigationTooltip => DevToolUiSettings.T("房间设置", "Room settings");
    public override string LegacyFallbackTooltip => DevToolUiSettings.T(
        "用于尚未迁移的模板、地形或自定义房间设置控件。",
        "Fallback for template, terrain or custom RoomSettings controls not migrated yet.");

    public override void DrawBrowser(EditorPresentationSnapshot snapshot) =>
        RoomSettingsView.DrawBrowser(RoomEditorPresentationHub.Current);

    public override void DrawInspector(EditorPresentationSnapshot snapshot) =>
        RoomSettingsView.DrawInspector(RoomEditorPresentationHub.Current);

    protected override void OnReset() => RoomSettingsView.ResetRetainedState();
}

internal sealed class ObjectsDevToolPage : DevToolFrontendPageBase
{
    public override string Id => "objects";
    public override EditorToolMode Mode => EditorToolMode.Objects;
    public override int NavigationOrder => 200;
    public override string NavigationLabel => DevToolUiSettings.T("物件", "Objects");
    public override string NavigationTooltip => DevToolUiSettings.T("物件", "Objects");
    public override bool SupportsSceneSurface => true;
    public override bool SupportsPlacementInput => true;

    public override bool SuppressInspector(EditorPresentationSnapshot snapshot) =>
        snapshot.Inspector?.HasSelection != true;

    public override void DrawBackground(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        NativeSpatialGizmoView.DrawObjects(snapshot, display);
        NativeObjectGeometryGizmoView.Draw(snapshot, display);
    }

    public override void DrawBrowser(EditorPresentationSnapshot snapshot) =>
        ObjectExplorerView.Draw(snapshot);

    public override void DrawInspector(EditorPresentationSnapshot snapshot) =>
        ObjectInspectorView.Draw(snapshot.Inspector);

    public override void DrawSceneWorkspace(EditorPresentationSnapshot snapshot) =>
        ObjectSceneWorkspaceView.Draw(snapshot);

    protected override DevToolPageStatusState BuildSessionStatusState(EditorPresentationSnapshot snapshot) =>
        new(
            countA: snapshot.SceneObjects?.Length ?? 0,
            countB: snapshot.Inspector?.SelectionCount ?? 0,
            flag: snapshot.PlacementActive,
            textA: snapshot.PlacementType);

    protected override string FormatSessionStatus(DevToolPageStatusState state) =>
        DevToolUiSettings.T("物件 ", "Objects ") + state.CountA +
        DevToolUiSettings.T(" · 已选 ", " · Selected ") + state.CountB +
        (state.Flag ? DevToolUiSettings.T(" · 放置 ", " · Placing ") + state.TextA : string.Empty);

    protected override void OnReset()
    {
        NativeSpatialGizmoView.ResetRetainedState();
        NativeObjectGeometryGizmoView.ResetRetainedState();
        ObjectExplorerView.ResetRetainedState();
        ObjectSceneWorkspaceView.ResetRetainedState();
        ObjectInspectorView.ResetRetainedState();
    }
}

internal sealed class SoundDevToolPage : DevToolFrontendPageBase
{
    public override string Id => "sound";
    public override EditorToolMode Mode => EditorToolMode.Sound;
    public override int NavigationOrder => 300;
    public override string NavigationLabel => DevToolUiSettings.T("声音", "Sound");
    public override string NavigationTooltip => DevToolUiSettings.T("声音", "Sound");
    public override bool SupportsSceneSurface => true;
    public override string LegacyFallbackTooltip => DevToolUiSettings.T(
        "用于未迁移的自定义声音页面控件。",
        "Fallback for custom SoundPage controls or mod-added sound tooling not migrated yet.");

    public override void DrawBackground(EditorPresentationSnapshot snapshot, Num.Vector2 display) =>
        NativeSpatialGizmoView.DrawSound(SoundEditorPresentationHub.Current, display);

    public override void DrawBrowser(EditorPresentationSnapshot snapshot) =>
        SoundEditorView.DrawBrowser(SoundEditorPresentationHub.Current);

    public override void DrawInspector(EditorPresentationSnapshot snapshot) =>
        SoundEditorView.DrawInspector(SoundEditorPresentationHub.Current);

    public override void DrawSceneWorkspace(EditorPresentationSnapshot snapshot) =>
        SoundEditorView.DrawSceneWorkspace(SoundEditorPresentationHub.Current);

    protected override DevToolPageStatusState BuildSessionStatusState(EditorPresentationSnapshot snapshot) =>
        new(countA: SoundEditorPresentationHub.Current.Sounds?.Length ?? 0);

    protected override string FormatSessionStatus(DevToolPageStatusState state) =>
        DevToolUiSettings.T("声音 ", "Sounds ") + state.CountA;

    protected override void OnReset()
    {
        NativeSpatialGizmoView.ResetRetainedState();
        SoundEditorView.ResetRetainedState();
        SoundLibraryGroupsView.ResetRetainedState();
    }
}

internal sealed class TriggersDevToolPage : DevToolFrontendPageBase
{
    public override string Id => "triggers";
    public override EditorToolMode Mode => EditorToolMode.Triggers;
    public override int NavigationOrder => 400;
    public override string NavigationLabel => DevToolUiSettings.T("触发器", "Triggers");
    public override string NavigationTooltip => DevToolUiSettings.T("触发器", "Triggers");
    public override bool SupportsSceneSurface => true;
    public override string LegacyFallbackTooltip => DevToolUiSettings.T(
        "用于新检查器无法表达的自定义触发器/事件控件。",
        "Fallback for custom Trigger/TriggeredEvent controls not represented by the native inspector.");

    public override void DrawBackground(EditorPresentationSnapshot snapshot, Num.Vector2 display) =>
        NativeSpatialGizmoView.DrawTriggers(TriggerEditorPresentationHub.Current, display);

    public override void DrawBrowser(EditorPresentationSnapshot snapshot) =>
        TriggerEditorView.DrawBrowser(TriggerEditorPresentationHub.Current);

    public override void DrawInspector(EditorPresentationSnapshot snapshot) =>
        TriggerEditorView.DrawInspector(TriggerEditorPresentationHub.Current);

    public override void DrawSceneWorkspace(EditorPresentationSnapshot snapshot) =>
        TriggerEditorView.DrawSceneWorkspace(TriggerEditorPresentationHub.Current);

    protected override DevToolPageStatusState BuildSessionStatusState(EditorPresentationSnapshot snapshot) =>
        new(countA: TriggerEditorPresentationHub.Current.Triggers?.Length ?? 0);

    protected override string FormatSessionStatus(DevToolPageStatusState state) =>
        DevToolUiSettings.T("触发器 ", "Triggers ") + state.CountA;

    protected override void OnReset()
    {
        NativeSpatialGizmoView.ResetRetainedState();
        TriggerEditorView.ResetRetainedState();
    }
}

internal sealed class MapDevToolPage : DevToolFrontendPageBase
{
    public override string Id => "map";
    public override EditorToolMode Mode => EditorToolMode.Map;
    public override int NavigationOrder => 500;
    public override string NavigationLabel => DevToolUiSettings.T("地图", "Map");
    public override string NavigationTooltip => DevToolUiSettings.T("地图", "Map");
    public override bool UsesDedicatedWorkspace => true;

    public override void DrawBackground(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        if (!snapshot.FocusMode) return;
        DevToolWorkspaceLayout.GetCentralRect(display, out Num.Vector2 position, out Num.Vector2 size);
        MapEditorView.DrawCanvas(MapEditorPresentationHub.Current, position, size);
    }

    public override void DrawBrowser(EditorPresentationSnapshot snapshot) =>
        MapEditorView.DrawBrowser(MapEditorPresentationHub.Current);

    public override void DrawInspector(EditorPresentationSnapshot snapshot) =>
        MapEditorView.DrawInspector(MapEditorPresentationHub.Current);

    public override void DrawWorkspace(EditorPresentationSnapshot snapshot, Num.Vector2 display) =>
        WorldWorkspaceView.Draw(snapshot, display);

    protected override DevToolPageStatusState BuildSessionStatusState(EditorPresentationSnapshot snapshot)
    {
        EditorMapPresentationSnapshot map = MapEditorPresentationHub.Current;
        return new(countA: map.Rooms?.Length ?? 0, textA: map.RegionName);
    }

    protected override string FormatSessionStatus(DevToolPageStatusState state) =>
        state.CountA + DevToolUiSettings.T(" 个房间 · ", " rooms · ") + state.TextA;

    protected override void OnReset()
    {
        MapEditorView.ResetRetainedState();
        WorldWorkspaceView.ResetRetainedState();
    }
}

internal sealed class DialogDevToolPage : DevToolFrontendPageBase
{
    public override string Id => "dialog";
    public override EditorToolMode Mode => EditorToolMode.Dialog;
    public override int NavigationOrder => 600;
    public override string NavigationLabel => DevToolUiSettings.T("对话", "Dialog");
    public override string NavigationTooltip => DevToolUiSettings.T("对话", "Dialog");

    public override void DrawBackground(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        DevToolWorkspaceLayout.GetCentralRect(display, out Num.Vector2 position, out Num.Vector2 size);
        DialogEditorView.DrawPreview(DialogEditorPresentationHub.Current, position, size);
    }

    public override void DrawBrowser(EditorPresentationSnapshot snapshot) =>
        DialogEditorView.DrawBrowser(DialogEditorPresentationHub.Current);

    public override void DrawInspector(EditorPresentationSnapshot snapshot) =>
        DialogEditorView.DrawInspector(DialogEditorPresentationHub.Current);

    protected override DevToolPageStatusState BuildSessionStatusState(EditorPresentationSnapshot snapshot)
    {
        EditorDialogPresentationSnapshot dialog = DialogEditorPresentationHub.Current;
        return new(countA: dialog.Events?.Length ?? 0, textA: dialog.SelectedFileName);
    }

    protected override string FormatSessionStatus(DevToolPageStatusState state) =>
        state.TextA + " · " + state.CountA + DevToolUiSettings.T(" 个事件", " events");

    protected override void OnReset() => DialogEditorView.ResetRetainedState();
}

internal sealed class RelationshipsDevToolPage : DevToolFrontendPageBase
{
    public override string Id => "relationships";
    public override EditorToolMode Mode => EditorToolMode.Relationships;
    public override int NavigationOrder => 700;
    public override string NavigationLabel => DevToolUiSettings.T("关系", "Relationships");
    public override string NavigationTooltip => DevToolUiSettings.T("关系", "Relationships");
    public override string LegacyFallbackTooltip => DevToolUiSettings.T(
        "用于矩阵编辑器尚未表达的关系页面扩展。",
        "Fallback for custom RelationshipPage extensions not represented by the matrix editor.");

    public override void DrawBackground(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        DevToolWorkspaceLayout.GetCentralRect(display, out Num.Vector2 position, out Num.Vector2 size);
        RelationshipEditorView.DrawMatrix(RelationshipEditorPresentationHub.Current, position, size);
    }

    public override void DrawBrowser(EditorPresentationSnapshot snapshot) =>
        RelationshipEditorView.DrawBrowser(RelationshipEditorPresentationHub.Current);

    public override void DrawInspector(EditorPresentationSnapshot snapshot) =>
        RelationshipEditorView.DrawInspector(RelationshipEditorPresentationHub.Current);

    protected override DevToolPageStatusState BuildSessionStatusState(EditorPresentationSnapshot snapshot) =>
        new(textA: RelationshipEditorPresentationHub.Current.PrimaryCreature);

    protected override string FormatSessionStatus(DevToolPageStatusState state) =>
        DevToolUiSettings.T("主体 ", "Primary ") + state.TextA;

    protected override void OnReset() => RelationshipEditorView.ResetRetainedState();
}