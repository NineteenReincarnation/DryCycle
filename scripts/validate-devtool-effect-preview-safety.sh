#!/usr/bin/env bash
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

# RuntimeDetour is never allowed back into preview safety.
if grep -R -n -E --include='*.cs'   'MonoMod\.RuntimeDetour|using[[:space:]]+MonoMod\.RuntimeDetour|new[[:space:]]+Hook\('   "$preview_root" >/tmp/effect_preview_detours.txt 2>/dev/null; then
  echo "Effect preview regained RuntimeDetour usage:" >&2
  cat /tmp/effect_preview_detours.txt >&2
  exit 1
fi

echo "DevTool effect preview safety guard passed."
