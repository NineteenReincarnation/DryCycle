#!/usr/bin/env bash
set -euo pipefail

root="src/DevUI/DevTool"
reflection="$root/Objects/NativeDataReflectionInspector.cs"
bootstrap="$root/Objects/NativeObjectInspectorBootstrap.cs"
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

for file in "$reflection" "$bootstrap" "$runtime_reconciler" "$detached_runtime_adapters" "$runtime_adapters" "$scheduler" "$anchor" "$quiescence" "$sandbox" "$frontend" \
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
if ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshAfterMutation(session, target);' "$backend" ||
   ! grep -Fq 'NativeObjectRuntimeReconciler.RefreshAfterMutation(session, target)' "$object_gizmo_backend" ||
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
  'runtime is AdjustableFan adjustableFan' \
  'DestroyRuntime(room, adjustableFan.FanElement)'; do
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
