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
   ! grep -Fq 'DryCycleLifecycleEvents.RaiseAfterModsInit(self)' "$plugin"; then
  echo "DryCycle post-mod startup no longer has the fail-open rollback contract." >&2
  exit 1
fi

# Primary plugin shutdown is also a rollback boundary. One broken subsystem cleanup must never
# stop the remaining hooks from being detached before a reload/shutdown completes.
if ! grep -Fq 'StartupDiagnostics.Marker("Plugin.OnDisable", "ENTER")' "$plugin" ||
   ! grep -Fq 'SafeBootstrapCleanup("OnDisable/' "$plugin" ||
   ! grep -Fq 'RollbackRuntimeInitialization();' "$plugin" ||
   ! grep -Fq 'StartupDiagnostics.Marker("Plugin.OnDisable", "EXIT")' "$plugin"; then
  echo "Primary plugin shutdown is no longer failure-independent/traced." >&2
  exit 1
fi

# DevConsole reset/registration is optional tooling. It must never be able to abort RainWorld's
# PreModsInit phase; every reset remains behind the non-throwing startup diagnostic wrapper.
for optional_reset in   'ScavengerLanceDevConsoleSupport.ResetRegistration'   'CreatureDevConsoleSupport.ResetRegistration'   'RopeSpearDevConsoleSupport.ResetRegistration'   'KarmaSpearDevConsoleSupport.ResetRegistration'   'SpinebackLizardDevConsoleSupport.ResetRegistration'; do
  if ! grep -Fq "StartupDiagnostics.Optional(\"RainWorld.PreModsInit/$optional_reset\"" "$plugin"; then
    echo "PreModsInit DevConsole reset can propagate into startup: $optional_reset" >&2
    exit 1
  fi
done

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

# Custom creature ExtEnums/descriptors are process-lifetime registrations. The core registry must
# be installed transactionally before descriptor creation and must not be removed while registered
# creature IDs remain globally visible to WorldLoader/StaticWorld.
creature_registry="src/Framework/Creature/Core/CreatureRegistry.cs"
if ! grep -Fq '_staticWorldTemplateHookInstalled' "$creature_registry" ||
   ! grep -Fq 'RemoveInstalledHooksBestEffort("enable rollback")' "$creature_registry" ||
   ! grep -Fq '_worldLoaderTypeHookInstalled' "$creature_registry"; then
  echo "CreatureRegistry hook installation is no longer transactional." >&2
  exit 1
fi
core_line="$(grep -n -F 'Plugin.OnEnable/CreatureCoreRegistry.Enable' "$plugin" | head -n1 | cut -d: -f1)"
moss_line="$(grep -n -F 'Plugin.OnEnable/MossySpiderDefinition.Register' "$plugin" | head -n1 | cut -d: -f1)"
if [[ -z "$core_line" || -z "$moss_line" || "$core_line" -ge "$moss_line" ]]; then
  echo "Creature core registry must be enabled before irreversible creature descriptor registration." >&2
  exit 1
fi
if ! grep -Fq 'PreserveCreatureCoreRegistryIfRegistered' "$plugin" ||
   grep -Fq 'SafeBootstrapCleanup("Creature Core registry", CreatureCoreRegistry.Disable)' "$plugin" ||
   grep -Fq 'SafeBootstrapCleanup("OnDisable/CreatureCoreRegistry.Disable", CreatureCoreRegistry.Disable)' "$plugin"; then
  echo "Primary plugin can detach CreatureRegistry while irreversible descriptors remain registered." >&2
  exit 1
fi
if ! grep -Fq 'SKIP-ALREADY-REGISTERED' "$plugin"; then
  echo "Creature registration bootstrap is not idempotent after partial registration." >&2
  exit 1
fi

# Optional audio codecs must never become an assembly-load prerequisite for DryCycle.dll.
# NAudio is runtime-discovered through reflection; compile-time type references can make BepInEx
# fail before Plugin.OnEnable and before startup diagnostics exist.
if grep -R --include='*.cs' -E '(^|[[:space:]])using[[:space:]]+NAudio\.|NAudio\.(Wave|CoreAudioApi|MediaFoundation)' src/Misc/SoundFormatSupport; then
  echo "SoundFormatSupport reintroduced a compile-time NAudio type dependency." >&2
  exit 1
fi
if ! grep -A4 -F '<PackageReference Include="NAudio.Wasapi" Version="2.2.1">' src/DryCycle.csproj |
     grep -Fq '<ExcludeAssets>compile</ExcludeAssets>'; then
  echo "NAudio.Wasapi must remain runtime-only so DryCycle.dll has no hard codec assembly dependency." >&2
  exit 1
fi
if ! grep -Fq 'Type.GetType(' src/Misc/SoundFormatSupport/ExternalAudioLoader.cs ||
   ! grep -Fq 'NAudio.Wave.MediaFoundationReader, NAudio.Wasapi' src/Misc/SoundFormatSupport/ExternalAudioLoader.cs; then
  echo "Optional Media Foundation backend is no longer reflection-isolated." >&2
  exit 1
fi

echo "DryCycle startup-safety guard passed."
