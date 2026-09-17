#!/usr/bin/env bash
set -euo pipefail

root="src/DevUI/DevTool"
factories="$root/Factories"
registry="$factories/NativeAuthoringFactoryRegistry.cs"

required_factories=(
  "$factories/NativePlacedObjectFactory.cs"
  "$factories/NativeSoundFactory.cs"
  "$factories/NativeTriggerFactory.cs"
  "$factories/NativeRoomEffectFactory.cs"
)
for file in "$registry" "${required_factories[@]}"; do
  if [[ ! -f "$file" ]]; then
    echo "Native authoring factory contract is missing: $file" >&2
    exit 1
  fi
done

# Built-in authoring code must not quietly route back through DevInterface UI factories. Those
# calls are permitted only inside the explicit Legacy fallback implementation in Factories or in
# Compatibility. This keeps Page/Panel materialization from becoming authoritative again.
legacy_factory_hits="$(
  find "$root" -type f -name '*.cs' \
    ! -path "$factories/*" \
    ! -path "$root/Compatibility/*" \
    -print0 |
    xargs -0 grep -nE '\.(CreateObjRep|CreateSoundRep|CreateTriggerRep)\(' || true
)"
if [[ -n "$legacy_factory_hits" ]]; then
  echo "DevTool feature code bypasses the Native Factory Layer:" >&2
  echo "$legacy_factory_hits" >&2
  exit 1
fi

legacy_room_effect_hits="$(
  find "$root" -type f -name '*.cs' \
    ! -path "$factories/*" \
    ! -path "$root/Compatibility/*" \
    -print0 |
    xargs -0 grep -nE 'RoomSettingsPage.*Signal\(|\.Signal\(DevUISignalType\.Create' || true
)"
if [[ -n "$legacy_room_effect_hits" ]]; then
  echo "DevTool feature code routes RoomEffect creation through RoomSettingsPage.Signal(Create):" >&2
  echo "$legacy_room_effect_hits" >&2
  exit 1
fi

# The normal authoring entry points must explicitly route through native factories.
required_routes=(
  "$root/Commands/EditorActions.cs:NativePlacedObjectFactory.TryCreate"
  "$root/Sound/SoundEditorActions.cs:NativeSoundFactory.TryCreate"
  "$root/Triggers/TriggerEditorActions.cs:NativeTriggerFactory.TryCreate"
  "$root/Triggers/TriggerEditorActions.cs:NativeTriggeredEventFactory.TryAssign"
  "$root/Room/RoomEditorActions.cs:NativeRoomEffectFactory.TryCreate"
)
for route in "${required_routes[@]}"; do
  file="${route%%:*}"
  symbol="${route#*:}"
  if ! grep -Fq "$symbol" "$file"; then
    echo "Native Factory route missing: $file -> $symbol" >&2
    exit 1
  fi
done

# Every concrete native factory must consult the shared provider registry before deciding that a
# type is built-in or must fall back to Legacy. This keeps future extension support on one path.
required_provider_routes=(
  "$factories/NativePlacedObjectFactory.cs:NativeAuthoringFactoryRegistry.TryCreatePlacedObject"
  "$factories/NativeSoundFactory.cs:NativeAuthoringFactoryRegistry.TryCreateSound"
  "$factories/NativeTriggerFactory.cs:NativeAuthoringFactoryRegistry.TryCreateTrigger"
  "$factories/NativeTriggerFactory.cs:NativeAuthoringFactoryRegistry.TryCreateTriggeredEvent"
  "$factories/NativeRoomEffectFactory.cs:NativeAuthoringFactoryRegistry.TryCreateRoomEffect"
)
for route in "${required_provider_routes[@]}"; do
  file="${route%%:*}"
  symbol="${route#*:}"
  if ! grep -Fq "$symbol" "$file"; then
    echo "Native provider registry route missing: $file -> $symbol" >&2
    exit 1
  fi
done

required_registry_symbols=(
  'RegisterPlacedObject'
  'RegisterSound'
  'RegisterTrigger'
  'RegisterTriggeredEvent'
  'RegisterRoomEffect'
)
for symbol in "${required_registry_symbols[@]}"; do
  if ! grep -Fq "$symbol" "$registry"; then
    echo "Native authoring registry contract is incomplete: missing $symbol" >&2
    exit 1
  fi
done

# Legacy materialization must remain visibly isolated in factory files instead of becoming a second
# silent normal path. Unknown third-party ExtEnum IDs may still use it while public native provider
# contracts remain internal/frozen for validation.
for file in "${required_factories[@]}"; do
  if ! grep -Fq 'TryCreateLegacy' "$file" && [[ "$file" != *"NativeSoundFactory.cs" ]]; then
    echo "Factory lost its explicit Legacy fallback boundary: $file" >&2
    exit 1
  fi
done

if ! grep -Fq 'TryAssignLegacy' "$factories/NativeTriggerFactory.cs"; then
  echo "TriggeredEvent factory lost its explicit Legacy fallback boundary." >&2
  exit 1
fi

echo "DevTool Native Factory boundary guard passed."
