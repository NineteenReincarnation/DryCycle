#!/usr/bin/env bash
set -euo pipefail

frontend="src/DevUI/DevTool/RWImGui"
overlay="$frontend/DevToolOverlay.cs"
control_center="$frontend/ControlCenterWindow.cs"
scene_workspace="$frontend/SceneWorkspaceWindow.cs"
scene_placement="$frontend/ScenePlacementWindow.cs"
page_contract="$frontend/IDevToolPageView.cs"
builtin_pages="$frontend/BuiltinDevToolPages.cs"
registry="$frontend/DevToolPageViewRegistry.cs"
lifecycle="$frontend/DevToolRetainedViewLifecyclePlugin.cs"

required_files=(
  "$overlay"
  "$control_center"
  "$scene_workspace"
  "$scene_placement"
  "$page_contract"
  "$builtin_pages"
  "$registry"
  "$lifecycle"
)
for file in "${required_files[@]}"; do
  if [[ ! -f "$file" ]]; then
    echo "DevTool page-view boundary file is missing: $file" >&2
    exit 1
  fi
done

# Shared chrome must dispatch through IDevToolPageView instead of importing feature editors or
# reading their PresentationHub snapshots directly. BuiltinDevToolPages is the deliberate composition
# root where concrete page implementations are allowed to meet the common contract.
shared_feature_hits="$(
  grep -nE \
    '(RoomSettingsView|ObjectExplorerView|ObjectSceneWorkspaceView|ObjectInspectorView|SoundEditorView|TriggerEditorView|MapEditorView|WorldWorkspaceView|DialogEditorView|RelationshipEditorView|RoomEditorPresentationHub|SoundEditorPresentationHub|TriggerEditorPresentationHub|MapEditorPresentationHub|DialogEditorPresentationHub|RelationshipEditorPresentationHub)' \
    "$overlay" "$control_center" "$scene_workspace" "$scene_placement" || true
)"
if [[ -n "$shared_feature_hits" ]]; then
  echo "Shared RWImGui chrome learned a concrete page/view or page PresentationHub:" >&2
  echo "$shared_feature_hits" >&2
  exit 1
fi

# Shared chrome must not branch on concrete normal ToolMode values. Page identity and capabilities
# are registered once on the page object; adding a page must not require editing the chrome switch.
shared_mode_hits="$(
  grep -nE \
    'EditorToolMode\.(Room|Objects|Sound|Triggers|Map|Dialog|Relationships)' \
    "$overlay" "$control_center" "$scene_workspace" "$scene_placement" || true
)"
if [[ -n "$shared_mode_hits" ]]; then
  echo "Shared RWImGui chrome contains a concrete built-in ToolMode branch:" >&2
  echo "$shared_mode_hits" >&2
  exit 1
fi

# Page-owned metadata/capabilities that the chrome depends on must stay in the common contract.
required_page_contract_symbols=(
  'int NavigationOrder { get; }'
  'string NavigationLabel { get; }'
  'string NavigationTooltip { get; }'
  'bool SupportsSceneSurface { get; }'
  'bool SupportsPlacementInput { get; }'
  'string GetSessionStatus(EditorPresentationSnapshot snapshot);'
  'void DrawBrowser(EditorPresentationSnapshot snapshot);'
  'void DrawInspector(EditorPresentationSnapshot snapshot);'
  'void DrawSceneWorkspace(EditorPresentationSnapshot snapshot);'
)
for symbol in "${required_page_contract_symbols[@]}"; do
  if ! grep -Fq "$symbol" "$page_contract"; then
    echo "IDevToolPageView contract is missing '$symbol'." >&2
    exit 1
  fi
done

# Objects currently owns room-click placement. The declaration belongs to the page composition root,
# never to DevToolOverlay. This check also catches an accidental loss of the capability after refactors.
if ! grep -Fq 'public override bool SupportsPlacementInput => true;' "$builtin_pages"; then
  echo "Objects page no longer declares the placement-input capability." >&2
  exit 1
