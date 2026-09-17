#!/usr/bin/env bash
set -euo pipefail

root="src/DevUI/DevTool"
reflection="$root/Objects/NativeDataReflectionInspector.cs"
bootstrap="$root/Objects/NativeObjectInspectorBootstrap.cs"
scheduler="$root/Core/NativeToolScheduler.cs"
anchor="$root/Core/NativeToolAnchorPage.cs"
controller="$root/Compatibility/ObjectGizmoPresentationController.cs"
frontend="$root/RWImGui/NativeSpatialGizmoView.cs"
geometry_frontend="$root/RWImGui/NativeObjectGeometryGizmoView.cs"
pages="$root/RWImGui/BuiltinDevToolPages.cs"
backend="$root/Gizmos/NativeGizmoCommandQueue.cs"
geometry_backend="$root/Gizmos/NativeObjectGeometryGizmoCommandQueue.cs"
coordinator="$root/Core/DevToolSubsystemCoordinator.cs"
factory="$root/Factories/NativePlacedObjectFactory.cs"

for file in "$reflection" "$bootstrap" "$scheduler" "$anchor" "$controller" "$frontend" \
            "$geometry_frontend" "$pages" "$backend" "$geometry_backend" "$coordinator" "$factory"; do
  if [[ ! -f "$file" ]]; then
    echo "Native Objects contract file missing: $file" >&2
    exit 1
  fi
done

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
# Data in the frontend or invent a parallel live-object snapshot.
if ! grep -Fq 'NativeObjectGeometryGizmoView.Draw(snapshot, display);' "$pages" ||
   ! grep -Fq 'key.EndsWith(".handlePos", StringComparison.Ordinal)' "$geometry_frontend" ||
   ! grep -Fq 'NativeObjectGeometryGizmoCommandQueue.Enqueue' "$geometry_frontend"; then
  echo "Native Objects secondary handle pipeline is incomplete." >&2
  exit 1
fi
if grep -Eq 'using DevInterface|global::DevInterface|PlacedObject|RoomSettings|ObjectsPage|PlacedObjectRepresentation|Reflection|GetField|GetProperty' "$geometry_frontend"; then
  echo "Native object geometry frontend regained live-model/reflection dependencies." >&2
  exit 1
fi
for symbol in \
  'SinglePlacedObjectStateSnapshot.Capture' \
  'EditorContinuousTransactionHub.Begin' \
  'EditorContinuousTransactionHub.Commit' \
  'EditorContinuousTransactionHub.Cancel' \
  'ObjectInspectorRegistry.TrySetValue' \
  'target.data?.RefreshLiveVisuals()' \
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

# The old selected-representation controller is intentionally inert. Rebuilt Objects does not create
# a representation tree, so there must be no dedicated Handle.Update hook left here.
if grep -Fq 'On.DevInterface.Handle.Update' "$controller"; then
  echo "Legacy Objects Handle.Update hook returned to ObjectGizmoPresentationController." >&2
  exit 1
fi
if ! grep -Fq 'no Handle.Update hook to install' "$controller"; then
  echo "ObjectGizmoPresentationController is no longer documented as an inert compatibility shim." >&2
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
if ! grep -Fq '[ModuleInitializer]' "$bootstrap" ||
   ! grep -Fq 'ObjectInspectorRegistry.Register(NativeDataReflectionInspector.Instance, -1000);' "$bootstrap"; then
  echo "Native reflected object inspector is no longer registered below explicit typed adapters." >&2
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
