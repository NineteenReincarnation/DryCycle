#!/usr/bin/env bash
set -euo pipefail

# DevTool Architecture
# Consolidated Guard category. Protect durable boundaries rather than historical implementation details.


# ============================================================================
# Migrated from Guard/scripts/validate-devtool-effect-preview-safety.sh
# ============================================================================
set -euo pipefail

preview_root="src/DevUI/DevTool/Preview"
runtime="$preview_root/EffectPreviewRuntime.cs"
ownership="$preview_root/EffectPreviewOwnership.cs"
visual="$preview_root/EffectPreviewRuntimeVisualOwnership.cs"

for file in "$runtime" "$ownership" "$visual"; do
  if [[ ! -f "$file" ]]; then
    echo "Effect preview safety contract file missing: $file" >&2
    exit 1
  fi
done

# Stage 2 may run only behind the conservative safety gate. Stage 1 remains the fallback.
if ! grep -Fq 'private static readonly bool AdvancedPreviewEnabled = true;' "$runtime"; then
  echo "Advanced effect preview gate is not in the expected guarded state." >&2
  exit 1
fi
if ! grep -Fq 'RuntimeMutationSafetyScanner.TryValidate' "$visual" ||
   ! grep -Fq 'OpCodes.Stsfld' "$visual" ||
   ! grep -Fq 'IsMutableFutileCall' "$visual" ||
   ! grep -Fq 'RuntimeCameraUsageScanner.TryCreateJournal' "$visual"; then
  echo "Advanced effect preview lost its mutation/camera fail-closed scan." >&2
  exit 1
fi

# Room.AddObject has one DryCycle owner for preview capture. Visual safety receives direct adoption
# calls and must never regain a second hook on the same lifecycle boundary.
add_installs="$(grep -R -h -E 'On\.Room\.AddObject[[:space:]]*\+=' "$preview_root" --include='*.cs' | wc -l | tr -d ' ')"
add_removals="$(grep -R -h -E 'On\.Room\.AddObject[[:space:]]*-=' "$preview_root" --include='*.cs' | wc -l | tr -d ' ')"
if [[ "$add_installs" != "1" || "$add_removals" != "1" ]]; then
  echo "Effect preview must own exactly one paired Room.AddObject hook; found +$add_installs / -$add_removals." >&2
  exit 1
fi
if ! grep -Fq 'EffectPreviewObjectCapture.AttachRuntimeOwner(this);' "$ownership" ||
   ! grep -Fq 'EffectPreviewRuntimeVisualOwnership.TryAdoptRuntimeObject' "$ownership"; then
  echo "Runtime descendant ownership is no longer routed through the single preview owner." >&2
  exit 1
fi
if grep -Eq 'On\.Room\.AddObject[[:space:]]*[+\-]=' "$visual"; then
  echo "EffectPreviewRuntimeVisualOwnership regained its own Room.AddObject hook." >&2
  exit 1
fi

# Rollback layers must remain failure-independent. An exception in ownership rollback may never
# skip scene/shader cleanup or leave the temporary RoomEffect attached after active state is cleared.
if ! grep -Fq 'RollbackAllOwnedState(typeName, reason);' "$runtime" ||
   ! grep -Fq 'private static void RollbackAllOwnedState' "$runtime" ||
   ! grep -Fq 'LoadedHookReplayProbe.RemoveExact(settings.effects, target);' "$runtime"; then
  echo "Effect preview lost failure-independent rollback or exact temporary-effect cleanup." >&2
  exit 1
fi

# RuntimeDetour is never allowed back into preview safety.
if grep -R -n -E --include='*.cs'   'using[[:space:]]+MonoMod\.RuntimeDetour|MonoMod\.RuntimeDetour\.Hook|new[[:space:]]+Hook\('   "$preview_root" >/tmp/effect_preview_detours.txt 2>/dev/null; then
  echo "Effect preview regained RuntimeDetour usage:" >&2
  cat /tmp/effect_preview_detours.txt >&2
  exit 1
fi

echo "DevTool effect preview safety guard passed."


# ============================================================================
# Migrated from Guard/scripts/validate-devtool-extension-api.sh
# ============================================================================
set -euo pipefail

api_dir="src/DevUI/DevTool/Extensions"
api_file="$api_dir/DevToolExtensionApi.cs"
phase_doc="src/DevUI/DevTool/PHASE5.md"
page_doc="src/DevUI/DevTool/RWImGui/PAGE_VIEW_ARCHITECTURE.md"

if [[ ! -f "$api_file" ]]; then
  echo "DevTool extension API entry point is missing: $api_file" >&2
  exit 1
fi

# The public ABI must stay backend-neutral and must not grow compile/runtime coupling to a
# third-party framework. Scan C# only so documentation can explain these forbidden dependencies.
forbidden='ImGuiNET|RWImGui|RegionKit|PomManaged|POM|Fisobs|M4r|BepInEx\.Bootstrap';
if grep -RInE --include='*.cs' "$forbidden" "$api_dir"; then
  echo "DevTool public extension API contains a forbidden frontend/third-party dependency." >&2
  exit 1
fi

# Page/View composition is an internal implementation contract of DryCycle.DevTool.RWImGui.dll.
# DevToolApi 1.x intentionally does not freeze an ImGui/window/page ABI. Adding such a capability
# requires a deliberate new public-API design instead of exposing the current internal registry.
page_api_forbidden='IDevToolPageView|IDevToolFrontendPage|DevToolPageViewRegistry|RegisterPage|PageRegistration';
if grep -RInE --include='*.cs' "$page_api_forbidden" "$api_dir"; then
  echo "DevTool public extension API leaked an internal Page/View frontend contract." >&2
  exit 1
fi

required_symbols=(
  'public static class DevToolApi'
  'public readonly struct DevToolApiVersion'
  'public enum DevToolCapability'
  'public sealed class DevToolExtensionScope'
  'public sealed class DevToolRegistration'
  'TryRegisterExtension'
  'RegisterObjectDescriptor'
  'RegisterInspector'
  'GetExtensions'
)

for symbol in "${required_symbols[@]}"; do
  if ! grep -Fq "$symbol" "$api_file"; then
    echo "DevTool extension API contract guard: missing $symbol" >&2
    exit 1
  fi
done

if [[ ! -f "$phase_doc" ]]; then
  echo "Phase 5 contract documentation is missing: $phase_doc" >&2
  exit 1
fi

# Stage 5 deliberately keeps custom frontend drawing out of the ABI. If this sentence disappears,
# force a deliberate review rather than silently widening the public compatibility contract.
if ! grep -Fq 'ImGui / RWImGui 绘制回调' "$phase_doc"; then
  echo "Phase 5 boundary documentation no longer records the frontend-injection exclusion." >&2
  exit 1
fi

if [[ ! -f "$page_doc" ]] ||
   ! grep -Fq '它不是第三方 Mod API' "$page_doc" ||
   ! grep -Fq '1.x Extension API 不提供 ImGui 绘制回调、页面注册、窗口注入' "$page_doc"; then
  echo "Internal Page/View documentation no longer records the public Extension API boundary." >&2
  exit 1
fi

echo "DevTool extension API architecture guard passed."


# ============================================================================
# Migrated from Guard/scripts/validate-devtool-final-architecture.sh
# ============================================================================
set -euo pipefail

root="src/DevUI/DevTool"
objects="$root/Objects"
frontend="$root/RWImGui"
phase_doc="$root/PHASE6.md"
runtime="$root/Core/DevToolRuntime.cs"
coordinator="$root/Core/DevToolSubsystemCoordinator.cs"
legacy_controller="$root/Compatibility/LegacyUiPresentationController.cs"
universal_presentation="$root/Compatibility/UniversalDevUiPresentation.cs"
diagnostics_publisher="$root/Compatibility/DevUiDiagnosticsPublisher.cs"
full_audit="$root/Compatibility/DevUiFullAudit.cs"
migration_coverage="$root/Compatibility/DevUiMigrationCoverage.cs"
page_coverage="$root/Compatibility/DevUiPageCoverageTracker.cs"
protocol_inventory="$root/Compatibility/DevUiProtocolInventory.cs"

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

# The frontend may render snapshots and enqueue commands, but it must not call backend mutation
# services directly. Keeping this boundary mechanical prevents Draw code from bypassing History,
# Revision hints or the main-thread command ordering contract.
frontend_action_hits="$(
  grep -RInE --include='*.cs' \
    '(EditorActions|RoomEditorActions|SoundEditorActions|TriggerEditorActions|MapEditorActions|DialogEditorActions|RelationshipEditorActions)\.' \
    "$frontend" || true
)"
if [[ -n "$frontend_action_hits" ]]; then
  echo "RWImGui frontend directly invokes a backend mutation service instead of enqueueing a command:" >&2
  echo "$frontend_action_hits" >&2
  exit 1
fi

# Diagnostics follow the same one-way Presentation rule as normal workspaces. Draw code may read
# detached snapshots, but must never process queues, register protocols, scan live DevUI trees/types,
# or evaluate compatibility state as a side effect.
frontend_diagnostic_side_effect_hits="$(
  grep -RInE --include='*.cs' \
    '(UniversalDevUiCommandQueue\.Process|UniversalDevUiPresentationHub\.Publish|DevUiGenericProtocolBootstrap\.Ensure|DevUiFullAudit\.ObserveAll|DevUiPageCoverageTracker\.Observe|DevUiProtocolInventory\.ObserveLoadedTypes|DevUiSemanticConformanceAudit\.Evaluate|DevUiCompatibilityGate\.Evaluate)' \
    "$frontend" || true
)"
if [[ -n "$frontend_diagnostic_side_effect_hits" ]]; then
  echo "RWImGui frontend is advancing compatibility diagnostics instead of reading published snapshots:" >&2
  echo "$frontend_diagnostic_side_effect_hits" >&2
  exit 1
fi

# Presentation access from RWImGui must be an O(1) detached snapshot read. Universal DevUI capture
# and command execution are backend-owned and diagnostics capture is completely absent from normal
# production frames when diagnostics are disabled.
if ! grep -Fq 'public static UniversalDevUiPresentationSnapshot Current => current;' "$universal_presentation"; then
  echo "Universal DevUI Current must remain a pure O(1) snapshot getter." >&2
  exit 1
fi
if ! grep -Fq 'public static DevUiPageCoverageSnapshot Current => current;' "$page_coverage"; then
  echo "Page coverage Current must remain a pure detached snapshot getter." >&2
  exit 1
fi
if ! grep -Fq 'public static DevUiProtocolInventorySnapshot Current => current;' "$protocol_inventory"; then
  echo "Protocol inventory Current must remain a pure detached snapshot getter." >&2
  exit 1
fi
if grep -Fq 'UniversalDevUiCommandQueue.Process' "$universal_presentation"; then
  echo "Universal DevUI Presentation is executing mutations from its read/publish path." >&2
  exit 1
fi
if [[ ! -f "$diagnostics_publisher" ]]; then
  echo "Backend compatibility diagnostics publisher is missing: $diagnostics_publisher" >&2
  exit 1
fi
if ! grep -Fq 'DevUiDiagnosticsPolicy.Enabled && session?.Owner != null' "$coordinator" ||
   ! grep -Fq 'DevUiDiagnosticsPublisher.Publish(session.Owner)' "$coordinator"; then
  echo "Compatibility diagnostics must be backend-owned and diagnostics-gated." >&2
  exit 1
fi
if grep -Fq 'UniversalDevUiPresentationHub.Publish(session.Owner)' "$coordinator"; then
  echo "Coordinator must not split diagnostic publication ownership from DevUiDiagnosticsPublisher." >&2
  exit 1
fi
if grep -Fq 'DevUiFullAudit.ObserveAll(' "$legacy_controller"; then
  echo "LegacyUiPresentationController must not run FullAudit directly; diagnostics are owned by DevUiDiagnosticsPublisher." >&2
  exit 1
fi

required_diagnostics_publisher_symbols=(
  'DevUiFullAudit.ObserveAll(owner)'
  'UniversalDevUiPresentationHub.Publish(owner)'
  'DevUiPageCoverageTracker.Observe(owner, mirror)'
  'DevUiProtocolInventory.ObserveLoadedTypes()'
  'DevUiSemanticConformanceAudit.Evaluate(mirror)'
  'DevUiCompatibilityGate.Evaluate('
)
for symbol in "${required_diagnostics_publisher_symbols[@]}"; do
  if ! grep -Fq "$symbol" "$diagnostics_publisher"; then
    echo "Backend diagnostics publication contract is incomplete: missing '$symbol'" >&2
    exit 1
  fi
done

# Phase 6 removed two stage-era compatibility helpers. Container protocols are already classified
# structurally by MigrationCoverage, and unknown non-Button Clicked() controls must remain visible as
# protocol gaps until the generic action bridge actually supports them.
if [[ -e "$root/Compatibility/DevUiGenericProtocolBootstrap.cs" ||
      -e "$root/Compatibility/UniversalDevUiProtocolAugmenter.cs" ]]; then
  echo "Obsolete generic compatibility bootstrap/augmenter was reintroduced." >&2
  exit 1
fi
if grep -Fq 'static DevUiFullAudit()' "$full_audit" ||
   grep -Fq 'DevUiMigrationCoverage.RegisterAssignable(' "$full_audit"; then
  echo "DevUiFullAudit must not own a second protocol-registration bootstrap." >&2
  exit 1
fi

# MigrationCoverage is diagnostics, not a public extension-registration surface. Third parties that
# want native behavior must use the Phase 5 DevTool Extension API; they may not declare a type mapped
# merely by calling a public coverage-registration helper.
public_migration_registration_hits="$(
  grep -nE 'public static void Register(Exact|Assignable|TypeName)\(' "$migration_coverage" || true
)"
if [[ -n "$public_migration_registration_hits" ]]; then
  echo "MigrationCoverage compatibility declarations became public again:" >&2
  echo "$public_migration_registration_hits" >&2
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

# Deleted adapters must not survive as dangling compile-time references in another DevTool folder.
removed_adapter_refs="$(
  grep -RInE --include='*.cs' \
    'PomManagedDataInspectorAdapter|RegionKitAdvancedShaderInspectorAdapter' \
    "$root" || true
)"
if [[ -n "$removed_adapter_refs" ]]; then
  echo "Removed framework-specific inspector types are still referenced by DevTool C# code:" >&2
  echo "$removed_adapter_refs" >&2
  exit 1
fi

if [[ -e "$objects/PomManagedDataInspectorAdapter.cs" || -e "$objects/RegionKitAdvancedShaderInspectorAdapter.cs" ]]; then
  echo "Removed framework-specific inspector files were reintroduced." >&2
  exit 1
fi

