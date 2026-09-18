#!/usr/bin/env bash
set -euo pipefail

root="src/DevUI/DevTool"
reflection="$root/Objects/NativeDataReflectionInspector.cs"
bootstrap="$root/Objects/NativeObjectInspectorBootstrap.cs"
runtime_reconciler="$root/Objects/NativeObjectRuntimeReconciler.cs"
scheduler="$root/Core/NativeToolScheduler.cs"
anchor="$root/Core/NativeToolAnchorPage.cs"
removed_controller="$root/Compatibility/ObjectGizmoPresentationController.cs"
sandbox="$root/Compatibility/LegacyObjectSandbox.cs"
quiescence="$root/Compatibility/LegacyDevUiQuiescenceController.cs"
removed_spatial_refresh="$root/Compatibility/LegacySpatialBackendRefresh.cs"
frontend="$root/RWImGui/NativeSpatialGizmoView.cs"
geometry_frontend="$root/RWImGui/NativeObjectGeometryGizmoView.cs"
pages="$root/RWImGui/BuiltinDevToolPages.cs"
backend="$root/Gizmos/NativeGizmoCommandQueue.cs"
geometry_backend="$root/Gizmos/NativeObjectGeometryGizmoCommandQueue.cs"
coordinator="$root/Core/DevToolSubsystemCoordinator.cs"
factory="$root/Factories/NativePlacedObjectFactory.cs"

for file in "$reflection" "$bootstrap" "$runtime_reconciler" "$scheduler" "$anchor" "$quiescence" "$sandbox" "$frontend" \
            "$geometry_frontend" "$pages" "$backend" "$geometry_backend" "$coordinator" "$factory"; do
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

# Secondary handle geometry is derived from the already-detached native inspector payload. The first
# intentionally conservative pattern is Rain World's ubiquitous handlePos offset; do not re-reflect
# Data in the frontend or invent a parallel live-object snapshot. Comments may document the model
# source, so reject only actual namespace/type/reflection API use.
if ! grep -Fq 'NativeObjectGeometryGizmoView.Draw(snapshot, display);' "$pages" ||
   ! grep -Fq 'property.GizmoHint == EditorPropertyGizmoHint.None' "$geometry_frontend" ||
   ! grep -Fq 'EditorPropertyGizmoHint.VerticalDistance' "$geometry_frontend" ||
   ! grep -Fq 'NativeObjectGeometryGizmoCommandQueue.Enqueue' "$geometry_frontend"; then
  echo "Native Objects secondary handle pipeline is incomplete." >&2
  exit 1
fi
if grep -Eq '^using DevInterface;|global::DevInterface|typeof\(PlacedObject\)|PlacedObject[[:space:]]+[A-Za-z_]|RoomSettings[[:space:]]+[A-Za-z_]|typeof\(ObjectsPage\)|ObjectsPage[[:space:]]+[A-Za-z_]|PlacedObjectRepresentation[[:space:]]+[A-Za-z_]|System\.Reflection|BindingFlags|GetField[[:space:]]*\(|GetProperty[[:space:]]*\(' "$geometry_frontend"; then
  echo "Native object geometry frontend regained live-model/reflection dependencies." >&2
  exit 1
fi
for symbol in \
  'SinglePlacedObjectStateSnapshot.Capture' \
  'EditorContinuousTransactionHub.Begin' \
  'EditorContinuousTransactionHub.Commit' \
  'EditorContinuousTransactionHub.Cancel' \
  'ObjectInspectorRegistry.TrySetValue' \
  'NativeObjectRuntimeReconciler.RefreshAfterMutation(session, target)' \
  'ObjectPresentationChangeHintHub.MarkMember'; do
  if ! grep -Fq "$symbol" "$geometry_backend"; then
    echo "Native object geometry backend lost transaction/model behavior: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'NativeObjectGeometryGizmoCommandQueue.Process(session);' "$coordinator" ||
   ! grep -Fq 'NativeObjectGeometryGizmoCommandQueue.Clear();' "$coordinator"; then
  echo "Native object geometry queue is not owned by the backend coordinator lifetime." >&2
  exit 1
fi

# Representation-specific model/runtime side effects move into native services. TerrainHandle is
# the first required contract: any position/geometry/history mutation must update TerrainCurve.
for symbol in \
  'target.data is not PlacedObject.TerrainHandleData' \
  'TerrainManager.ITerrain' \
  'terrain is TerrainCurve curve' \
  'curve.UpdateHandles();'; do
  if ! grep -Fq "$symbol" "$runtime_reconciler"; then
    echo "Native object runtime reconciler lost TerrainHandle behavior: $symbol" >&2
    exit 1
  fi
done
if ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshAfterMutation(session, target);' "$backend" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshAfterMutation(session, target);' "$geometry_backend" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshAfterMutation(session, target);' "$root/History/PlacedObjectHistory.cs" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshAfterMutation(session, target)' "$root/Commands/EditorActions.cs"; then
  echo "Native object mutations no longer converge on the runtime side-effect reconciler." >&2
  exit 1
fi

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
