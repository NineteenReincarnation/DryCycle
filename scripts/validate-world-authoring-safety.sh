#!/usr/bin/env bash
set -euo pipefail

resolver="src/DevUI/DevTool/World/WorldAuthoringPathResolver.cs"
world_text="src/DevUI/DevTool/World/WorldTextRegistry.cs"
room_attr="src/DevUI/DevTool/World/WorldRoomAttractionRegistry.cs"

for file in "$resolver" "$world_text" "$room_attr"; do
  if [[ ! -f "$file" ]]; then
    echo "World authoring safety file missing: $file" >&2
    exit 1
  fi
done

# Editable world data must never use Rain World's generated mergedmods file as its save target.
if ! grep -Fq 'IsMergedCachePath' "$resolver" ||
   ! grep -Fq 'Path.Combine(root, "mergedmods")' "$resolver" ||
   ! grep -Fq 'return string.Empty;' "$resolver"; then
  echo "World authoring resolver no longer fail-closes on mergedmods targets." >&2
  exit 1
fi

if ! grep -Fq 'WorldAuthoringPathResolver.ResolveExistingSource(relativePath)' "$world_text"; then
  echo "world.txt authoring bypasses the real-source resolver." >&2
  exit 1
fi

if ! grep -Fq 'WorldAuthoringPathResolver.ResolveExistingSource(relativeBase)' "$room_attr" ||
   ! grep -Fq 'WorldAuthoringPathResolver.ResolveExistingSource(relativeTimeline)' "$room_attr"; then
  echo "Room_Attr authoring bypasses the real-source resolver." >&2
  exit 1
fi

# If a timeline-specific Properties file is the runtime winner, authoring must not silently fall
# back to base Properties.txt when that winner exists only as generated merged data.
if ! grep -Fq 'ResolveReadPath(relativeTimeline)' "$room_attr" ||
   ! grep -Fq 'return WorldAuthoringPathResolver.ResolveExistingSource(relativeTimeline);' "$room_attr"; then
  echo "Timeline-specific Properties authoring can silently fall back to the wrong source." >&2
  exit 1
fi

# Room_Attr editing must fail before mutation when there is no writable source.
if ! grep -Fq 'DryCycle will not author generated mergedmods data.' "$room_attr"; then
  echo "Room_Attr editor can enter dirty state without a writable source file." >&2
  exit 1
fi

# MapPage.filePath may point at AssetManager's mergedmods winner. Rebuilt, fallback and Vanilla
# Map saves must all resolve a real source before writing.
map_pipeline="src/DevUI/DevTool/Map/PlayerMap/PlayerMapConfigBuildPipeline.cs"
map_runtime="src/DevUI/DevTool/Map/PlayerMap/PlayerMapWorkspaceRuntime.cs"
if ! grep -Fq 'TryResolveMapConfigSource' "$map_pipeline" ||
   ! grep -Fq 'AtomicWriteAllLines(authoringPath' "$map_pipeline"; then
  echo "Rebuilt Player Map writer can bypass the real-source authoring path." >&2
  exit 1
fi
if ! grep -Fq 'SaveVanillaMapConfigToAuthoringSource' "$map_runtime" ||
   ! grep -Fq 'TryResolveMapConfigSource' "$map_runtime" ||
   ! grep -Fq 'AtomicWriteAllLines(authoringPath' "$map_runtime"; then
  echo "Fallback or Vanilla MapPage save can bypass the real-source authoring path." >&2
  exit 1
fi

# Unsaved Player Map state must survive MapPage rebuilds and region switches. A same-region
# replacement is rebound; a different region is blocked until the retained dirty document is saved.
if ! grep -Fq 'Player Map rebound unsaved authoring state to a replacement MapPage' "$map_runtime" ||
   ! grep -Fq 'Unsaved Player Map belongs to another region' "$map_runtime" ||
   ! grep -Fq 'if (!EnsureStateForPage(page, state))' "$map_runtime"; then
  echo "Player Map can lose unsaved authoring state across MapPage/region replacement." >&2
  exit 1
fi

# Every map save surface must converge on the same Core transaction. The RWImGui toolbar may only
# enqueue Save; it must not race the Core command by writing individual files itself.
editor_actions="src/DevUI/DevTool/Commands/EditorActions.cs"
world_view="src/DevUI/DevTool/RWImGui/WorldWorkspaceView.cs"
if ! grep -Fq 'SaveMapWorkspace(session, map)' "$editor_actions" ||
   ! grep -Fq 'PlayerMapWorkspaceRuntime.IsDirty(session)' "$editor_actions" ||
   ! grep -Fq 'WorldRoomAttractionRegistry.Save()' "$editor_actions" ||
   ! grep -Fq 'WorldTextRegistry.Save()' "$editor_actions"; then
  echo "Canonical map save path does not persist dirty world documents through Core." >&2
  exit 1
fi

world_data_view="src/DevUI/DevTool/RWImGui/WorldWorkspaceDataView.cs"
if ! grep -Fq 'EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.Save))' "$world_data_view"; then
  echo "World Data save button bypasses the canonical Core save transaction." >&2
  exit 1
fi
if grep -Fq '            SaveDirty();' "$world_data_view"; then
  echo "World Data UI still persists files directly instead of queuing Core Save." >&2
  exit 1
fi

if grep -Eq 'World(RoomAttractionRegistry|TextRegistry|TopologyRegistry)\.Save\(|WorldWorkspaceDataView\.SaveDirty\(' "$world_view"; then
  echo "World Workspace UI writes files directly instead of routing through the Core save command." >&2
  exit 1
fi

echo "World authoring source safety guard passed."