# Outside Compatibility diagnostics, runtime feature modules may not learn third-party private type
# names. Compatibility is allowed to classify source/coverage for diagnostics, but business editing
# semantics must remain protocol-based.
private_third_party_hits="$(
  find "$root" -type f -name '*.cs' ! -path "$root/Compatibility/*" -print0 |
    xargs -0 grep -nE 'Pom\.Pom|RegionKit\.Modules\.|Fisobs\.|M4r\.' || true
)"
if [[ -n "$private_third_party_hits" ]]; then
  echo "DevTool runtime feature code contains a third-party private runtime type dependency:" >&2
  echo "$private_third_party_hits" >&2
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

# The dormant DevUI lifetime edge used to duplicate feature queues and presentation hubs.
# Compatibility owns only compatibility cleanup; backend runtime cleanup must delegate to Core.
dormant_fanout_hits="$(
  {
    grep -nE '(EditorUi|RoomEditor|SoundEditor|TriggerEditor|MapEditor|DialogEditor|RelationshipEditor|UniversalDevUi)CommandQueue\.Clear' "$legacy_controller" || true
    grep -nE '(Editor|RoomEditor|SoundEditor|TriggerEditor|MapEditor|DialogEditor|RelationshipEditor|UniversalDevUi)PresentationHub\.Clear' "$legacy_controller" || true
  }
)"
if [[ -n "$dormant_fanout_hits" ]]; then
  echo "LegacyUiPresentationController duplicated backend runtime cleanup again:" >&2
  echo "$dormant_fanout_hits" >&2
  exit 1
fi

required_coordinator_symbols=(
  'ProcessPendingCommands'
  'ResetRuntimeState'
  'ClearDetailPresentations'
  'EditorUiCommandQueue.Process'
  'RoomEditorCommandQueue.Process'
  'RelationshipEditorCommandQueue.Process'
  'UniversalDevUiCommandQueue.Process'
  'UniversalDevUiCommandQueue.Clear'
  'EditorPresentationHub.Clear'
  'UniversalDevUiPresentationHub.Clear'
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
if ! grep -Fq 'DevToolSubsystemCoordinator.ResetRuntimeState()' "$legacy_controller"; then
  echo "Dormant DevUI cleanup is not routing backend lifecycle reset through the subsystem coordinator." >&2
  exit 1
fi

# History document identity must include its owning World, and a same-name document backed by a
# replacement runtime object graph must drop stale direct-reference history before edits continue.
if ! grep -Fq 'worldName + "/" + roomName' "$runtime"; then
  echo "Room history identity is not scoped by World." >&2
  exit 1
fi
if ! grep -Fq 'bool documentInstanceReplaced' "$runtime" ||
   ! grep -Fq 'History.ClearActive();' "$runtime"; then
  echo "DevTool can retain stale history across a same-name runtime document replacement." >&2
  exit 1
fi

# Undo/Redo failures must never move history stacks or escape the editor frame. Composite history
# must also attempt compensating rollback for children already applied in the failed operation.
history="src/DevUI/DevTool/History/EditorHistoryService.cs"
if ! grep -Fq "history stack was left unchanged" "$history" ||
   ! grep -Fq "Composite history undo rollback failed" "$history" ||
   ! grep -Fq "Composite history redo rollback failed" "$history"; then
  echo "Editor history lost failure-independent Undo/Redo semantics." >&2
  exit 1
fi

# History batches are document-scoped. A logical document switch must commit already-applied batch
# edits to the old key, while replacement of the same runtime document must discard stale-reference
# batch state before clearing its stack.
if ! grep -Fq 'CommitOpenBatchBeforeDocumentSwitch' "$history" ||
   ! grep -Fq 'DiscardOpenBatch' "$history" ||
   ! grep -Fq 'if (changed && batchDepth > 0 && hasActiveDocument)' "$history"; then
  echo "Editor history batch state can cross document/runtime-instance boundaries." >&2
  exit 1
fi

# Compatibility diagnostics are session/lifetime state and must be reset together. The loaded-type
# inventory is intentionally process-level and is therefore not required here.
required_diagnostic_resets=(
  'DevUiFullAudit.Reset()'
  'DevUiMigrationCoverage.Reset()'
  'DevUiSemanticConformanceAudit.Reset()'
  'DevUiCompatibilityGate.Reset()'
  'DevUiPageCoverageTracker.Reset()'
)
for symbol in "${required_diagnostic_resets[@]}"; do
  if ! grep -Fq "$symbol" "$legacy_controller"; then
    echo "Compatibility lifecycle reset is incomplete: missing '$symbol'" >&2
    exit 1
  fi
done

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


# ============================================================================
# Migrated from Guard/scripts/validate-devtool-native-factory-boundaries.sh
# ============================================================================
set -euo pipefail

root="src/DevUI/DevTool"
factories="$root/Factories"
registry="$factories/NativeAuthoringFactoryRegistry.cs"

required_factories=(
  "$factories/NativePlacedObjectFactory.cs"
  "$factories/NativeSoundFactory.cs"
  "$factories/NativeTriggerFactory.cs"
  "$factories/NativeRoomEffectFactory.cs"
)
for file in "$registry" "${required_factories[@]}"; do
  if [[ ! -f "$file" ]]; then
    echo "Native authoring factory contract is missing: $file" >&2
    exit 1
  fi
done

# Built-in authoring code must not quietly route back through DevInterface UI factories. Those
# calls are permitted only inside the explicit Legacy fallback implementation in Factories or in
# Compatibility. This keeps Page/Panel materialization from becoming authoritative again.
legacy_factory_hits="$(
  find "$root" -type f -name '*.cs' \
    ! -path "$factories/*" \
    ! -path "$root/Compatibility/*" \
    -print0 |
    xargs -0 grep -nE '\.(CreateObjRep|CreateSoundRep|CreateTriggerRep)\(' || true
)"
if [[ -n "$legacy_factory_hits" ]]; then
  echo "DevTool feature code bypasses the Native Factory Layer:" >&2
  echo "$legacy_factory_hits" >&2
  exit 1
fi

legacy_room_effect_hits="$(
  find "$root" -type f -name '*.cs' \
    ! -path "$factories/*" \
    ! -path "$root/Compatibility/*" \
    -print0 |
    xargs -0 grep -nE 'RoomSettingsPage.*Signal\(|\.Signal\(DevUISignalType\.Create' || true
)"
if [[ -n "$legacy_room_effect_hits" ]]; then
  echo "DevTool feature code routes RoomEffect creation through RoomSettingsPage.Signal(Create):" >&2
  echo "$legacy_room_effect_hits" >&2
  exit 1
fi

# The normal authoring entry points must explicitly route through native factories.
required_routes=(
  "$root/Commands/EditorActions.cs:NativePlacedObjectFactory.TryCreate"
  "$root/Sound/SoundEditorActions.cs:NativeSoundFactory.TryCreate"
  "$root/Triggers/TriggerEditorActions.cs:NativeTriggerFactory.TryCreate"
  "$root/Triggers/TriggerEditorActions.cs:NativeTriggeredEventFactory.TryAssign"
  "$root/Room/RoomEditorActions.cs:NativeRoomEffectFactory.TryCreate"
)
for route in "${required_routes[@]}"; do
  file="${route%%:*}"
  symbol="${route#*:}"
  if ! grep -Fq "$symbol" "$file"; then
    echo "Native Factory route missing: $file -> $symbol" >&2
    exit 1
  fi
done

# Every concrete native factory must consult the shared provider registry before deciding that a
# type is built-in or must fall back to Legacy. This keeps future extension support on one path.
required_provider_routes=(
  "$factories/NativePlacedObjectFactory.cs:NativeAuthoringFactoryRegistry.TryCreatePlacedObject"
  "$factories/NativeSoundFactory.cs:NativeAuthoringFactoryRegistry.TryCreateSound"
  "$factories/NativeTriggerFactory.cs:NativeAuthoringFactoryRegistry.TryCreateTrigger"
  "$factories/NativeTriggerFactory.cs:NativeAuthoringFactoryRegistry.TryCreateTriggeredEvent"
  "$factories/NativeRoomEffectFactory.cs:NativeAuthoringFactoryRegistry.TryCreateRoomEffect"
)
for route in "${required_provider_routes[@]}"; do
  file="${route%%:*}"
  symbol="${route#*:}"
  if ! grep -Fq "$symbol" "$file"; then
    echo "Native provider registry route missing: $file -> $symbol" >&2
    exit 1
  fi
done

required_registry_symbols=(
  'RegisterPlacedObject'
  'RegisterSound'
  'RegisterTrigger'
  'RegisterTriggeredEvent'
  'RegisterRoomEffect'
)
for symbol in "${required_registry_symbols[@]}"; do
  if ! grep -Fq "$symbol" "$registry"; then
    echo "Native authoring registry contract is incomplete: missing $symbol" >&2
    exit 1
  fi
done

# Legacy materialization must remain visibly isolated in factory files instead of becoming a second
# silent normal path. Unknown third-party ExtEnum IDs may still use it while public native provider
# contracts remain internal/frozen for validation.
for file in "${required_factories[@]}"; do
  if ! grep -Fq 'TryCreateLegacy' "$file" && [[ "$file" != *"NativeSoundFactory.cs" ]]; then
    echo "Factory lost its explicit Legacy fallback boundary: $file" >&2
    exit 1
  fi
done

if ! grep -Fq 'TryAssignLegacy' "$factories/NativeTriggerFactory.cs"; then
  echo "TriggeredEvent factory lost its explicit Legacy fallback boundary." >&2
  exit 1
fi

echo "DevTool Native Factory boundary guard passed."


# ============================================================================
# Migrated from Guard/scripts/validate-devtool-native-gizmo-boundaries.sh
# ============================================================================
set -euo pipefail

root="src/DevUI/DevTool"
backend="$root/Gizmos/NativeGizmoCommandQueue.cs"
viewport="$root/Core/EditorViewportPresentation.cs"
transaction="$root/History/EditorContinuousTransaction.cs"
frontend="$root/RWImGui/NativeSpatialGizmoView.cs"
pages="$root/RWImGui/BuiltinDevToolPages.cs"
removed_retirement="$root/Compatibility/NativeLegacySpatialHandleRetirement.cs"
removed_spatial_refresh="$root/Compatibility/LegacySpatialBackendRefresh.cs"
legacy_invalidation="$root/Compatibility/NativeLegacyPresentationInvalidation.cs"
legacy_sound_hydrator="$root/Compatibility/LegacySoundPageHydrator.cs"
quiescence="$root/Compatibility/LegacyDevUiQuiescenceController.cs"
sound_actions="$root/Sound/SoundEditorActions.cs"
sound_runtime="$root/Sound/NativeSoundRuntimeReconciler.cs"
sound_activation="$root/Sound/SoundActivationPipeline.cs"
sound_catalog="$root/Sound/SoundSampleCatalog.cs"
sound_resource_snapshot="$root/Sound/NativeSoundResourceSnapshot.cs"
sound_presentation="$root/Sound/SoundEditorRuntime.cs"
trigger_actions="$root/Triggers/TriggerEditorActions.cs"
history="$root/History/EditorHistoryService.cs"
coordinator="$root/Core/DevToolSubsystemCoordinator.cs"

required=(
  "$backend" "$viewport" "$transaction" "$frontend" "$pages"
  "$legacy_invalidation" "$legacy_sound_hydrator" "$quiescence"
  "$sound_actions" "$trigger_actions" "$sound_runtime" "$sound_activation" "$sound_catalog"
  "$sound_resource_snapshot" "$sound_presentation" "$history" "$coordinator"
)
for file in "${required[@]}"; do
  if [[ ! -f "$file" ]]; then
    echo "Native gizmo contract file missing: $file" >&2
    exit 1
  fi
done

for removed in "$removed_retirement" "$removed_spatial_refresh"; do
  if [[ -e "$removed" ]]; then
    echo "Obsolete construct/retain legacy spatial backend returned: $removed" >&2
    exit 1
  fi
done
if grep -R -n -E --include='*.cs' 'NativeLegacySpatialHandleRetirement|LegacySpatialBackendRefresh' "$root" >/tmp/devtool_removed_spatial_hits.txt 2>/dev/null; then
  echo "Native workspaces regained a removed legacy spatial backend layer:" >&2
  cat /tmp/devtool_removed_spatial_hits.txt >&2
  exit 1
fi

if grep -Eq 'using DevInterface|global::DevInterface|RoomCamera|RoomSettings|PlacedObject|AmbientSound|EventTrigger' "$frontend"; then
  echo "NativeSpatialGizmoView directly references backend/Rain World runtime objects." >&2
  exit 1
fi
if ! grep -Fq 'EditorViewportPresentationHub.Current' "$frontend" ||
   ! grep -Fq 'NativeGizmoCommandQueue.Enqueue' "$frontend"; then
  echo "Native gizmo frontend no longer follows detached snapshot -> command flow." >&2
  exit 1
fi

for symbol in \
  'NativeSpatialGizmoView.DrawObjects' \
  'NativeSpatialGizmoView.DrawSound' \
  'NativeSpatialGizmoView.DrawTriggers'; do
  if ! grep -Fq "$symbol" "$pages"; then
    echo "A native spatial page is no longer routed through NativeSpatialGizmoView: $symbol" >&2
    exit 1
  fi
done

for symbol in 'EditorContinuousTransactionHub.Begin' 'EditorContinuousTransactionHub.Commit' 'EditorContinuousTransactionHub.Cancel'; do
  if ! grep -Fq "$symbol" "$backend"; then
    echo "Native gizmo transaction boundary missing: $symbol" >&2
    exit 1
  fi
done

if ! grep -Fq 'new AmbientSoundPlayer' "$sound_runtime" ||
   ! grep -Fq 'ReferenceEquals(player.aSound, sound)' "$sound_runtime"; then
  echo "Native Sound runtime reconciler no longer owns ambient player membership." >&2
  exit 1
fi
if grep -Eq 'using DevInterface|global::DevInterface|new[[:space:]]+(AmbientSoundPanel|SpotSoundHandle|DirectionalSoundHandle)[[:space:]]*\(' "$sound_runtime"; then
  echo "Native Sound runtime reconciler regained DevInterface presentation dependencies." >&2
  exit 1
fi

# Headless Sound resource code may mention legacy concepts in comments, but may not actually import,
# type-check, instantiate, call, or store DevInterface/SoundPage presentation state.
for file in "$sound_activation" "$sound_catalog" "$sound_resource_snapshot"; do
  if grep -Eq 'using DevInterface|global::DevInterface|typeof\(SoundPage\)|is[[:space:]]+SoundPage|as[[:space:]]+SoundPage|SoundPage\.|SoundPage[[:space:]]*\(|AmbientSoundPanel[[:space:]]+[A-Za-z_]|RefreshFilesPage[[:space:]]*\(|\.fileNames' "$file"; then
    echo "Headless Sound resource code regained DevInterface/SoundPage dependencies: $file" >&2
    exit 1
  fi
