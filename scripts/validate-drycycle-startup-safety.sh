#!/usr/bin/env bash
set -euo pipefail

plugin="src/Plugin.cs"
lifecycle="src/DryCycleLifecycleEvents.cs"
misc="src/Misc/MiscRuntime.cs"
audio="src/Misc/SoundFormatSupport/SoundFormatSupportRuntime.cs"
bridge="src/DevUI/DevTool/RWImGui/BridgePlugin.cs"

for file in "$plugin" "$lifecycle" "$misc" "$audio" "$bridge"; do
  if [[ ! -f "$file" ]]; then
    echo "Startup-safety contract file missing: $file" >&2
    exit 1
  fi
done

# Rain World lifecycle hooks belong to the core plugin. Optional frontends subscribe to the shared
# DryCycle event surface instead of stacking their own hooks on the same startup phases.
for hook in   'On.RainWorld.PreModsInit += RainWorld_PreModsInit'   'On.RainWorld.OnModsInit += RainWorld_OnModsInit'   'On.RainWorld.PostModsInit += RainWorld_PostModsInit'; do
  if ! grep -Fq "$hook" "$plugin"; then
    echo "Core Rain World lifecycle ownership is missing: $hook" >&2
    exit 1
  fi
done

for phase in BeforePreModsInit AfterPreModsInit BeforeModsInit AfterModsInit; do
  if ! grep -Fq "internal static event Action<RainWorld> $phase;" "$lifecycle"; then
    echo "Shared DryCycle lifecycle phase is missing: $phase" >&2
    exit 1
  fi
done

if grep -Eq 'On\.RainWorld\.(PreModsInit|OnModsInit)[[:space:]]*\+=' "$bridge"; then
  echo "RWImGui frontend regained direct RainWorld startup hooks; use DryCycleLifecycleEvents." >&2
  exit 1
fi
for subscription in   'DryCycleLifecycleEvents.BeforePreModsInit +='   'DryCycleLifecycleEvents.AfterPreModsInit +='   'DryCycleLifecycleEvents.BeforeModsInit +='   'DryCycleLifecycleEvents.AfterModsInit +='; do
  if ! grep -Fq "$subscription" "$bridge"; then
    echo "RWImGui frontend lost shared lifecycle subscription: $subscription" >&2
    exit 1
  fi
done

# Both BepInEx bootstrap surfaces must fail open. A single DryCycle hook mismatch may disable the
# affected subsystem/plugin, but must not propagate an exception that prevents Rain World startup.
if ! grep -Fq 'DryCycle bootstrap failed during OnEnable. Partial hooks are being rolled back so Rain World can continue loading.' "$plugin" ||
   ! grep -Fq 'RollbackBootstrap();' "$plugin" ||
   ! grep -Fq 'SafeBootstrapCleanup' "$plugin" ||
   ! grep -Fq 'StartupDiagnostics.Begin(Logger);' "$plugin" ||
   ! grep -Fq 'StartupDiagnostics.Failure("Plugin.OnEnable", error);' "$plugin"; then
  echo "Core Plugin.OnEnable no longer has transactional fail-open rollback." >&2
  exit 1
fi
if ! grep -Fq 'DryCycle DevTool RWImGui frontend failed during OnEnable and has been isolated; Rain World startup will continue.' "$bridge" ||
   ! grep -Fq 'ShutdownBridgeState();' "$bridge"; then
  echo "RWImGui BridgePlugin.OnEnable no longer has fail-open isolation." >&2
  exit 1
fi

# Optional compatibility must fail open. SlugBase discovery and DevTool/audio initialization are not
# allowed to make Rain World's OnModsInit fail solely because an optional integration is broken.
if ! grep -Fq 'TryInitializeSlugBaseHydrationFeatures();' "$plugin" ||
   ! grep -Fq 'StartupDiagnostics.Step("RainWorld.OnModsInit/' "$plugin" ||
   ! grep -Fq 'StartupDiagnostics.Failure("RainWorld.OnModsInit/PostModTransaction", ex);' "$plugin"; then
  echo "SlugBase hydration compatibility is no longer guarded during startup." >&2
  exit 1
fi
if ! grep -Fq 'DryCycle post-mod initialization failed; the failing runtime transaction was rolled back so Rain World can continue loading.' "$plugin" ||
   ! grep -Fq 'DryCycleLifecycleEvents.RaiseAfterModsInit(self);' "$plugin"; then
  echo "DryCycle post-mod startup no longer has the fail-open rollback contract." >&2
  exit 1
fi

# MP3/M4A overrides are hydrated lazily at SoundClipReady after native release. Running a blocking
# all-audio hydration from Plugin.OnModsInit would move file decode back onto the startup critical path.
if grep -Fq 'SoundFormatSupportRuntime.HydrateExisting' "$plugin"; then
  echo "Blocking custom-audio hydration returned to Plugin.OnModsInit." >&2
  exit 1
fi

# DevTool and loose-format audio are optional surfaces. Their initialization/cleanup must stay
# isolated from the core Misc runtime and partial hook installation must be reversible.
if ! grep -Fq 'TryEnableDevToolBackend();' "$misc" ||
   ! grep -Fq 'DryCycle DevTool backend failed to initialize and has been disabled; gameplay startup will continue.' "$misc" ||
   ! grep -Fq 'DisableDevToolBackendSafely();' "$misc"; then
  echo "DevTool backend is no longer isolated from gameplay startup." >&2
  exit 1
fi
if ! grep -Fq 'TryEnableSoundFormatSupport();' "$misc" ||
   ! grep -Fq 'Optional sound-format support failed to initialize and has been disabled; Rain World startup will continue.' "$misc"; then
  echo "Optional sound-format support is no longer isolated from gameplay startup." >&2
  exit 1
fi
if ! grep -Fq 'catch' "$audio" ||
   ! grep -Fq 'RemoveOnHooks();' "$audio" ||
   ! grep -Fq 'enabled = false;' "$audio"; then
  echo "Sound-format hook installation is no longer transactional." >&2
  exit 1
fi

echo "DryCycle startup-safety guard passed."