fi
if ! grep -Fq 'page?.SupportsPlacementInput != true' "$overlay"; then
  echo "DevToolOverlay is not gating placement input through the page capability." >&2
  exit 1
fi

# Scene chrome stays a pure window shell and must dispatch page content through the contract. Both
# shared Scene windows also defend themselves against the standalone debug workspace so BridgePlugin
# call ordering cannot accidentally resurrect an underlying page surface.
if ! grep -Fq 'page.DrawSceneWorkspace(snapshot);' "$scene_workspace"; then
  echo "SceneWorkspaceWindow is not dispatching through IDevToolPageView." >&2
  exit 1
fi
for scene_file in "$scene_workspace" "$scene_placement"; do
  if ! grep -Fq 'DevToolOverlay.SuppressesSharedPageSurfaces' "$scene_file"; then
    echo "Shared Scene surface does not respect the standalone debug workspace: $scene_file" >&2
    exit 1
  fi
done

# Standalone debug is a replacement workspace, not merely a visual overlay. The lifetime plugin runs
# independently from Draw(), so it must keep the normal page deactivated instead of reactivating it
# every LateUpdate from the unchanged backend ToolMode.
if ! grep -Fq 'if (DevToolOverlay.SuppressesSharedPageSurfaces)' "$lifecycle" ||
   ! grep -Fq 'DevToolPageViewRegistry.DeactivateActive();' "$lifecycle"; then
  echo "Standalone debug workspace no longer suppresses normal page lifecycle activation." >&2
  exit 1
fi

# Control Center status and tool labels belong to the page object. The base class caches status text
# by semantic state and language so this ownership move must not regress stable-frame allocations.
if ! grep -Fq 'page.GetSessionStatus(snapshot)' "$control_center"; then
  echo "ControlCenterWindow is not reading session status through the page contract." >&2
  exit 1
fi
if ! grep -Fq 'return page.NavigationLabel;' "$control_center"; then
  echo "ControlCenterWindow is not reading the tool label from page metadata." >&2
  exit 1
fi
if ! grep -Fq 'BuildSessionStatusState' "$page_contract" ||
   ! grep -Fq 'FormatSessionStatus' "$page_contract"; then
  echo "Page session-status stable-frame cache contract is incomplete." >&2
  exit 1
fi

# Navigation is registry-driven. The overlay may iterate NavigationPages but must never rebuild the
# seven built-in buttons itself.
if ! grep -Fq 'DevToolPageViewRegistry.NavigationPages' "$overlay"; then
  echo "DevToolOverlay navigation is not registry-driven." >&2
  exit 1
fi
if ! grep -Fq 'NavigationPages' "$registry"; then
  echo "DevToolPageViewRegistry does not expose ordered navigation pages." >&2
  exit 1
fi

# Retained page projections have exactly one lifecycle owner. The frontend lifetime edge resets the
# page registry and generic chrome; it must not grow a second list of concrete page view reset calls.
if ! grep -Fq 'DevToolPageViewRegistry.ResetAll();' "$lifecycle"; then
  echo "Frontend retained-state lifetime is not resetting registered pages through the registry." >&2
  exit 1
fi
if grep -Eq '(RoomSettingsView|ObjectExplorerView|ObjectSceneWorkspaceView|ObjectInspectorView|SoundEditorView|TriggerEditorView|MapEditorView|WorldWorkspaceView|DialogEditorView|RelationshipEditorView)\.ResetRetainedState' "$lifecycle"; then
  echo "Frontend retained-state lifetime contains a duplicate concrete page reset fan-out." >&2
  exit 1
fi
if grep -Fq 'SceneWorkspaceWindow.ResetRetainedState' "$lifecycle"; then
  echo "Pure SceneWorkspaceWindow was given retained-state ownership again." >&2
  exit 1
fi
if ! grep -Fq 'ScenePlacementWindow.ResetRetainedState();' "$lifecycle"; then
  echo "Scene placement retained projection is not released with the frontend lifetime." >&2
  exit 1
fi

echo "DevTool page-view boundary guard passed."