done
if grep -Eq 'SoundSampleCatalog\.Refresh\(|\.fileNames' "$sound_presentation"; then
  echo "Native Sound presentation reads resources through SoundPage instead of headless catalogues." >&2
  exit 1
fi
if ! grep -Fq 'SoundFileNameCatalog.CurrentNames' "$sound_presentation" ||
   ! grep -Fq 'NativeSoundResourceSnapshot.Capture' "$sound_presentation"; then
  echo "Native Sound presentation no longer consumes the headless resource generation." >&2
  exit 1
fi
if ! grep -Fq 'page.fileNames = names;' "$legacy_sound_hydrator" ||
   ! grep -Fq 'LegacySoundPageHydrator.Step(session)' "$coordinator"; then
  echo "Vanilla Sound compatibility hydration is no longer isolated at the compatibility boundary." >&2
  exit 1
fi

if grep -Eq 'activePage[^;]*Refresh|SoundPage[^;]*Refresh|TriggersPage[^;]*Refresh' "$sound_actions" "$trigger_actions"; then
  echo "Native Sound/Trigger action code calls a legacy page Refresh." >&2
  exit 1
fi
if ! grep -Fq 'page.Refresh();' "$legacy_invalidation"; then
  echo "Sound/Trigger compatibility fallback no longer has an explicit legacy refresh boundary." >&2
  exit 1
fi

if grep -Eq 'On\.DevInterface\.(ObjectsPage|SoundPage|TriggersPage)\.(Update|Refresh)' "$quiescence"; then
  echo "A page-less native Objects/Sound/Trigger page hook returned to quiescence." >&2
  exit 1
fi
if grep -Eq 'PreserveWorldHandles|UseNativeBackendRefresh|IsWorldBackendNode|ObjectsPage_Refresh|ObjectsPage_Update' "$quiescence"; then
  echo "Quiescence regained the removed vanilla world-handle backend contract." >&2
  exit 1
fi

if ! grep -Fq 'NativeLegacyPresentationInvalidation.RefreshCurrentSoundOrTriggerFallback(session)' "$history"; then
  echo "History no longer owns Sound/Trigger legacy fallback invalidation." >&2
  exit 1
fi
if ! grep -Fq 'ReconcileRuntimeAfterHistoryRestore(session)' "$history" ||
   ! grep -Fq 'NativeSoundRuntimeReconciler.Reconcile(session)' "$coordinator"; then
  echo "Undo/Redo no longer reconciles native Sound runtime state." >&2
  exit 1
fi

# The only remaining Handle.Update hook is the generic input arbiter used while legacy-backed pages
# such as Room/Map may still coexist with rebuilt overlay capture. No feature workspace owns one.
handle_hook_hits="$(grep -R -n -E 'On\.DevInterface\.Handle\.Update' "$root" --include='*.cs' || true)"
if [[ -n "$handle_hook_hits" ]]; then
  invalid="$(printf '%s\n' "$handle_hook_hits" | grep -v '/Input/EditorInputRouter.cs:' || true)"
  if [[ -n "$invalid" ]]; then
    echo "A feature-specific DevInterface.Handle.Update hook was introduced:" >&2
    echo "$invalid" >&2
    exit 1
  fi
fi

if ! grep -Fq 'EditorViewportPresentationHub.Publish(session)' "$coordinator" ||
   ! grep -Fq 'NativeGizmoCommandQueue.Process(session)' "$coordinator"; then
  echo "Core coordinator no longer owns native gizmo command/viewport scheduling." >&2
  exit 1
fi

echo "DevTool Native Gizmo boundary guard passed."


# ============================================================================
# Migrated from Guard/scripts/validate-devtool-native-objects-runtime.sh
# ============================================================================
set -euo pipefail

root="src/DevUI/DevTool"
reflection="$root/Objects/NativeDataReflectionInspector.cs"
bootstrap="$root/Objects/NativeObjectInspectorBootstrap.cs"
structured_inspectors="$root/Objects/BuiltinStructuredInspectorAdapters.cs"
runtime_reconciler="$root/Objects/NativeObjectRuntimeReconciler.cs"
runtime_adapters="$root/Objects/BuiltinObjectRuntimeAdapters.cs"
detached_runtime_adapters="$root/Objects/DetachedBuiltinObjectRuntimeAdapters.cs"
scheduler="$root/Core/NativeToolScheduler.cs"
anchor="$root/Core/NativeToolAnchorPage.cs"
removed_controller="$root/Compatibility/ObjectGizmoPresentationController.cs"
sandbox="$root/Compatibility/LegacyObjectSandbox.cs"
quiescence="$root/Compatibility/LegacyDevUiQuiescenceController.cs"
removed_spatial_refresh="$root/Compatibility/LegacySpatialBackendRefresh.cs"
frontend="$root/RWImGui/NativeSpatialGizmoView.cs"
object_gizmo_frontend="$root/RWImGui/NativeObjectGizmoView.cs"
removed_geometry_frontend="$root/RWImGui/NativeObjectGeometryGizmoView.cs"
gizmo_presentation="$root/Objects/NativeObjectGizmoPresentation.cs"
pages="$root/RWImGui/BuiltinDevToolPages.cs"
backend="$root/Gizmos/NativeGizmoCommandQueue.cs"
object_gizmo_backend="$root/Gizmos/NativeObjectGizmoEditCommandQueue.cs"
removed_geometry_backend="$root/Gizmos/NativeObjectGeometryGizmoCommandQueue.cs"
coordinator="$root/Core/DevToolSubsystemCoordinator.cs"
factory="$root/Factories/NativePlacedObjectFactory.cs"

for file in "$reflection" "$bootstrap" "$structured_inspectors" "$runtime_reconciler" "$detached_runtime_adapters" "$runtime_adapters" "$scheduler" "$anchor" "$quiescence" "$sandbox" "$frontend" \
            "$object_gizmo_frontend" "$gizmo_presentation" "$pages" "$backend" "$object_gizmo_backend" "$coordinator" "$factory"; do
  if [[ ! -f "$file" ]]; then
    echo "Native Objects contract file missing: $file" >&2
    exit 1
  fi
done

if [[ -e "$removed_spatial_refresh" ]]; then
  echo "Obsolete Objects legacy spatial refresh layer returned: $removed_spatial_refresh" >&2
  exit 1
fi
if [[ -e "$removed_controller" ]]; then
  echo "Obsolete Objects gizmo presentation shim returned: $removed_controller" >&2
  exit 1
fi
if [[ -e "$removed_geometry_frontend" ]]; then
  echo "Superseded property-only object gizmo frontend returned: $removed_geometry_frontend" >&2
  exit 1
fi
if [[ -e "$removed_geometry_backend" ]]; then
  echo "Superseded property-only object gizmo queue returned: $removed_geometry_backend" >&2
  exit 1
fi

# Rebuilt Objects is page-less just like Sound/Trigger. A matching ObjectsPage is allowed only when
# NativeToolScheduler explicitly materializes the legacy fallback.
if ! grep -Fq 'mode == EditorToolMode.Objects' "$scheduler" ||
   ! grep -Fq 'EditorToolMode.Objects => ObjectsPageIndex' "$scheduler" ||
   ! grep -Fq 'EditorToolMode.Objects => page is ObjectsPage' "$scheduler"; then
  echo "Objects is no longer owned by the page-less NativeToolScheduler boundary." >&2
  exit 1
fi
if grep -Fq 'EditorToolMode.Objects => ObjectsPageIndex' "$scheduler" &&
   ! grep -Fq 'private static int LegacyPageIndex' "$scheduler"; then
  echo "ObjectsPage index escaped the explicit legacy-materialization mapping." >&2
  exit 1
fi

if ! grep -Fq 'native Objects/Sound/Trigger' "$anchor"; then
  echo "NativeToolAnchorPage documentation no longer records Objects ownership." >&2
  exit 1
fi

# A page-less native Objects workspace must not keep an ObjectsPage Update/Refresh hook or any world-
# handle execution plan. Explicit Vanilla/Legacy materializes the original page and runs it normally.
if grep -Eq 'On\.DevInterface\.ObjectsPage\.(Update|Refresh)|ObjectsPage_(Update|Refresh)|PreserveWorldHandles|UseNativeBackendRefresh|IsWorldBackendNode|LegacySpatialBackendRefresh' "$quiescence"; then
  echo "Objects regained a hidden legacy Page/Handle backend in quiescence." >&2
  exit 1
fi

# Unknown third-party Objects may use a selected-only compatibility sandbox, but that sandbox must
# never Refresh the ObjectsPage (which would materialize every room object). It reuses CreateObjRep
# only with the already-existing selected PlacedObject and is reset with the DevTool runtime.
if grep -Fq 'page.Refresh();' "$sandbox" || grep -Fq '.Refresh();' "$sandbox"; then
  echo "Selected-only legacy object sandbox regained a full page refresh." >&2
  exit 1
fi
for symbol in \
  'page.CreateObjRep(target.type, target);' \
  'LegacyDevInterfaceBridge.Capture(session.Owner, target)' \
  'session.Owner.activePage = state.Page;' \
  'session.Owner.activePage = previous;' \
  'LegacyObjectSandbox.Reset();'; do
  if ! grep -R -Fq "$symbol" "$sandbox" "$root/Core/DevToolSubsystemCoordinator.cs"; then
    echo "Selected-only legacy object sandbox lost its no-full-refresh contract: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'LegacyObjectSandbox.Capture(session, selected)' "$root/Core/EditorObjectPresentationPartial.cs" ||
   ! grep -Fq 'LegacyObjectSandbox.Run(session, target, action)' "$root/Commands/EditorActions.cs"; then
  echo "Unknown object controls no longer route through the selected-only sandbox." >&2
  exit 1
fi

# Selected-only compatibility nodes own real Futile sprites and must be explicitly released on
# both runtime reset and DevUI-session replacement; GC alone is not a valid lifecycle.
if ! grep -Fq 'Release(DevToolSessionHub.Current);' "$sandbox" ||
   ! grep -Fq 'LegacyObjectSandbox.Release(previous);' "$root/Core/DevToolRuntime.cs"; then
  echo "Selected-only sandbox is no longer released with session/runtime lifetime." >&2
  exit 1
fi

# Native object interaction must be detached snapshot -> command, never a frontend reference to
# PlacedObject/ObjectsPage/PlacedObjectRepresentation.
if ! grep -Fq 'NativeSpatialGizmoView.DrawObjects(snapshot, display);' "$pages" ||
   ! grep -Fq 'NativeGizmoTargetKind.ObjectPosition' "$backend"; then
  echo "Objects is not routed through the native position gizmo pipeline." >&2
  exit 1
fi
if grep -Eq 'using DevInterface|global::DevInterface|PlacedObject|ObjectsPage|PlacedObjectRepresentation' "$frontend"; then
  echo "NativeSpatialGizmoView regained an Objects backend/runtime dependency." >&2
  exit 1
fi

# Selected-object geometry is now one generic detached primitive protocol. The frontend may know
# only world-space handles/anchor lines/Bezier segments and command IDs; Rain World object types stay
# in backend presentation/command code.
if ! grep -Fq 'NativeObjectGizmoView.Draw(snapshot, display);' "$pages" ||
   ! grep -Fq 'NativeObjectGizmoEditCommandQueue.Enqueue' "$object_gizmo_frontend" ||
   ! grep -Fq 'EditorObjectBezierSegmentSnapshot[]' "$object_gizmo_frontend"; then
  echo "Unified detached Objects gizmo frontend is not wired." >&2
  exit 1
fi
if grep -Eq '^using DevInterface;|global::DevInterface|typeof\(PlacedObject\)|PlacedObject[[:space:]]+[A-Za-z_]|typeof\(ObjectsPage\)|ObjectsPage[[:space:]]+[A-Za-z_]|PlacedObjectRepresentation[[:space:]]+[A-Za-z_]|WaterCurrent\.WaterCurrentData|BezierSpline[[:space:]]+[A-Za-z_]|System\.Reflection|BindingFlags|GetField[[:space:]]*\(|GetProperty[[:space:]]*\(' "$object_gizmo_frontend"; then
  echo "Unified Objects gizmo frontend regained Rain World/DevInterface/reflection dependencies." >&2
  exit 1
fi
for symbol in \
  'WaterCurrent.WaterCurrentData' \
  'PlacedObject.SplineObjectData' \
  'EditorObjectGizmoHandleSnapshot' \
  'EditorObjectLineSegmentSnapshot' \
  'EditorObjectBezierSegmentSnapshot' \
  'Id = "property:" + property.Key' \
  '"water:end"' \
  '"water:width"' \
  '"water:velocity"' \
  '"spline:mid:" + i + ":pos"' \
  '"localTerrain:bottom"' \
  '"superSlope:bottom"' \
  '"waterFlow:width"' \
  '"waterCutoff:end"' \
  '"airPocket:corner"' \
  '"airPocket:waterLevel"' \
  '"mudPit:decalSize"'; do
  if ! grep -Fq "$symbol" "$gizmo_presentation"; then
    echo "Native object gizmo presentation lost a verified primitive mapping: $symbol" >&2
    exit 1
  fi
done
for symbol in \
  'command.HandleId == "superSlope:bottom"' \
  'superSlope.bottom = Mathf.Max(10f, target.pos.y - command.Y);' \
  'command.HandleId == "waterFlow:width"' \
  'Mathf.RoundToInt((command.X - target.pos.x) / 20f)'; do
  if ! grep -Fq "$symbol" "$object_gizmo_backend"; then
    echo "SuperSlope/WaterFlow native gizmo behavior regressed: $symbol" >&2
    exit 1
  fi
done

for symbol in \
  'command.HandleId == "waterCutoff:end"' \
  'if (!command.Snap)' \
  'relative.y = 0f;' \
  'command.HandleId == "airPocket:corner"' \
  'command.HandleId == "airPocket:waterLevel"' \
  'command.HandleId == "mudPit:decalSize"'; do
  if ! grep -Fq "$symbol" "$object_gizmo_backend"; then
    echo "WaterCutoff/AirPocket/MudPit native gizmo behavior regressed: $symbol" >&2
    exit 1
  fi
done

