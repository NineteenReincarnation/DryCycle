#!/usr/bin/env bash
set -euo pipefail

root="src/DevUI/DevTool"
sound_runtime="$root/Sound/SoundEditorRuntime.cs"
trigger_runtime="$root/Triggers/TriggerEditorRuntime.cs"
trigger_catalog="$root/Triggers/TriggerSongCatalog.cs"
selection_bridge="$root/Compatibility/LegacySoundTriggerSelectionBridge.cs"
sound_hydrator="$root/Compatibility/LegacySoundPageHydrator.cs"
trigger_hydrator="$root/Compatibility/LegacyTriggerPageHydrator.cs"
coordinator="$root/Core/DevToolSubsystemCoordinator.cs"

for file in "$sound_runtime" "$trigger_runtime" "$trigger_catalog" "$selection_bridge" \
            "$sound_hydrator" "$trigger_hydrator" "$coordinator"; do
  if [[ ! -f "$file" ]]; then
    echo "Native Sound/Trigger runtime contract file missing: $file" >&2
    exit 1
  fi
done

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

echo "DevTool Native Sound/Trigger runtime boundary guard passed."
