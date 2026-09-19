#!/usr/bin/env bash
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