for symbol in \
  'SinglePlacedObjectStateSnapshot.Capture' \
  'EditorContinuousTransactionHub.Begin' \
  'EditorContinuousTransactionHub.Commit' \
  'EditorContinuousTransactionHub.Cancel' \
  'NativeObjectGizmoEditKind.InsertCurvePoint' \
  'NativeObjectGizmoEditKind.RemoveHandle' \
  'SnapEightDirections' \
  'SplitSpline' \
  'RemoveSplineMidpoint' \
  'ObjectInspectorRegistry.TrySetValue' \
  'NativeObjectRuntimeReconciler.RefreshAfterMutation(session, target)' \
  'ObjectPresentationChangeHintHub.MarkMember'; do
  if ! grep -Fq "$symbol" "$object_gizmo_backend"; then
    echo "Unified native object gizmo backend lost model/history behavior: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'NativeObjectGizmoEditCommandQueue.Process(session);' "$coordinator" ||
   ! grep -Fq 'NativeObjectGizmoEditCommandQueue.Clear();' "$coordinator"; then
  echo "Unified object gizmo queue is not owned by the backend coordinator lifetime." >&2
  exit 1
fi

if ! grep -Fq 'DrawLines(draw, viewport, display, gizmo.Lines);' "$object_gizmo_frontend" ||
   ! grep -Fq 'EditorObjectLineSegmentSnapshot[] lines' "$object_gizmo_frontend"; then
  echo "Unified object gizmo frontend lost detached line rendering." >&2
  exit 1
fi

# Representation-specific model/runtime side effects move into native services. TerrainHandle is
# the first required contract: any position/geometry/history mutation must update TerrainCurve.
for symbol in \
  'target.data is PlacedObject.TerrainHandleData' \
  'TerrainManager.ITerrain' \
  'terrain is TerrainCurve curve' \
  'curve.UpdateHandles();' \
  'target.data is PlacedObject.LocalTerrainData localTerrain' \
  'terrain is LocalTerrainCurve local' \
  'local.RefreshCurve();' \
  'terrain is CurvedSlope slope' \
  'slope.RefreshCurve();'; do
  if ! grep -Fq "$symbol" "$runtime_reconciler"; then
    echo "Native object runtime reconciler lost TerrainHandle behavior: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshInteractivePreview(session, target);' "$backend" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshInteractivePreview(session, target)' "$object_gizmo_backend" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshAfterMutation(session, target);' "$root/History/PlacedObjectHistory.cs" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshAfterMutation(session, target)' "$root/Commands/EditorActions.cs"; then
  echo "Native object mutations no longer converge on the runtime side-effect reconciler." >&2
  exit 1
fi

# Representation constructors used to create several gameplay/runtime objects as a side effect.
# Native object creation/deletion/history must now own that membership explicitly.
for symbol in \
  'EnsureRuntimePresence(room, target);' \
  'EnsureWaterMembership(room, target);' \
  'EnsureSimpleRoomRuntime(room, target);' \
  'room.AddObject(new LocalTerrainCurve(room, localTerrain));' \
  'room.AddObject(new CurvedSlope(room, localTerrain));' \
  'room.AddObject(new VoidSpawnMigrationStream(room, streamData));' \
  'room.AddObject(new SuperSlope(room, superSlope));' \
  'room.AddObject(new Geyser(target));' \
  'room.AddObject(new MudPit(target));' \
  'water.MainSurface.waterCutoffs.Add(target);' \
  'new Water.AirPocketSurface(water, target)' \
  'RemoveWaterMembership(room, target);' \
  'target.data is PlacedObject.WaterFlowData' \
  'Mathf.Round(target.pos.x / 20f) * 20f' \
  'slope.thickness = superSlope.bottom;' \
  'internal static void RemoveRuntime(EditorSession session, PlacedObject target)'; do
  if ! grep -Fq "$symbol" "$runtime_reconciler"; then
    echo "Native object runtime membership reconciliation is incomplete: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'NativeObjectRuntimeReconciler.RemoveRuntime(session, target);' "$root/Commands/EditorActions.cs" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.RemoveRuntime(session, selected[i]);' "$root/Commands/EditorActions.cs" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.RemoveRuntime(session, target);' "$root/History/PlacedObjectHistory.cs"; then
  echo "Native object deletion/history no longer removes representation-owned runtime state." >&2
  exit 1
fi

# TerrainRubble and RippleTree had non-visual Representation refresh side effects. They must stay
# on the native runtime path after page-less Objects removed those representations.
for symbol in \
  'target.type == PlacedObject.Type.TerrainRubble' \
  'RefreshTerrainRubble(room);' \
  'curve.UpdateRubble();' \
  'internal static void RefreshAfterRemoval(EditorSession session, PlacedObject target)' \
  'target.data is PlacedObject.RippleStalkData rippleData' \
  'rippleData.update = true;'; do
  if ! grep -Fq "$symbol" "$runtime_reconciler"; then
    echo "TerrainRubble/RippleTree native cache reconciliation regressed: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshAfterRemoval(session, target);' "$root/Commands/EditorActions.cs" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshAfterRemoval(session, selected[i]);' "$root/Commands/EditorActions.cs" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshAfterRemoval(session, target);' "$root/History/PlacedObjectHistory.cs"; then
  echo "TerrainRubble after-removal cache rebuild is no longer wired to delete/history paths." >&2
  exit 1
fi

# LightSource must be bound before its PlacedObject moves, then synchronized without relying on
# LightSourceRepresentation. All model properties and runtime membership are native-owned.
for symbol in \
  'internal static void PrepareForMutation(EditorSession session, PlacedObject target)' \
  'ConditionalWeakTable<PlacedObject, LightBinding>' \
  'EnsureLightSourceRuntime(room, target, createIfMissing: true)' \
  'light.setPos = target.pos;' \
  'light.setRad = data.Rad;' \
  'light.setAlpha = data.strength;' \
  'light.fadeWithSun = data.fadeWithSun;' \
  'light.colorFromEnvironment = data.colorType == PlacedObject.LightSourceData.ColorType.Environment;' \
  'light.flat = data.flat;' \
  'light.effectColor = Math.Max(-1, (int)data.colorType - 2);' \
  'light.setBlinkProperties(data.blinkType, data.blinkRate);' \
  'light.nightLight = data.nightLight;' \
  'RemoveLightSourceRuntime(room, target);'; do
  if ! grep -Fq "$symbol" "$runtime_reconciler"; then
    echo "LightSource runtime binding/sync contract regressed: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'NativeObjectRuntimeReconciler.PrepareForMutation(session, objectTarget);' "$backend" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.PrepareForMutation(session, target);' "$object_gizmo_backend" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.PrepareForMutation(session, target);' "$root/Commands/EditorActions.cs" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.ResetRuntimeState();' "$coordinator"; then
  echo "LightSource pre-mutation binding/runtime lifetime is no longer wired through all native mutation paths." >&2
  exit 1
fi

for symbol in \
  '"lightning:start"' \
  '"lightning:end"'; do
  if ! grep -Fq "$symbol" "$gizmo_presentation" ||
     ! grep -Fq "$symbol" "$object_gizmo_backend"; then
    echo "LightningMachine endpoint native gizmo coverage regressed: $symbol" >&2
    exit 1
  fi
done

# The last direct Handle-style special cases have verified native semantics.
for symbol in \
  '"weaver:direction"' \
  '"lobeTree:root"'; do
  if ! grep -Fq "$symbol" "$gizmo_presentation" ||
     ! grep -Fq "$symbol" "$object_gizmo_backend"; then
    echo "Direct-handle object native coverage regressed: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'direction.normalized * 460f' "$object_gizmo_backend"; then
  echo "WeaverSpot lost its fixed 460-unit direction contract." >&2
  exit 1
fi

# Reflection fallback may expose native sliders, but known vanilla slider ranges must be carried as
# detached metadata and clamped in the backend.
for symbol in \
  'declaringType == typeof(PlacedObject.LightSourceData)' \
  'string.Equals(name, "strength", StringComparison.Ordinal)' \
  'string.Equals(name, "blinkRate", StringComparison.Ordinal)' \
  'Mathf.Clamp(value.X, binding.Min, binding.Max)' \
  'HasRange = binding.HasRange'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "Native LightSource inspector range contract regressed: $symbol" >&2
    exit 1
  fi
done

for symbol in \
  'declaringType == typeof(PlacedObject.LightFixtureData)' \
  'string.Equals(name, "randomSeed", StringComparison.Ordinal)' \
  'max = 100f;' \
  'string.Equals(name, "impact", StringComparison.Ordinal)' \
  'max = 3f;' \
  'string.Equals(name, "soundType", StringComparison.Ordinal)' \
  'int integerValue = binding.HasRange'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "Native LightFixture/Lightning integer range contract regressed: $symbol" >&2
    exit 1
  fi
done

# Runtime objects previously created by LightBeam/BlackSpot/Wind representations are native-owned.
for symbol in \
  'target.type == PlacedObject.Type.LightBeam' \
  'EnsureLightBeamRuntime(room, target);' \
  'beam.meshDirty = true;' \
  'beam.SetBlinkProperties(data.blinkType, data.blinkRate);' \
  'beam.nightLight = data.nightLight;' \
  'target.type == PlacedObject.Type.BlackSpot' \
  'room.AddObject(new BlackSpot(target));' \
  'target.type == PlacedObject.Type.WindRect' \
  'room.AddObject(new WindRect(target));'; do
  if ! grep -Fq "$symbol" "$runtime_reconciler"; then
    echo "LightBeam/BlackSpot/Wind runtime ownership regressed: $symbol" >&2
    exit 1
  fi
done
for symbol in \
  'declaringType == typeof(LightBeam.LightBeamData)' \
  'string.Equals(name, "alpha", StringComparison.Ordinal)' \
  'string.Equals(name, "colorA", StringComparison.Ordinal)' \
  'string.Equals(name, "colorB", StringComparison.Ordinal)'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "Native LightBeam inspector range contract regressed: $symbol" >&2
    exit 1
  fi
done

# Representation-created visual/gameplay runtimes are isolated behind builtin runtime adapters
# instead of growing NativeObjectRuntimeReconciler into another CreateObjRep switch.
if ! grep -Fq 'BuiltinObjectRuntimeAdapters.Refresh(room, target);' "$runtime_reconciler" ||
   ! grep -Fq 'BuiltinObjectRuntimeAdapters.Remove(room, target);' "$runtime_reconciler"; then
  echo "Builtin runtime adapters are no longer connected to the native object runtime boundary." >&2
  exit 1
fi
for symbol in \
  'target.type == PlacedObject.Type.CustomDecal' \
  'runtime.UpdateAsset();' \
  'runtime.UpdateMesh();' \
  'target.type == PlacedObject.Type.GooDrips' \
  'runtime.RefreshCeilingTiles();' \
  'target.type == PlacedObject.Type.Rainbow' \
  'target.type == PlacedObject.Type.RainbowNoFade' \
  'runtime.Refresh();' \
  'target.type == PlacedObject.Type.SSLightRod' \
  'runtime.UpdateLightAmount();' \
  'target.type == PlacedObject.Type.PlateTree' \
  'target.type == PlacedObject.Type.RotPlateTree' \
  'runtime.Reset();' \
  'target.type == PlacedObject.Type.DeepProcessing' \
  'runtime.meshDirty = true;' \
  'customDecal.RoomUnloaded();'; do
  if ! grep -Fq "$symbol" "$runtime_adapters"; then
    echo "Builtin visual runtime adapter coverage regressed: $symbol" >&2
    exit 1
  fi
done
if grep -Eq '^using DevInterface;|global::DevInterface|ObjectsPage|PlacedObjectRepresentation' "$runtime_adapters"; then
  echo "Builtin runtime adapters regained DevInterface dependencies." >&2
  exit 1
fi

# Builtin runtimes that retain the authored PlacedObject are reconstructed directly from model
# state; LightFixture additionally snapshots constructor-only type/randomSeed before mutation.
for symbol in \
  'private sealed class LightFixtureState' \
  'internal static void Prepare(global::Room room, PlacedObject target)' \
  'EnsureLightFixtureRuntime(room, target);' \
  'new Redlight(room, target, data)' \
  'new HolyFire(room, target, data)' \
  'new ZapCoilLight(room, target, data)' \
  'new DeepProcessingLight(room, target, data)' \
  'new SlimeMoldLight(room, target, data)' \
  'new GlowWeedLight(room, target, data)' \
  'new AdjustableFan(target, room)' \
  'new HarmfulSteam(target, room)' \
  'new SkyWhalePathfindingNode(target, room)' \
  'runtime is SpinningFan spinningFan' \
  'DestroyRuntime(room, spinningFan.FanElement)'; do
  if ! grep -Fq "$symbol" "$runtime_adapters"; then
    echo "Direct-reference builtin runtime coverage regressed: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'BuiltinObjectRuntimeAdapters.Prepare(room, target);' "$runtime_reconciler" ||
   ! grep -Fq 'BuiltinObjectRuntimeAdapters.ResetRuntimeState();' "$runtime_reconciler"; then
  echo "Direct-reference runtime constructor-state lifecycle is no longer wired." >&2
  exit 1
fi

for symbol in \
  'EnsureInsectGroupRuntime(room, target);' \
  'room.insectCoordinator = new InsectCoordinator(room);' \
  'room.insectCoordinator.AddGroup(target);' \
  'swarm.Initiate();' \
  'RemoveInsectGroupRuntime(room, target);' \
  'coordinator.allInsects?.Remove(member);' \
  'coordinator.swarms.RemoveAt(i);'; do
  if ! grep -Fq "$symbol" "$runtime_adapters"; then
    echo "Native InsectGroup swarm ownership regressed: $symbol" >&2
    exit 1
  fi
done

for symbol in \
  'EnsureCommonLinkedRuntime(room, target)' \
  'new PlayerPushback(room, target)' \
  'new WaterCurrent(target)' \
  'new FluxDrain(room, target)' \
  'new HugeTurbine(target, room)' \
  'new ReliableIggyDirection(target)' \
  'new ARKillRect(room, target)' \
  'new SpotLight(target)' \
  'new GravityDisruptor(target, room)' \
  'runtime is PlayerPushback pushback' \
  'runtime is WaterCurrent current' \
  'runtime is FluxDrain drain' \
  'runtime is SpinningFan fanRuntime' \
  'runtime is ReliableIggyDirection iggy' \
  'runtime is ARKillRect killRect' \
  'runtime is SpotLight spot' \
  'runtime is GravityDisruptor disruptor'; do
  if ! grep -Fq "$symbol" "$runtime_adapters"; then
    echo "Common linked builtin runtime coverage regressed: $symbol" >&2
    exit 1
  fi
done

