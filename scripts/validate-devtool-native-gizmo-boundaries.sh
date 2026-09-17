#!/usr/bin/env bash
set -euo pipefail

root="src/DevUI/DevTool"
backend="$root/Gizmos/NativeGizmoCommandQueue.cs"
viewport="$root/Core/EditorViewportPresentation.cs"
transaction="$root/History/EditorContinuousTransaction.cs"
frontend="$root/RWImGui/NativeSpatialGizmoView.cs"
pages="$root/RWImGui/BuiltinDevToolPages.cs"
retirement="$root/Compatibility/NativeLegacySpatialHandleRetirement.cs"
spatial_refresh="$root/Compatibility/LegacySpatialBackendRefresh.cs"
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
  "$backend" "$viewport" "$transaction" "$frontend" "$pages" "$retirement"
  "$spatial_refresh" "$legacy_invalidation" "$legacy_sound_hydrator" "$quiescence"
  "$sound_actions" "$trigger_actions" "$sound_runtime" "$sound_activation" "$sound_catalog"
  "$sound_resource_snapshot" "$sound_presentation" "$history" "$coordinator"
)
for file in "${required[@]}"; do
  if [[ ! -f "$file" ]]; then
    echo "Native gizmo contract file missing: $file" >&2
    exit 1
  fi
done

# RWImGui may only consume detached snapshots and enqueue commands. It must not regain live
# DevInterface/Rain World model ownership just because the gizmo is drawn over the room.
if grep -Eq 'using DevInterface|global::DevInterface|RoomCamera|RoomSettings|PlacedObject|AmbientSound|EventTrigger' "$frontend"; then
  echo "NativeSpatialGizmoView directly references backend/Rain World runtime objects." >&2
  exit 1
fi
if ! grep -Fq 'EditorViewportPresentationHub.Current' "$frontend" ||
   ! grep -Fq 'NativeGizmoCommandQueue.Enqueue' "$frontend"; then
  echo "Native gizmo frontend no longer follows detached snapshot -> command flow." >&2
  exit 1
fi

# Sound/Trigger pages must actually expose the native spatial layer; otherwise the backend can drift
# into dead infrastructure while the old Handle tree quietly returns.
if ! grep -Fq 'NativeSpatialGizmoView.DrawSound' "$pages" ||
   ! grep -Fq 'NativeSpatialGizmoView.DrawTriggers' "$pages"; then
  echo "Sound/Trigger pages are no longer routed through NativeSpatialGizmoView." >&2
  exit 1
fi

# Interactive drags are one semantic transaction. Intermediate mouse frames must not become hundreds
# of Undo entries.
for symbol in 'EditorContinuousTransactionHub.Begin' 'EditorContinuousTransactionHub.Commit' 'EditorContinuousTransactionHub.Cancel'; do
  if ! grep -Fq "$symbol" "$backend"; then
    echo "Native gizmo transaction boundary missing: $symbol" >&2
    exit 1
  fi
done

# Built-in Sound/Trigger legacy spatial presentation is retired as nodes, not intercepted through a
# new hook. Returning to Vanilla may recreate it, but normal rebuilt authoring must not keep a hidden
# Panel/Handle refresh pipeline alive.
if grep -Eq 'On\.DevInterface|IL\.DevInterface' "$retirement"; then
  echo "Legacy spatial retirement introduced a new DevInterface hook." >&2
  exit 1
fi
if ! grep -Fq 'NativeLegacySpatialHandleRetirement.Apply(session)' "$coordinator"; then
  echo "Native legacy spatial retirement is not owned by the backend coordinator." >&2
  exit 1
fi
if grep -Eq 'new[[:space:]]+(AmbientSoundPanel|TriggerPanel|SpotSoundHandle|DirectionalSoundHandle|SpotTriggerHandle)[[:space:]]*\(' "$spatial_refresh"; then
  echo "Legacy backend refresh started rebuilding Sound/Trigger Panel/Handle nodes." >&2
  exit 1
fi
if grep -Eq 'TryRefreshSound|TryRefreshTriggers|ReconcileAmbientPlayers' "$spatial_refresh"; then
  echo "LegacySpatialBackendRefresh regained a Sound/Trigger backend path." >&2
  exit 1
fi

