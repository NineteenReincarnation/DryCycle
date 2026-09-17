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
coordinator="$root/Core/DevToolSubsystemCoordinator.cs"

required=("$backend" "$viewport" "$transaction" "$frontend" "$pages" "$retirement" "$spatial_refresh" "$coordinator")
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
# new hook. The minimal refresh path may reconcile AmbientSoundPlayer runtime state, but it must never
# rematerialize vanilla Panel/Handle trees behind the native editor.
if grep -Eq 'On\.DevInterface|IL\.DevInterface' "$retirement"; then
  echo "Legacy spatial retirement introduced a new DevInterface hook." >&2
  exit 1
fi
if ! grep -Fq 'NativeLegacySpatialHandleRetirement.Apply(session)' "$coordinator"; then
  echo "Native legacy spatial retirement is not owned by the backend coordinator." >&2
  exit 1
fi
if grep -Eq 'new[[:space:]]+(AmbientSoundPanel|TriggerPanel|SpotSoundHandle|DirectionalSoundHandle|SpotTriggerHandle)[[:space:]]*\(' "$spatial_refresh"; then
  echo "Native Sound/Trigger backend refresh started rebuilding legacy Panel/Handle nodes." >&2
  exit 1
fi
if ! grep -Fq 'ReconcileAmbientPlayers(page)' "$spatial_refresh"; then
  echo "Native Sound backend no longer reconciles the real ambient runtime." >&2
  exit 1
fi
if ! grep -Fq 'PruneBuiltinSoundNodes(page)' "$spatial_refresh" ||
   ! grep -Fq 'PruneBuiltinTriggerNodes(page)' "$spatial_refresh"; then
  echo "Native Sound/Trigger refresh no longer prunes stale built-in legacy nodes." >&2
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