for symbol in \
  'target.type == PlacedObject.Type.FluxWaterfall' \
  'RefreshFluxWaterfall(room, target);' \
  'RegisterFluxWaterfall(room, runtime);' \
  'ReferenceEquals(waterfall.data, flowData)' \
  'runtime.topLoop.volume = 0f;' \
  'target.type == PlacedObject.Type.ZapCoil' \
  'RefreshZapCoil(room, target);' \
  'FindZapCoilByPlacedObjectOrdinal' \
  'runtime = new ZapCoil(desired, room);' \
  'bool findExisting'; do
  if ! grep -Fq "$symbol" "$detached_runtime_adapters"; then
    echo "FluxWaterfall/ZapCoil detached runtime contract regressed: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'Acquire(room, target, createIfMissing: true, findExisting: false)' "$detached_runtime_adapters" ||
   ! grep -Fq 'Acquire(room, target, createIfMissing: true, findExisting: true)' "$detached_runtime_adapters"; then
  echo "Detached runtime creation can once again steal an overlapping existing runtime." >&2
  exit 1
fi

# Builtin MSC runtimes such as LightningMachine/EnergySwirl do not retain the authored
# PlacedObject. They require pre-mutation weak binding so moves can still update/remove the exact
# live runtime after the model position changes.
for symbol in \
  'ConditionalWeakTable<PlacedObject, Binding>' \
  'using MoreSlugcats;' \
  'PlacedObject.Type.LightningMachine' \
  'PlacedObject.Type.EnergySwirl' \
  'PlacedObject.Type.SteamPipe' \
  'PlacedObject.Type.WallSteamer' \
  'PlacedObject.Type.SnowSource' \
  'PlacedObject.Type.LocalBlizzard' \
  'PlacedObject.Type.CellDistortion' \
  'machine.startPoint = lightningData.startPoint;' \
  'swirl.setRad = swirlData.Rad;' \
  'snow.shape = snowData.shape;' \
  'blizzard.angle = blizzardData.angle;' \
  'distortion.cromaticIntensity = distortionData.chromaticIntensity;' \
  'steam.direction = Direction(steamData.handlePos);'; do
  if ! grep -Fq "$symbol" "$detached_runtime_adapters"; then
    echo "Detached MSC runtime adapter coverage regressed: $symbol" >&2
    exit 1
  fi
done
for symbol in \
  'DetachedBuiltinObjectRuntimeAdapters.ResetRuntimeState();' \
  'DetachedBuiltinObjectRuntimeAdapters.Prepare(room, target);' \
  'DetachedBuiltinObjectRuntimeAdapters.Refresh(room, target);' \
  'DetachedBuiltinObjectRuntimeAdapters.Remove(room, target);'; do
  if ! grep -Fq "$symbol" "$runtime_reconciler"; then
    echo "Detached MSC runtime adapter is no longer wired through native runtime reconciliation: $symbol" >&2
    exit 1
  fi
done
if grep -Eq '^using DevInterface;|global::DevInterface|ObjectsPage|PlacedObjectRepresentation' "$detached_runtime_adapters"; then
  echo "Detached MSC runtime adapters regained DevInterface dependencies." >&2
  exit 1
fi

# Watcher Urban objects previously relied on Representation constructors/AxisHandles for live
# runtime ownership and constrained authoring. Native Objects must preserve that without DevInterface.
for symbol in \
  'private sealed class UrbanLifeState' \
  'private sealed class UrbanCandleHolderState' \
  'EnsureUrbanLifeRuntime(room, target, urbanLife);' \
  'EnsureUrbanLifePathRuntime(room, target, urbanPath);' \
  'EnsureUrbanCandleHolderRuntime(room, target, holder);' \
  'RemoveWatcherUrbanRuntime(room, target);' \
  'state.Captured && (state.LayerCount != desiredLayers || state.IsShadow != data.isShadow)' \
  'state.Captured && state.Seed != data.seed' \
  'runtime.candleWidthScale = data.candleWidth;' \
  '(int)Mathf.Max('; do
  if ! grep -Fq "$symbol" "$runtime_adapters"; then
    echo "Watcher Urban authoring/runtime contract regressed: $symbol" >&2
    exit 1
  fi
done

for symbol in \
  '"urbanLife:upLeft"' \
  '"urbanLife:downRight"' \
  '"urbanLife:direction"' \
  '"urbanPath:pointA"' \
  '"urbanPath:pointB"'; do
  if ! grep -Fq "$symbol" "$gizmo_presentation" ||
     ! grep -Fq "$symbol" "$object_gizmo_backend"; then
    echo "Watcher Urban detached gizmo coverage regressed: $symbol" >&2
    exit 1
  fi
done

for symbol in \
  'typeof(Watcher.UrbanLife.UrbanLifeData)' \
  'typeof(Watcher.UrbanLifePath.UrbanLifePathData)' \
  'typeof(Watcher.UrbanCandleHolder.UrbanCandleHolderData)' \
  'string.Equals(name, "nLayers", StringComparison.Ordinal)' \
  'string.Equals(name, "density", StringComparison.Ordinal)' \
  'string.Equals(name, "candleWidth", StringComparison.Ordinal)'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "Watcher Urban native inspector range contract regressed: $symbol" >&2
    exit 1
  fi
done

if ! grep -Fq 'Watcher.WatcherEnums.PlacedObjectType.UrbanCandleHolder' "$factory" ||
   ! grep -Fq 'holder.seed = (int)(UnityEngine.Random.value * 111111f);' "$factory"; then
  echo "UrbanCandleHolder native creation no longer preserves vanilla seed initialization." >&2
  exit 1
fi

# CompetitiveFilter uses a session-aware native enum instead of the legacy SelectPanel.
for symbol in \
  'target?.data is PlacedObject.CompetitiveFilterData' \
  'AppendCompetitiveFilter(result, competitive);' \
  '"builtin.competitiveFilter.name"' \
  'CompetitiveGameSession session =' \
  'DevToolSessionHub.Current?.Owner?.game?.session as CompetitiveGameSession' \
  'data.name = options[value.Integer] ?? "NONE";'; do
  if ! grep -Fq "$symbol" "$structured_inspectors"; then
    echo "CompetitiveFilter native selector regressed: $symbol" >&2
    exit 1
  fi
done

# DayNight palette editing is native: vanilla only clamps palettes at zero and immediately applies
# them to rainCycle.
if ! grep -Fq 'data is PlacedObject.DayNightData' "$reflection" ||
   ! grep -Fq 'return Math.Max(0, palette);' "$reflection" ||
   ! grep -Fq 'target.data is PlacedObject.DayNightData dayNight' "$runtime_reconciler" ||
   ! grep -Fq 'dayNight.Apply(room);' "$runtime_reconciler"; then
  echo "DayNight native authoring contract regressed." >&2
  exit 1
fi

# FairyParticle authoring is native: preserve every vanilla slider range and keep existing room
# particles live by calling FairyParticleData.Apply(room) after model mutation.
for symbol in \
  'declaringType == typeof(PlacedObject.FairyParticleData)' \
  'string.Equals(name, "scaleMin", StringComparison.Ordinal)' \
  'max = 25f;' \
  'string.Equals(name, "dirMin", StringComparison.Ordinal)' \
  'max = 360f;' \
  'string.Equals(name, "interpDistMin", StringComparison.Ordinal)' \
  'max = 1000f;' \
  'string.Equals(name, "interpDurMin", StringComparison.Ordinal)' \
  'max = 500f;' \
  'string.Equals(name, "pulseMin", StringComparison.Ordinal)' \
  'max = 50f;' \
  'string.Equals(name, "glowRad", StringComparison.Ordinal)' \
  'max = 200f;' \
  'string.Equals(name, "rotationRate", StringComparison.Ordinal)' \
  'max = 20f;' \
  'string.Equals(name, "numKeyframes", StringComparison.Ordinal)' \
  'min = 1f;' \
  'max = 10f;'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "FairyParticle native inspector/live-preview contract regressed: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'target.data is PlacedObject.FairyParticleData fairyParticle' "$runtime_reconciler" ||
   ! grep -Fq 'fairyParticle.Apply(room);' "$runtime_reconciler"; then
  echo "FairyParticle native live preview no longer calls Data.Apply(room)." >&2
  exit 1
fi

# Final direct Representation gaps from ObjectsPage.CreateObjRep:
# - HideVoidSpawn is fully covered by inherited ResizableObjectData handlePos + generic ExtEnum.
# - RippleEggDestination gets its region-aware destination selector natively.
# - OEsphere gets native radius/parameter editing plus detached runtime ownership.
for symbol in \
  'declaringType == typeof(PlacedObject.ResizableObjectData)' \
  'typeof(ExtEnumBase).IsAssignableFrom(type)'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "HideVoidSpawn inherited native Resizeable/ExtEnum coverage regressed: $symbol" >&2
    exit 1
  fi
done
for symbol in \
  'target?.data is PlacedObject.RippleEggDestinationData' \
  'AppendRippleEggDestination(result, rippleEggDestination);' \
  '"builtin.rippleEggDestination.room"' \
  'regionWarpRooms' \
  'regionSpinningTopRooms' \
  "entry.Split(':')[0].ToLowerInvariant()" \
  'IsRippleEggDestinationManagedProperty'; do
  if ! grep -Fq "$symbol" "$structured_inspectors"; then
    echo "RippleEggDestination native room selector regressed: $symbol" >&2
    exit 1
  fi
done
for symbol in \
  'typeof(PlacedObject.OEsphereData)' \
  'string.Equals(name, "depth", StringComparison.Ordinal)' \
  'string.Equals(name, "lIntensity", StringComparison.Ordinal)'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "OEsphere native inspector/gizmo contract regressed: $symbol" >&2
    exit 1
  fi
done
for symbol in \
  'case OEsphere sphere when target.data is PlacedObject.OEsphereData sphereData:' \
  'target?.type == DLCSharedEnums.PlacedObjectType.OEsphere' \
  'room.oeSpheres[i].pos == target.pos' \
  'runtime = new OEsphere(target.pos, 100f, 0);' \
  'sphere.rad = sphereData.Rad;' \
  'sphere.depth = sphereData.depth;' \
  'sphere.lIntensity = sphereData.lIntensity;'; do
  if ! grep -Fq "$symbol" "$detached_runtime_adapters"; then
    echo "OEsphere detached native runtime ownership regressed: $symbol" >&2
    exit 1
  fi
done

# Watcher SpinningTopSpot no longer needs SpinningTopSpotRepresentation/SpinningTopPanel for
# timeline -> region -> room -> tile destination authoring. The structured inspector owns the dynamic
# choices and preserves the vanilla cascade that clears downstream destination fields.
for symbol in \
  'target?.data is Watcher.SpinningTopData' \
  'AppendSpinningTop(result, spinningTop);' \
  'TrySetSpinningTop(spinningTop, key, value)' \
  '"builtin.spinningTop.timeline"' \
  '"builtin.spinningTop.region"' \
  '"builtin.spinningTop.room"' \
  '"builtin.spinningTop.hasDestPos"' \
  '"builtin.spinningTop.destPos"' \
  'ExtEnum<SlugcatStats.Timeline>.values.entries' \
  'Region.GetFullRegionOrder(data.destTimeline)' \
  'AssetManager.ListDirectory("world/" + region + "-rooms")' \
  'data.RegionString = null;' \
  'data.destRoom = null;' \
  'data.destPos = null;' \
  'Mathf.Floor(pos.x / 20f) * 20f + 10f' \
  'IsSpinningTopManagedProperty'; do
  if ! grep -Fq "$symbol" "$structured_inspectors"; then
    echo "SpinningTopSpot native structured inspector regressed: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'data is Watcher.SpinningTopData' "$reflection" ||
   ! grep -Fq 'return Math.Max(0, spawnIdentifier);' "$reflection"; then
  echo "SpinningTopSpot spawn identifier lost its native non-negative invariant." >&2
  exit 1
fi

# Watcher TerrainGrassPatch no longer depends on TerrainGrassPatchRepresentation/AxisHandle.
# Its eight authored axes are detached native gizmos. Grass regeneration is intentionally deferred
# during continuous preview and finalized once per committed drag so large patches do not respawn
# hundreds/thousands of blades every mouse frame.
for symbol in \
  'typeof(Watcher.GrassBlade.TerrainGrassPatchData)' \
  'string.Equals(name, "amount", StringComparison.Ordinal)' \
  'max = 2000f;' \
  'step = 4f;' \
  'string.Equals(name, "minHeight", StringComparison.Ordinal)' \
  'max = 1000f;' \
  'string.Equals(name, "fallOff", StringComparison.Ordinal)' \
  'string.Equals(name, "width", StringComparison.Ordinal)' \
  'max = 4f;' \
  'string.Equals(name, "spawnedGrass", StringComparison.Ordinal)' \
  'return Mathf.Abs(range);' \
  'return Mathf.Clamp(amount, 0, 2000) / 4 * 4;'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "TerrainGrassPatch native inspector contract regressed: $symbol" >&2
    exit 1
  fi
done
for symbol in \
  '"terrainGrass:range"' \
  '"terrainGrass:fallOff"' \
  '"terrainGrass:width"' \
  '"terrainGrass:depthOffset"' \
  '"terrainGrass:depthRange"' \
  '"terrainGrass:amount"' \
  '"terrainGrass:minHeight"' \
  '"terrainGrass:maxHeight"'; do
  if ! grep -Fq "$symbol" "$gizmo_presentation" ||
     ! grep -Fq "$symbol" "$object_gizmo_backend"; then
    echo "TerrainGrassPatch detached AxisHandle coverage regressed: $symbol" >&2
    exit 1
  fi
done
for symbol in \
  'internal static void RefreshInteractivePreview(EditorSession session, PlacedObject target)' \
  'internal static void FinalizeInteractiveMutation(EditorSession session, PlacedObject target)' \
  'target.data is Watcher.GrassBlade.TerrainGrassPatchData grassPatch' \
  'if (!interactivePreview)' \
  'RespawnTerrainGrassPatch(room, grassPatch);' \
  'Watcher.Grass.InitGrassInRoom(room);' \
  'Watcher.GrassBlade.SpawnGrassPatch(room, data);' \
  'RemoveTerrainGrassPatchRuntime(room, grassPatch);' \
  'room.grass.grassBlades.Remove(data.spawnedGrass[i]);' \
  'room.grass.needsShuffle = true;'; do
  if ! grep -Fq "$symbol" "$runtime_reconciler"; then
    echo "TerrainGrassPatch native runtime lifecycle regressed: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'NativeObjectRuntimeReconciler.FinalizeInteractiveMutation(session, target);' "$object_gizmo_backend" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.FinalizeInteractiveMutation(' "$backend" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshInteractivePreview(session, target);' "$object_gizmo_backend" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshInteractivePreview(session, target);' "$backend"; then
  echo "TerrainGrassPatch continuous preview/finalize boundary is not wired through both object gizmo paths." >&2
  exit 1