# Native Sound runtime ownership is separate from DevInterface presentation. Collection membership is
# reconciled directly against AmbientSoundPlayer, while scalar/spatial model fields stay live by
# reference and therefore do not need a page rebuild.
if ! grep -Fq 'new AmbientSoundPlayer' "$sound_runtime" ||
   ! grep -Fq 'ReferenceEquals(player.aSound, sound)' "$sound_runtime"; then
  echo "Native Sound runtime reconciler no longer owns ambient player membership." >&2
  exit 1
fi
if grep -Eq 'using DevInterface|global::DevInterface|new[[:space:]]+(AmbientSoundPanel|SpotSoundHandle|DirectionalSoundHandle)[[:space:]]*\(' "$sound_runtime"; then
  echo "Native Sound runtime reconciler regained DevInterface presentation dependencies." >&2
  exit 1
fi

# Resource discovery/indexing is now a truly headless Sound subsystem. Neither activation nor sample
# catalog nor the immutable resource snapshot may know that SoundPage exists. Presentation consumes
# the headless filename generation directly; only Compatibility may project it back into page.fileNames.
for file in "$sound_activation" "$sound_catalog" "$sound_resource_snapshot"; do
  if grep -Eq 'using DevInterface|global::DevInterface|SoundPage|AmbientSoundPanel|RefreshFilesPage|\.fileNames' "$file"; then
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

# Sound/Trigger native actions may mutate game models and history, but they must not call the hidden
# legacy page Refresh path. The only immediate Refresh allowed is isolated in the compatibility
# fallback used when defer is refused for Vanilla/opaque third-party DevUI.
if grep -Eq 'activePage[^;]*Refresh|SoundPage[^;]*Refresh|TriggersPage[^;]*Refresh' "$sound_actions" "$trigger_actions"; then
  echo "Native Sound/Trigger action code calls a legacy page Refresh." >&2
  exit 1
fi
if ! grep -Fq 'page.Refresh();' "$legacy_invalidation"; then
  echo "Sound/Trigger compatibility fallback no longer has an explicit legacy refresh boundary." >&2
  exit 1
fi

# Sound/Trigger no longer need Refresh hooks. Objects remains the only migrated workspace with a
# reduced legacy Refresh interception until its representation/gizmo backend is native too.
if grep -Eq 'On\.DevInterface\.(SoundPage|TriggersPage)\.Refresh' "$quiescence"; then
  echo "Sound/Trigger Refresh hooks returned to the quiescence controller." >&2
  exit 1
fi
if ! grep -Fq 'On.DevInterface.ObjectsPage.Refresh += ObjectsPage_Refresh;' "$quiescence"; then
  echo "Objects transitional Refresh hook unexpectedly disappeared during Sound/Trigger migration." >&2
  exit 1
fi

# History is the authoritative stale-presentation boundary. Pure rebuilt pages defer; when defer is
# refused, Sound/Trigger alone may invoke the compatibility fallback. Undo/Redo must also reconcile
# the real Sound runtime because snapshot restore can replace collection members by reference.
if ! grep -Fq 'NativeLegacyPresentationInvalidation.RefreshCurrentSoundOrTriggerFallback(session)' "$history"; then
  echo "History no longer owns Sound/Trigger legacy fallback invalidation." >&2
  exit 1
fi
if ! grep -Fq 'ReconcileRuntimeAfterHistoryRestore(session)' "$history" ||
   ! grep -Fq 'NativeSoundRuntimeReconciler.Reconcile(session)' "$coordinator"; then
  echo "Undo/Redo no longer reconciles native Sound runtime state." >&2
  exit 1
fi

# Two pre-existing Handle.Update hooks are intentionally allowed during the migration:
# 1) EditorInputRouter is the single global mouse-arbitration boundary that prevents clicks captured
#    by ImGui from starting unrelated legacy handle drags underneath the overlay;
# 2) ObjectGizmoPresentationController temporarily owns Objects-only legacy gizmo suppression until
#    native object gizmo coverage is complete.
# Sound/Trigger migration must not add any additional Handle.Update interception point.
handle_hook_hits="$(grep -R -n -E 'On\.DevInterface\.Handle\.Update' "$root" --include='*.cs' || true)"
if [[ -n "$handle_hook_hits" ]]; then
  invalid="$(printf '%s\n' "$handle_hook_hits" |
    grep -v '/Compatibility/ObjectGizmoPresentationController.cs:' |
    grep -v '/Input/EditorInputRouter.cs:' || true)"
  if [[ -n "$invalid" ]]; then
    echo "A new DevInterface.Handle.Update hook was introduced:" >&2
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
