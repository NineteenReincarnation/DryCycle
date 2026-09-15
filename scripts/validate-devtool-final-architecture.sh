#!/usr/bin/env bash
set -euo pipefail

root="src/DevUI/DevTool"
objects="$root/Objects"
phase_doc="$root/PHASE6.md"
runtime="$root/Core/DevToolRuntime.cs"
coordinator="$root/Core/DevToolSubsystemCoordinator.cs"

if [[ ! -d "$root" ]]; then
  echo "DevTool root is missing: $root" >&2
  exit 1
fi

# Backend code must stay frontend-neutral. Documentation/comments are intentionally ignored;
# this checks only C# compile-time references outside the dedicated RWImGui frontend project.
backend_imgui_hits="$(
  find "$root" -type f -name '*.cs' ! -path "$root/RWImGui/*" -print0 |
    xargs -0 grep -nE '(^|[^A-Za-z0-9_])(ImGuiNET|ImGuiPtr|ImGuiWindow|ImGuiChild|ImDrawList)([^A-Za-z0-9_]|$)' || true
)"
if [[ -n "$backend_imgui_hits" ]]; then
  echo "DevTool backend contains a forbidden ImGui/RWImGui dependency:" >&2
  echo "$backend_imgui_hits" >&2
  exit 1
fi

# Phase 6 removes framework-specific native inspectors from core ownership. Unknown/third-party
# controls must use the Extension API, generic DevInterface protocol bridge, data model, or Vanilla
# fallback instead of reintroducing private third-party reflection into Objects.
third_party_object_hits="$(
  grep -RInE --include='*.cs' \
    'Pom\.Pom|RegionKit\.Modules\.Objects|PomManagedDataInspectorAdapter|RegionKitAdvancedShaderInspectorAdapter' \
    "$objects" || true
)"
if [[ -n "$third_party_object_hits" ]]; then
  echo "Objects contains a forbidden framework-specific inspector/reflective adapter:" >&2
  echo "$third_party_object_hits" >&2
  exit 1
fi

if [[ -e "$objects/PomManagedDataInspectorAdapter.cs" || -e "$objects/RegionKitAdvancedShaderInspectorAdapter.cs" ]]; then
  echo "Removed framework-specific inspector files were reintroduced." >&2
  exit 1
fi

if grep -Fq 'PomManagedDataInspectorAdapter.EnsureRegistered' "$objects/ObjectCatalog.cs"; then
  echo "ObjectCatalog must not bootstrap a third-party-specific inspector." >&2
  exit 1
fi

# Runtime owns hook/frame orchestration only. Cross-feature queue/reset fan-out has exactly one
# backend owner so adding a workspace cannot silently miss one of several lifecycle lists.
if [[ ! -f "$coordinator" ]]; then
  echo "DevTool subsystem coordinator is missing: $coordinator" >&2
  exit 1
fi

runtime_fanout_hits="$(
  grep -nE '(EditorUi|RoomEditor|SoundEditor|TriggerEditor|MapEditor|DialogEditor|RelationshipEditor)CommandQueue\.(Process|Clear)' \
    "$runtime" || true
)"
if [[ -n "$runtime_fanout_hits" ]]; then
  echo "DevToolRuntime directly owns feature CommandQueue processing/cleanup again:" >&2
  echo "$runtime_fanout_hits" >&2
  exit 1
fi

required_coordinator_symbols=(
  'ProcessPendingCommands'
  'ResetRuntimeState'
  'ClearDetailPresentations'
  'EditorUiCommandQueue.Process'
  'RoomEditorCommandQueue.Process'
  'RelationshipEditorCommandQueue.Process'
  'EditorPresentationHub.Clear'
  'DevToolSessionHub.Reset'
)
for symbol in "${required_coordinator_symbols[@]}"; do
  if ! grep -Fq "$symbol" "$coordinator"; then
    echo "Subsystem coordinator contract is incomplete: missing '$symbol'" >&2
    exit 1
  fi
done

if grep -Eq 'DevToolApi|DevToolExtensionScope|DevToolRegistration' "$coordinator"; then
  echo "UI/runtime reset must not own third-party Extension API scope lifetime." >&2
  exit 1
fi

if ! grep -Fq 'DevToolSubsystemCoordinator.ProcessPendingCommands(session)' "$runtime"; then
  echo "DevToolRuntime is not routing command fan-out through the subsystem coordinator." >&2
  exit 1
fi
if ! grep -Fq 'DevToolSubsystemCoordinator.ResetRuntimeState()' "$runtime"; then
  echo "DevToolRuntime is not routing lifecycle reset through the subsystem coordinator." >&2
  exit 1
fi

if [[ ! -f "$phase_doc" ]]; then
  echo "Phase 6 architecture contract is missing: $phase_doc" >&2
  exit 1
fi

required_contracts=(
  '核心不认识具体第三方 Mod'
  '前端依赖只能单向'
  '状态只能有一个权威拥有者'
  '生命周期必须成对且集中'
  '公共 Extension API 进入 1.x 冻结期'
  '稳定帧不能重新退化'
)

for contract in "${required_contracts[@]}"; do
  if ! grep -Fq "$contract" "$phase_doc"; then
    echo "Phase 6 architecture contract guard: missing '$contract'" >&2
    exit 1
  fi
done

echo "DevTool final architecture guard passed."