fi

# Watcher DaemonEye/DaemonCrown no longer depend on their DevInterface radius handles/panels.
# Both expose native radius gizmos and 0..30 depth editing. Native runtime ownership adopts or creates
# the room runtime; DaemonCrown additionally rebuilds its segment array when the authored radius
# crosses a 20px segment-count boundary and destroys nested DynamicLevelElements on removal.
for symbol in \
  'typeof(Watcher.DaemonEyeData)' \
  'typeof(Watcher.DaemonCrownData)' \
  'string.Equals(name, "depth", StringComparison.Ordinal)' \
  'max = 30f;' \
  'declaringType == typeof(Watcher.DaemonEyeData)' \
  'declaringType == typeof(Watcher.DaemonCrownData)'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "DaemonEye/DaemonCrown native inspector/gizmo contract regressed: $symbol" >&2
    exit 1
  fi
done
for symbol in \
  'EnsureDaemonEyeRuntime(room, target, daemonEye);' \
  'new Watcher.DaemonEye(target, room, target.pos, data.Rad, data.depth)' \
  'FindDaemonEye(room, target)' \
  'RemoveDaemonEyeRuntime(room, target);' \
  'EnsureDaemonCrownRuntime(room, target, daemonCrown);' \
  'Mathf.Max(Mathf.FloorToInt(data.Rad / 20f), 1)' \
  'new Watcher.DaemonCrown(target, room, target.pos, data.Rad, data.depth)' \
  'runtime.crownSegments.Length != desiredSegments' \
  'runtime.CrownSegmentPosition(i)' \
  'RemoveDaemonCrownRuntime(room, target);' \
  'DestroyRuntime(room, runtime.crownSegments[i]);'; do
  if ! grep -Fq "$symbol" "$runtime_adapters"; then
    echo "DaemonEye/DaemonCrown native runtime ownership regressed: $symbol" >&2
    exit 1
  fi
done

# Watcher CosmeticRipple no longer depends on CosmeticRippleRepresentation. Its radius is a
# detached relative-point gizmo; the four original sliders remain 0..1; derived/runtime-only fields
# stay out of the inspector; and the room runtime/list/mask lifecycle is native-owned.
for symbol in \
  'typeof(Watcher.CosmeticRippleData)' \
  'string.Equals(name, "intensity", StringComparison.Ordinal)' \
  'string.Equals(name, "fallOff", StringComparison.Ordinal)' \
  'string.Equals(name, "depthMix", StringComparison.Ordinal)' \
  'string.Equals(name, "squish", StringComparison.Ordinal)' \
  'declaringType == typeof(Watcher.CosmeticRippleData)' \
  'string.Equals(name, "handlePos", StringComparison.Ordinal)' \
  'string.Equals(name, "animateOnSpawn", StringComparison.Ordinal)' \
  'string.Equals(name, "isGameplay", StringComparison.Ordinal)'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "CosmeticRipple native inspector/gizmo contract regressed: $symbol" >&2
    exit 1
  fi
done
for symbol in \
  'EnsureCosmeticRippleRuntime(room, target, cosmeticRipple);' \
  'new Watcher.CosmeticRipple(target)' \
  'FindCosmeticRipple(room, target)' \
  'data.scale = data.handlePos.magnitude;' \
  'runtime.intensity = data.intensity;' \
  'runtime.fallOff = data.fallOff;' \
  'runtime.depthMix = data.depthMix;' \
  'runtime.squish = data.squish;' \
  'runtime.square = data.square;' \
  'runtime.leavesTrail = data.leavesTrail;' \
  'runtime.UpdateRotation();' \
  'room.cosmeticRipples.Add(runtime);' \
  'room.cosmeticRipples.Remove(runtime);' \
  'runtime.RemoveObject();'; do
  if ! grep -Fq "$symbol" "$runtime_adapters"; then
    echo "CosmeticRipple native runtime ownership regressed: $symbol" >&2
    exit 1
  fi
done

# Watcher WallLight no longer depends on WallLightRepresentation: three geometry handles, HSV
# ranges and live runtime ownership are native.
for symbol in \
  '"wallLight:up"' \
  '"wallLight:right"' \
  '"wallLight:one"'; do
  if ! grep -Fq "$symbol" "$gizmo_presentation" ||
     ! grep -Fq "$symbol" "$object_gizmo_backend"; then
    echo "WallLight native gizmo coverage regressed: $symbol" >&2
    exit 1
  fi
done
for symbol in \
  'typeof(Watcher.WallLight.WallLightData)' \
  'string.Equals(name, "hue", StringComparison.Ordinal)' \
  'string.Equals(name, "saturation", StringComparison.Ordinal)' \
  'string.Equals(name, "value", StringComparison.Ordinal)' \
  'string.Equals(name, "obj", StringComparison.Ordinal)' \
  'string.Equals(name, "pos", StringComparison.Ordinal)'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "WallLight native inspector contract regressed: $symbol" >&2
    exit 1
  fi
done
for symbol in \
  'EnsureWallLightRuntime(room, target, wallLight);' \
  'Watcher.WallLight.FromPlacedObject(room, target)' \
  'data.obj = runtime;' \
  'runtime.up = data.up;' \
  'runtime.right = data.right;' \
  'runtime.one = data.one;' \
  'runtime.usePalette = data.usePalette;' \
  'runtime.activeDuring = data.activeDuring;' \
  'runtime.shadowType = data.shadowType;' \
  'runtime.UpdatePaletteColor(room.game.cameras[0].currentPalette);'; do
  if ! grep -Fq "$symbol" "$runtime_adapters"; then
    echo "WallLight native authoring/runtime contract regressed: $symbol" >&2
    exit 1
  fi
done

# Watcher WarpPointToRoom authoring used to live almost entirely inside nested DevInterface
# panels. Native Objects owns destination position, limited uses, effect presets/sliders and the live
# WarpPoint runtime directly.
for symbol in \
  'target?.data is Watcher.WarpPoint.WarpPointData' \
  'AppendWarpPoint(result, warpPoint);' \
  '"builtin.warpPoint.destRegion"' \
  '"builtin.warpPoint.destRoom"' \
  '"builtin.warpPoint.hasDestPos"' \
  '"builtin.warpPoint.destPos"' \
  '"builtin.warpPoint.uses"' \
  'AppendWarpEffect(result, "vignette", "Vignette", effect.vignette, 15);' \
  'AppendWarpEffect(result, "triggerDuration", "Trigger Duration", effect.triggerDuration, 400);' \
  'const string effectPrefix = "builtin.warpPoint.effect.";' \
  '"builtin.warpPoint.preset.default"' \
  '"builtin.warpPoint.preset.outerRim"' \
  '"builtin.warpPoint.preset.badWarp"' \
  '"builtin.warpPoint.preset.dynamic"' \
  'data.limitedUse = data.uses > 0;' \
  'data.destCam = -1;'; do
  if ! grep -Fq "$symbol" "$structured_inspectors"; then
    echo "WarpPointToRoom native inspector contract regressed: $symbol" >&2
    exit 1
  fi
done
for symbol in \
  'EnsureWarpPointRuntime(room, target, warpPoint);' \
  'RemoveWarpPointRuntime(room, target);' \
  'ReferenceEquals(candidate.placedObject, target)' \
  'runtime.parameters.x =' \
  'runtime.parameters.y =' \
  'runtime.parameters.z =' \
  'runtime.parameters.w =' \
  'runtime.activateAnimationTime = 50f + data.effectSettings.activeDuration;' \
  'runtime.triggerActivationTime = 50f + data.effectSettings.triggerDuration;' \
  'runtime.successfullWarpCooldownFrames = data.rippleWarp ? 10 : 1200;' \
  'Watcher.WarpPoint.GetDestCam(data)' \
  'runtime.refreshGraphics = true;'; do
  if ! grep -Fq "$symbol" "$runtime_adapters"; then
    echo "WarpPointToRoom native authoring/runtime contract regressed: $symbol" >&2
    exit 1
  fi
done

# Rebuilt Objects must not retain a selected-representation controller at all. Explicit legacy mode
# owns the original ObjectsPage directly; native mode owns detached gizmos.
if grep -R -n -F --include='*.cs' 'ObjectGizmoPresentationController' "$root" >/tmp/devtool_object_gizmo_shim_hits.txt 2>/dev/null; then
  echo "Obsolete Objects gizmo presentation shim reference returned:" >&2
  cat /tmp/devtool_object_gizmo_shim_hits.txt >&2
  exit 1
fi

# Generic native model inspection is the replacement for using a representation panel as the normal
# property editor. Comments may mention legacy classes while explaining the migration; reject only
# actual imports/type uses/member access so documentation does not trip the architecture guard.
if grep -Eq '^using DevInterface;|global::DevInterface|typeof\(ObjectsPage\)|is[[:space:]]+ObjectsPage|as[[:space:]]+ObjectsPage|ObjectsPage\.|ObjectsPage[[:space:]]*\(|PlacedObjectRepresentation[[:space:]]+[A-Za-z_]|DevUINode[[:space:]]+[A-Za-z_]' "$reflection"; then
  echo "Native reflected object inspector regained DevInterface UI dependencies." >&2
  exit 1
fi
for symbol in \
  'ConcurrentDictionary<Type, Schema>' \
  'typeof(ExtEnumBase).IsAssignableFrom(type)' \
  'ExtEnumBase.GetNames(type)' \
  'ExtEnumBase.Parse(type, selected, ignoreCase: false)' \
  'type == typeof(Vector2)' \
  'type == typeof(Color)'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "Native reflected object inspector lost required model capability: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'internal static void Enable()' "$bootstrap" ||
   ! grep -Fq 'ObjectInspectorRegistry.Register(NativeDataReflectionInspector.Instance, -1000);' "$bootstrap" ||
   ! grep -Fq 'NativeObjectInspectorBootstrap.Enable();' "$root/Core/DevToolRuntime.cs"; then
  echo "Native reflected object inspector is no longer registered below explicit typed adapters from the DevTool runtime lifecycle." >&2
  exit 1
fi
if grep -Fq '[ModuleInitializer]' "$bootstrap"; then
  echo "Native object inspector bootstrap regained a ModuleInitializer dependency; net48 runtime activation must stay explicit." >&2
  exit 1
fi

for symbol in \
  'GizmoHint = ResolveGizmoHint(declaringType, name, valueType)' \
  'declaringType == typeof(PlacedObject.ResizableObjectData)' \
  'declaringType == typeof(PlacedObject.GridRectObjectData)' \
  'declaringType == typeof(PlacedObject.TerrainHandleData)' \
  'BuildVectorArrayBinding(current, field, handleIndex)' \
  'GizmoHint = EditorPropertyGizmoHint.RelativePoint' \
  'EditorPropertyGizmoHint.VerticalDistance' \
  'Math.Min(0f, x)' \
  'Math.Max(0f, x)'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "Native object geometry semantic whitelist lost verified model behavior: $symbol" >&2
    exit 1
  fi
done

# Native reflection fallback preserves builtin slider ranges and coupled constraints that used to
# live only in DevInterface Panels.
for symbol in \
  'typeof(PlacedObject.CustomDecalData)' \
  'typeof(PlacedObject.DeepProcessingData)' \
  'typeof(PlacedObject.SSLightRodData)' \
  'typeof(PlacedObject.SpawnMigrationStreamData)' \
  'typeof(PlacedObject.ScavengerOutpostData)' \
  'typeof(Watcher.TowerCrabSpawner.Data)' \
  'typeof(Watcher.BigSkyWhaleTrigger.Data)' \
  'Mathf.Min(fromDepth, decal.toDepth)' \
  'Mathf.Max(toDepth, decal.fromDepth)' \
  'tower.maxLayer = Mathf.Max(tower.minLayer, tower.maxLayer);' \
  'tower.minLayer = Mathf.Min(tower.maxLayer, tower.minLayer);'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "Builtin inspector slider/coupling semantics regressed: $symbol" >&2
    exit 1
  fi
done

# Builtin array/matrix data that the generic reflection fallback keeps read-only is upgraded by
# strongly typed native adapters while delegating ordinary/base members back to reflection.
for symbol in \
  'ObjectInspectorRegistry.Register(StructuredArrayInspector.Instance, 500);' \
  'target?.data is PlacedObject.CustomDecalData' \
  'target?.data is Rainbow.RainbowData' \
  'target?.data is RainbowNoFade.RainbowNoFadeData' \
  '"builtin.customDecal.alpha.all"' \
  '"builtin.customDecal.erosion.all"' \
  '"builtin.rainbow.fade."' \
  '"builtin.rainbow.thickness"' \
  '"builtin.rainbow.chance"' \
  'NativeDataReflectionInspector.Instance.Capture(target)' \
  'NativeDataReflectionInspector.Instance.TrySetValue(target, key, value)'; do
  if ! grep -Fq "$symbol" "$structured_inspectors"; then
    echo "Structured builtin inspector coverage regressed: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'BuiltinStructuredInspectorAdapters.Enable();' "$bootstrap"; then
  echo "Structured builtin inspectors are no longer registered before reflection fallback." >&2
  exit 1
fi

# Player-availability lists that vanilla exposes as button arrays are native structured
# booleans. Filter additionally recomputes its derived timeline list after every toggle.
for symbol in \
  'target?.data is PlacedObject.FilterData' \
  'target?.data is ReliableIggyDirection.ReliableIggyDirectionData' \
  '"builtin.filter.player."' \
  'data.RefreshTimelineList();' \
  '"builtin.filter.timelines"' \
  '"builtin.reliableIggy.player."' \
  'SlugcatStats.HiddenOrUnplayableSlugcat(name)' \
  'SetMembership(data.availableToPlayers, name, value.Boolean);'; do
  if ! grep -Fq "$symbol" "$structured_inspectors"; then
    echo "Filter/ReliableIggy structured player availability regressed: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'typeof(ReliableIggyDirection.ReliableIggyDirectionData)' "$reflection" ||
   ! grep -Fq 'string.Equals(name, "cyclesToShow", StringComparison.Ordinal)' "$reflection" ||
   ! grep -Fq 'max = 9f;' "$reflection"; then
  echo "ReliableIggy native cycle range contract regressed." >&2
  exit 1
fi

