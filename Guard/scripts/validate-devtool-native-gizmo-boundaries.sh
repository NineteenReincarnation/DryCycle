#!/usr/bin/env bash
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