# RippleTree/RippleStalk nullable cosmetics preserve vanilla <R>/inherit semantics as explicit
# Override + Value properties. Value edits enable the override; disabling restores null.
for symbol in \
  'target?.data is PlacedObject.RippleStalkData' \
  'AppendNullableFloat(result, "testRippleAmount"' \
  'AppendNullableFloat(result, "spiralCoils"' \
  'AppendNullableFloat(result, "spiralAmount"' \
  'AppendNullableFloat(result, "droopy"' \
  'AppendNullableFloat(result, "depth"' \
  'AppendNullableFloat(result, "sinWidth"' \
  'AppendNullableFloat(result, "sinDist"' \
  'AppendNullableFloat(result, "sinOffset"' \
  'field ??= 0f;' \
  'field = null;' \
  'data.update = true;'; do
  if ! grep -Fq "$symbol" "$structured_inspectors"; then
    echo "RippleTree structured nullable cosmetic inspector regressed: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'typeof(PlacedObject.RippleTreeData)' "$reflection" ||
   ! grep -Fq 'string.Equals(name, "sproutThreshold", StringComparison.Ordinal)' "$reflection" ||
   ! grep -Fq 'string.Equals(name, "sproutEnd", StringComparison.Ordinal)' "$reflection"; then
  echo "RippleTree native growth range contract regressed." >&2
  exit 1
fi

# CollectToken player availability is a persisted builtin list just like Filter. Hidden/unplayable
# slugcats remain excluded because CollectTokenData.ToString() does not persist them.
for symbol in \
  'target?.data is CollectToken.CollectTokenData' \
  '"builtin.collectToken.player."' \
  'TrySetCollectTokenPlayer' \
  'SlugcatStats.HiddenOrUnplayableSlugcat(name)' \
  'SetMembership(data.availableToPlayers, name, value.Boolean);'; do
  if ! grep -Fq "$symbol" "$structured_inspectors"; then
    echo "CollectToken native player availability regressed: $symbol" >&2
    exit 1
  fi
done

# Remaining vanilla/Watcher object sliders keep their original authored ranges instead of falling
# back to unbounded numeric inputs. Consumable regeneration also preserves min<=max coupling.
for symbol in \
  'typeof(PlacedObject.PrinceFilterData)' \
  'typeof(PlacedObject.RippleLevelFilterData)' \
  'typeof(PlacedObject.RippleEggFilterData)' \
  'typeof(GooDripSource.GooDripsData)' \
  'typeof(PlacedObject.InsectGroupData)' \
  'typeof(PlacedObject.MultiplayerItemData)' \
  'typeof(PlacedObject.FanData)' \
  'typeof(PlacedObject.SkyWhalePathfindingData)' \
  'typeof(PlacedObject.ConsumableObjectData)' \
  'typeof(Watcher.BigSkyWhaleSpawner.Data)' \
  'typeof(Watcher.SandGrubNetwork.NetworkData)' \
  'typeof(DaddyCorruption.CustomRotData)' \
  'Math.Min(regen, consumable.maxRegen)' \
  'Math.Max(regen, consumable.minRegen)'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "Remaining builtin slider range coverage regressed: $symbol" >&2
    exit 1
  fi
done

# Watcher FlameJet and KarmaFlowerPatch no longer require their DevInterface Representations.
# FlameJet owns a detached target handle plus live runtime synchronization; KarmaFlowerPatch owns its
# tilt handle and room-level flower regeneration.
for symbol in \
  '"flameJet:target"' \
  '"karmaPatch:tilt"'; do
  if ! grep -Fq "$symbol" "$gizmo_presentation" ||
     ! grep -Fq "$symbol" "$object_gizmo_backend"; then
    echo "FlameJet/KarmaFlowerPatch native gizmo coverage regressed: $symbol" >&2
    exit 1
  fi
done
for symbol in \
  'target.type == Watcher.WatcherEnums.PlacedObjectType.FlameJet' \
  'EnsureFlameJetRuntime(room, target, flameJet);' \
  'Watcher.FlameJet.FromPlacedObject(room, target)' \
  'runtime.setTarget = data.target;' \
  'runtime.intensityMin = 0f;' \
  'runtime.temperatureMax = data.temperatureMax;' \
  'target.type == Watcher.WatcherEnums.PlacedObjectType.KarmaFlowerPatch' \
  'RefreshKarmaFlowerPatchRuntime(room);' \
  'new Watcher.KarmaFlowerPatch()' \
  'runtime.PlaceFlowers();'; do
  if ! grep -Fq "$symbol" "$runtime_adapters"; then
    echo "FlameJet/KarmaFlowerPatch native authoring regressed: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'BuiltinObjectRuntimeAdapters.RefreshAfterRemoval(room, target);' "$runtime_reconciler"; then
  echo "Room-level Watcher runtime reconciliation is no longer called after object deletion." >&2
  exit 1
fi
for symbol in \
  'typeof(Watcher.KarmaFlowerPatch.KarmaFlowerPatchData)' \
  'string.Equals(name, "glowStrength", StringComparison.Ordinal)' \
  'typeof(Watcher.FlameJet.FlameJetData)' \
  'string.Equals(name, "intensityMin", StringComparison.Ordinal)' \
  'string.Equals(name, "smokeVolumeMax", StringComparison.Ordinal)' \
  'string.Equals(name, "lethality", StringComparison.Ordinal)' \
  'ShouldIgnore(current, field.Name)' \
  'string.Equals(name, "obj", StringComparison.Ordinal)' \
  'string.Equals(name, "pos", StringComparison.Ordinal)'; do
  if ! grep -Fq "$symbol" "$reflection"; then
    echo "FlameJet/KarmaFlowerPatch inspector semantics regressed: $symbol" >&2
    exit 1
  fi
done

# Watcher UrbanCandlePlacer owns a world-space brush center and a radius handle relative to that
# center. Native gizmos must preserve both persisted handlePos and transient radius.
for symbol in \
  '"urbanCandle:brush"' \
  '"urbanCandle:radius"'; do
  if ! grep -Fq "$symbol" "$gizmo_presentation" ||
     ! grep -Fq "$symbol" "$object_gizmo_backend"; then
    echo "UrbanCandlePlacer detached brush geometry regressed: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'candlePlacer.brushHandlePos = world;' "$object_gizmo_backend" ||
   ! grep -Fq 'candlePlacer.handlePos = world - candlePlacer.brushHandlePos;' "$object_gizmo_backend" ||
   ! grep -Fq 'candlePlacer.radius = candlePlacer.handlePos.magnitude;' "$object_gizmo_backend"; then
  echo "UrbanCandlePlacer brush/radius coupling regressed." >&2
  exit 1
fi

# Native Objects actions/history never refresh the current page directly. Only Compatibility may
# refresh a materialized ObjectsPage when Vanilla/Legacy/diagnostics actually owns it.
if grep -Eq 'activePage[^;]*Refresh|Owner[^;]*activePage[^;]*Refresh|session[^;]*activePage[^;]*Refresh' "$root/Commands/EditorActions.cs"; then
  echo "Native object actions regained a direct Page.Refresh dependency." >&2
  exit 1
fi
if ! grep -Fq 'NativeLegacyPresentationInvalidation.RefreshCurrentObjectFallback(session)' "$root/Commands/EditorActions.cs" ||
   ! grep -Fq 'NativeLegacyPresentationInvalidation.RefreshCurrentObjectFallback(session)' "$root/History/PlacedObjectHistory.cs" ||
   ! grep -Fq 'internal static void RefreshCurrentObjectFallback(EditorSession session)' "$root/Compatibility/NativeLegacyPresentationInvalidation.cs"; then
  echo "Objects legacy refresh fallback escaped the Compatibility boundary." >&2
  exit 1
fi

# Builtin/game-defined creation remains data-first. CreateObjRep is allowed only inside the isolated
# legacy fallback block for unknown third-party ExtEnum object IDs.
if ! grep -Fq 'GameDefinedExtEnumCatalog.Contains(typeof(PlacedObject.Type), type.value)' "$factory" ||
   ! grep -Fq 'page.CreateObjRep(type, null);' "$factory"; then
  echo "Native PlacedObject factory lost its builtin-first / unknown-legacy fallback split." >&2
  exit 1
fi

# Only the global input arbiter may still observe Handle.Update. Objects no longer owns a second
# presentation hook for normal rebuilt editing.
handle_hook_hits="$(grep -R -n -E 'On\.DevInterface\.Handle\.Update' "$root" --include='*.cs' || true)"
if [[ -n "$handle_hook_hits" ]]; then
  invalid="$(printf '%s\n' "$handle_hook_hits" | grep -v '/Input/EditorInputRouter.cs:' || true)"
  if [[ -n "$invalid" ]]; then
    echo "Unexpected DevInterface.Handle.Update hook remains after Objects virtualization:" >&2
    echo "$invalid" >&2
    exit 1
  fi
fi

echo "DevTool Native Objects runtime boundary guard passed."


# ============================================================================
# Migrated from Guard/scripts/validate-devtool-native-sound-trigger-runtime.sh
# ============================================================================
set -euo pipefail

root="src/DevUI/DevTool"
sound_runtime="$root/Sound/SoundEditorRuntime.cs"
trigger_runtime="$root/Triggers/TriggerEditorRuntime.cs"
trigger_catalog="$root/Triggers/TriggerSongCatalog.cs"
selection_bridge="$root/Compatibility/LegacySoundTriggerSelectionBridge.cs"
sound_hydrator="$root/Compatibility/LegacySoundPageHydrator.cs"
trigger_hydrator="$root/Compatibility/LegacyTriggerPageHydrator.cs"
top_level_pump="$root/Compatibility/LegacyNativeSoundTriggerTopLevelPump.cs"
scheduler="$root/Compatibility/NativeSoundTriggerDevUiScheduler.cs"
native_tool_scheduler="$root/Core/NativeToolScheduler.cs"
native_anchor="$root/Core/NativeToolAnchorPage.cs"
runtime="$root/Core/DevToolRuntime.cs"
quiescence="$root/Compatibility/LegacyDevUiQuiescenceController.cs"
removed_sound_ctor="$root/Compatibility/SoundPageConstructorOptimization.cs"
removed_spatial_retirement="$root/Compatibility/NativeLegacySpatialHandleRetirement.cs"
coordinator="$root/Core/DevToolSubsystemCoordinator.cs"
misc_runtime="src/Misc/MiscRuntime.cs"

for file in "$sound_runtime" "$trigger_runtime" "$trigger_catalog" "$selection_bridge" \
            "$sound_hydrator" "$trigger_hydrator" "$top_level_pump" "$scheduler" \
            "$native_tool_scheduler" "$native_anchor" "$runtime" "$quiescence" \
            "$coordinator" "$misc_runtime"; do
  if [[ ! -f "$file" ]]; then
    echo "Native Sound/Trigger runtime contract file missing: $file" >&2
    exit 1
  fi
done

if [[ -e "$removed_sound_ctor" ]]; then
  echo "Obsolete SoundPage constructor optimization returned: $removed_sound_ctor" >&2
  exit 1
fi
if [[ -e "$removed_spatial_retirement" ]]; then
  echo "Obsolete Sound/Trigger legacy spatial retirement layer returned: $removed_spatial_retirement" >&2
  exit 1
fi

# Native runtime/presentation is allowed to use Rain World model types, but never DevInterface UI
# nodes/pages as state owners. Legacy selection import lives exclusively in Compatibility.
if grep -Eq '^using DevInterface;|global::DevInterface|is[[:space:]]+SoundPage|as[[:space:]]+SoundPage|SoundPage\.|AmbientSoundPanel[[:space:]]+[A-Za-z_]|DevUINode[[:space:]]+[A-Za-z_]' "$sound_runtime"; then
  echo "Native Sound runtime regained DevInterface presentation ownership." >&2
  exit 1
fi
if grep -Eq '^using DevInterface;|global::DevInterface|is[[:space:]]+TriggersPage|as[[:space:]]+TriggersPage|TriggersPage\.|TriggerPanel[[:space:]]+[A-Za-z_]|DevUINode[[:space:]]+[A-Za-z_]|\.songNames' "$trigger_runtime"; then
  echo "Native Trigger runtime regained DevInterface presentation ownership." >&2
  exit 1
fi

# Trigger metadata discovery must stay headless. It may document the old page in comments, but must
# not import/type-check/instantiate it or publish through page.songNames.
if grep -Eq '^using DevInterface;|global::DevInterface|typeof\(TriggersPage\)|is[[:space:]]+TriggersPage|as[[:space:]]+TriggersPage|TriggersPage\.|\.songNames' "$trigger_catalog"; then
  echo "Trigger song catalogue regained a TriggersPage dependency." >&2
  exit 1
fi
if ! grep -Fq 'TriggerSongCatalog.CurrentNames' "$trigger_runtime"; then
  echo "Trigger presentation no longer consumes the headless song catalogue." >&2
  exit 1
fi

# Exact legacy UI translation is isolated in Compatibility and runs before native queues so a native
# selection command from the same frame stays authoritative.
if ! grep -Fq 'AmbientSoundPanel panel' "$selection_bridge" ||
   ! grep -Fq 'TriggerPanel panel' "$selection_bridge" ||
   ! grep -Fq 'LegacySoundTriggerSelectionBridge.Synchronize(session);' "$coordinator"; then
  echo "Legacy Sound/Trigger selection translation is no longer isolated at the compatibility boundary." >&2
  exit 1
fi

# Headless metadata is projected back into vanilla pages only when compatibility presentation is
# actually active. The native runtime must never read those page fields back.
if ! grep -Fq 'page.fileNames = names;' "$sound_hydrator" ||
   ! grep -Fq 'page.songNames = names;' "$trigger_hydrator" ||
   ! grep -Fq 'LegacySoundPageHydrator.Step(session);' "$coordinator" ||
   ! grep -Fq 'LegacyTriggerPageHydrator.Step(session);' "$coordinator"; then
  echo "Legacy Sound/Trigger metadata hydration is not isolated in Compatibility." >&2
  exit 1
fi

# Runtime lifetime is centralized. A new DevUI session must not reuse stale trigger metadata or page
# projection state from a previous room/editor lifetime.
if ! grep -Fq 'LegacySoundPageHydrator.Reset();' "$coordinator" ||
   ! grep -Fq 'LegacyTriggerPageHydrator.Reset();' "$coordinator" ||
   ! grep -Fq 'TriggerSongCatalog.ResetRuntimeState();' "$coordinator"; then
  echo "Native Sound/Trigger compatibility/catalog lifetime reset is incomplete." >&2
  exit 1
fi

# Native Sound/Trigger workspace selection is genuinely page-less. The anchor may inherit only Page,
# must retire Page's common chrome immediately, and must never materialize Room/Objects/Sound/Trigger
# business controls of its own.
if ! grep -Fq 'internal sealed class NativeToolAnchorPage : Page' "$native_anchor" ||
   ! grep -Fq 'subNodes.Clear();' "$native_anchor" ||
   ! grep -Fq 'initRefresh = false;' "$native_anchor"; then
  echo "NativeToolAnchorPage no longer satisfies the inert Page lifetime contract." >&2
  exit 1
fi
if grep -Eq 'RoomSettingsPage|ObjectsPage|SoundPage|TriggersPage|MapPage|DialogPage|RelationshipPage' "$native_anchor"; then
  echo "NativeToolAnchorPage gained a legacy business-page dependency." >&2
  exit 1
fi

# Entering Sound/Trigger installs the native anchor before EditorSession's generic SwitchPage path.
# Leaving the anchor is also owned here: all five canonical non-native destinations must materialize
# their real page before ResolveToolMode can mistake the unknown anchor for Room.
if ! grep -Fq 'internal static bool TryActivate(EditorSession session, EditorToolMode mode)' "$native_tool_scheduler" ||
   ! grep -Fq 'session.Owner.activePage = new NativeToolAnchorPage(session.Owner);' "$native_tool_scheduler" ||
   ! grep -Fq 'return LeaveNativeAnchor(session, mode);' "$native_tool_scheduler" ||
   ! grep -Fq 'private static bool LeaveNativeAnchor(EditorSession session, EditorToolMode mode)' "$native_tool_scheduler" ||
   ! grep -Fq 'session.Owner.SwitchPage(pageIndex);' "$native_tool_scheduler"; then
  echo "Native Sound/Trigger scheduler lost its enter/exit anchor ownership." >&2
  exit 1
fi
for mapping in \
  'EditorToolMode.Room => RoomPageIndex' \
  'EditorToolMode.Objects => ObjectsPageIndex' \
  'EditorToolMode.Map => MapPageIndex' \
  'EditorToolMode.Dialog => DialogPageIndex' \
  'EditorToolMode.Relationships => RelationshipsPageIndex'; do
  if ! grep -Fq "$mapping" "$native_tool_scheduler"; then
    echo "Native anchor exit mapping missing: $mapping" >&2
    exit 1
  fi
done
if grep -Eq 'RoomAnchorPageIndex|page is RoomSettingsPage.*anchor|new RoomSettingsPage' "$native_tool_scheduler"; then
  echo "Native Sound/Trigger scheduler regressed to a heavyweight RoomSettingsPage anchor." >&2
  exit 1
fi
if ! grep -Fq 'DevUiDiagnosticsPolicy.Enabled' "$native_tool_scheduler"; then
  echo "Diagnostics no longer force real Sound/Trigger page materialization for compatibility audit." >&2
  exit 1
fi

native_select_line="$(grep -n 'NativeToolScheduler.TryActivate(this, mode)' "$runtime" | head -1 | cut -d: -f1)"
legacy_switch_line="$(grep -n 'Owner.SwitchPage(pageIndex);' "$runtime" | head -1 | cut -d: -f1)"
if [[ -z "$native_select_line" || -z "$legacy_switch_line" || "$native_select_line" -ge "$legacy_switch_line" ]]; then
  echo "EditorSession.SetToolMode no longer gives NativeToolScheduler priority over generic legacy SwitchPage." >&2
  exit 1
fi
if ! grep -Fq 'NativeToolScheduler.MaterializeLegacyTool(this, ToolMode, explicitLegacyUi: true)' "$runtime" ||
   ! grep -Fq 'NativeToolScheduler.ReturnToNativeTool(this)' "$runtime"; then
  echo "Per-workspace Legacy UI no longer materializes/retires Sound/Trigger through NativeToolScheduler." >&2
  exit 1
fi
if ! grep -Fq 'NativeToolScheduler.SynchronizePresentationOwnership(session);' "$runtime" ||
   ! grep -Fq 'NativeToolScheduler.SynchronizePresentationOwnership(DevToolRuntime.ActiveSession);' "$scheduler"; then
  echo "Global New UI/Vanilla ownership is not synchronized on the main-thread Sound/Trigger scheduler boundary." >&2
  exit 1
fi

# Native Sound/Trigger own no dedicated derived Page.Update hooks at all. The top-level DevUI
# scheduler consumes virtual workspaces before activePage.Update; explicit Vanilla/Legacy pages fail
# open to the original DevUI lifecycle. This is a structural rule, not an install-then-unsubscribe trick.
if grep -R -n -E --include='*.cs' 'On\.DevInterface\.(SoundPage|TriggersPage)\.Update' "$root" >/tmp/devtool_sound_trigger_page_hooks.txt 2>/dev/null; then
  echo "Dedicated Sound/Trigger Page.Update hook returned:" >&2
  cat /tmp/devtool_sound_trigger_page_hooks.txt >&2
  exit 1
fi
if grep -Eq 'SoundPage_Update|TriggersPage_Update|RetireNativeSoundTriggerPageUpdateHooks' "$quiescence" "$top_level_pump" "$scheduler"; then
  echo "Obsolete Sound/Trigger derived-page update handler/retirement code returned." >&2
  exit 1
fi
if ! grep -Fq 'TryRunNativeSoundTriggerTopLevelUpdate' "$top_level_pump" ||
   ! grep -Fq 'NativeToolScheduler.IsVirtualToolActive(session)' "$top_level_pump" ||
   ! grep -Fq 'TryRunNativeSoundTriggerTopLevelUpdate(self)' "$scheduler"; then
  echo "Native Sound/Trigger top-level update ownership is incomplete." >&2
  exit 1
fi
if ! grep -Fq 'EditorUiModeState.UseVanilla' "$top_level_pump" ||
   ! grep -Fq 'session?.LegacyUiVisible == true' "$top_level_pump"; then
  echo "Visible Vanilla/Legacy Sound/Trigger presentation no longer fails open before native pumping." >&2
  exit 1
fi

# SoundPage.Update contains one model-side preview behavior: horizontal mouse position drives music
# threat preview. Page virtualization must preserve it explicitly without keeping SoundPage.Update.
if ! grep -Fq 'PreserveNativeSoundPreviewContract(session, owner);' "$top_level_pump" ||
   ! grep -Fq 'threatTracker.currentThreat = Mathf.InverseLerp(0f, 1300f, Futile.mousePosition.x);' "$top_level_pump"; then
  echo "Native Sound lost the vanilla threat-music preview contract." >&2
  exit 1
fi

# Once page virtualization is active, there is no reason to intercept SoundPage construction or its
# file-list refresh. Native never constructs SoundPage; explicit Vanilla/Legacy must run the complete
# original constructor. Reintroducing either hook would duplicate ownership and add startup risk.
if grep -R -n -E --include='*.cs' 'IL\.DevInterface\.SoundPage\.ctor|On\.DevInterface\.SoundPage\.RefreshFilesPage|SoundPageConstructorOptimization' \
    "$root" "$misc_runtime" >/tmp/devtool_sound_ctor_hits.txt 2>/dev/null; then
  echo "Obsolete SoundPage constructor/file-list interception returned:" >&2
  cat /tmp/devtool_sound_ctor_hits.txt >&2
  exit 1
fi

# Hook order is semantic: quiescence installs generic compatibility hooks first, scheduler becomes the
# inner DevUI layer second, and DevToolRuntime becomes the outer authoritative editor lifecycle last.
q_line="$(grep -n 'LegacyDevUiQuiescenceController.Enable' "$misc_runtime" | head -1 | cut -d: -f1)"
s_line="$(grep -n 'NativeSoundTriggerDevUiScheduler.Enable' "$misc_runtime" | head -1 | cut -d: -f1)"
r_line="$(grep -n 'DevToolRuntime.Enable' "$misc_runtime" | head -1 | cut -d: -f1)"
if [[ -z "$q_line" || -z "$s_line" || -z "$r_line" || "$q_line" -ge "$s_line" || "$s_line" -ge "$r_line" ]]; then
  echo "Sound/Trigger scheduler hook ordering changed; expected quiescence -> scheduler -> DevToolRuntime." >&2
  exit 1
fi

if ! grep -Fq 'NativeSoundTriggerDevUiScheduler.Disable' "$misc_runtime"; then
  echo "Native Sound/Trigger scheduler lifetime is not paired on shutdown." >&2
  exit 1
fi

echo "DevTool Native Sound/Trigger runtime boundary guard passed."


# ============================================================================
# Migrated from Guard/scripts/validate-devtool-page-view-boundaries.sh
# ============================================================================
set -euo pipefail

root="src/DevUI/DevTool"
frontend="$root/RWImGui"
frontend_project="$frontend/DryCycle.DevTool.RWImGui.csproj"
overlay="$frontend/DevToolOverlay.cs"
control_center="$frontend/ControlCenterWindow.cs"
scene_workspace="$frontend/SceneWorkspaceWindow.cs"
scene_placement="$frontend/ScenePlacementWindow.cs"
object_scene_workspace="$frontend/ObjectSceneWorkspaceView.cs"
core_page="$root/Core/IDevToolPage.cs"
page_contract="$frontend/IDevToolPageView.cs"
builtin_pages="$frontend/BuiltinDevToolPages.cs"
registry="$frontend/DevToolPageViewRegistry.cs"
lifecycle="$frontend/DevToolRetainedViewLifecyclePlugin.cs"
map_runtime="$root/Map/MapEditorRuntime.cs"
dialog_runtime="$root/Dialog/DialogEditorRuntime.cs"

required_files=(
  "$frontend_project"
  "$overlay"
  "$control_center"
  "$scene_workspace"
  "$scene_placement"
  "$object_scene_workspace"
  "$core_page"
  "$page_contract"
  "$builtin_pages"
  "$registry"
  "$lifecycle"
  "$map_runtime"
  "$dialog_runtime"
)
for file in "${required_files[@]}"; do
  if [[ ! -f "$file" ]]; then
    echo "DevTool page-view boundary file is missing: $file" >&2
    exit 1
  fi
done

# The RWImGui project intentionally relies on Microsoft.NET.Sdk's default Compile glob. Keeping the
# project beside the frontend sources means every current/future .cs file in this directory tree is
# compiled automatically; there must not be a parallel hand-maintained Compile list that can silently
# omit a newly extracted page/view such as ObjectSceneWorkspaceView.
if ! grep -Fq '<Project Sdk="Microsoft.NET.Sdk">' "$frontend_project" ||
   ! grep -Fq '<AssemblyName>DryCycle.DevTool.RWImGui</AssemblyName>' "$frontend_project"; then
  echo "RWImGui frontend project no longer uses the expected SDK/default compile contract." >&2
  exit 1
fi
if grep -Eq '<EnableDefaultCompileItems>[[:space:]]*false[[:space:]]*</EnableDefaultCompileItems>' "$frontend_project" ||
   grep -Eq '<Compile[[:space:]]+(Include|Remove|Update)=' "$frontend_project"; then
  echo "RWImGui frontend introduced an explicit Compile item policy; page source inclusion can now drift." >&2
  exit 1
fi

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

# Identity/lifecycle and rendering remain conceptually separate contracts, but the registry owns one
# composite object per page. This prevents Page and PageView from drifting into two maps, two IDs or
# two reset/activation lifetimes.
required_core_page_symbols=(
  'string Id { get; }'
  'EditorToolMode Mode { get; }'
  'void Activate();'
  'void Deactivate();'
  'void Reset();'
)
for symbol in "${required_core_page_symbols[@]}"; do
  if ! grep -Fq "$symbol" "$core_page"; then
    echo "IDevToolPage lifecycle contract is missing '$symbol'." >&2
    exit 1
  fi
done
if ! grep -Fq 'internal interface IDevToolFrontendPage : IDevToolPage, IDevToolPageView' "$page_contract"; then
  echo "RWImGui no longer composes lifecycle and rendering into one authoritative page object." >&2
  exit 1
fi
if ! grep -Fq 'Dictionary<EditorToolMode, IDevToolFrontendPage> Pages' "$registry" ||
   ! grep -Fq 'Dictionary<string, IDevToolFrontendPage> PagesById' "$registry" ||
   ! grep -Fq 'Register(IDevToolFrontendPage page)' "$registry"; then
  echo "DevTool page registry is no longer backed exclusively by composite frontend pages." >&2
  exit 1
fi
if grep -Eq 'Dictionary<[^>]*IDevToolPageView|Dictionary<[^>]*IDevToolPage>' "$registry"; then
  echo "DevTool page registry reintroduced a parallel lifecycle/view map." >&2
  exit 1
fi
if ! grep -Fq 'previous?.Deactivate();' "$registry" ||
   ! grep -Fq 'next.Activate();' "$registry"; then
  echo "DevTool page switching no longer owns paired Deactivate/Activate lifecycle calls." >&2
  exit 1
fi

# Compile-sensitive registry contracts are checked explicitly because C# out parameters are invariant.
# Shared callers use `out IDevToolPageView`; changing TryGet to `out IDevToolFrontendPage` would look
# structurally similar but fail the frontend build.
if ! grep -Fq 'internal static bool TryGet(EditorToolMode mode, out IDevToolPageView view)' "$registry" ||
   ! grep -Fq 'internal static bool TryGet(string id, out IDevToolPageView view)' "$registry"; then
  echo "DevTool page registry TryGet signatures no longer match the shared IDevToolPageView callers." >&2
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

# The composition root uses public detached backend snapshot types. Keep the exact type names guarded
# so a backend rename cannot leave the separately-built RWImGui assembly with a stale type reference.
if ! grep -Fq 'public sealed class EditorMapPresentationSnapshot' "$map_runtime" ||
   ! grep -Fq 'EditorMapPresentationSnapshot map = MapEditorPresentationHub.Current;' "$builtin_pages"; then
  echo "RWImGui Map page status references a missing/renamed EditorMapPresentationSnapshot contract." >&2
  exit 1
fi
if ! grep -Fq 'public sealed class EditorDialogPresentationSnapshot' "$dialog_runtime" ||
   ! grep -Fq 'EditorDialogPresentationSnapshot dialog = DialogEditorPresentationHub.Current;' "$builtin_pages"; then
  echo "RWImGui Dialog page status references a missing/renamed EditorDialogPresentationSnapshot contract." >&2
  exit 1
fi

# Objects currently owns room-click placement and its scene workspace is a real compiled frontend
# source, not logic left behind in shared chrome. The declaration belongs to the page composition root.
if ! grep -Fq 'public override bool SupportsPlacementInput => true;' "$builtin_pages"; then
  echo "Objects page no longer declares the placement-input capability." >&2
  exit 1
fi
if ! grep -Fq 'page?.SupportsPlacementInput != true' "$overlay"; then
  echo "DevToolOverlay is not gating placement input through the page capability." >&2
  exit 1
fi
if ! grep -Fq 'ObjectSceneWorkspaceView.Draw(snapshot);' "$builtin_pages" ||
   ! grep -Fq 'internal static class ObjectSceneWorkspaceView' "$object_scene_workspace"; then
  echo "Objects Scene workspace is no longer owned by its compiled page-specific frontend view." >&2
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
