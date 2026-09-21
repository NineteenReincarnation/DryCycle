#!/usr/bin/env bash
set -euo pipefail

fail=0

while IFS= read -r file; do
  [[ -z "$file" ]] && continue

  # Only independent BepInEx startup entrypoints matter here.
  if ! grep -Fq '[BepInPlugin(' "$file" || ! grep -Eq 'OnEnable[[:space:]]*\(' "$file"; then
    continue
  fi

  case "$file" in
    src/Plugin.cs)
      if ! grep -Fq 'StartupDiagnostics.Begin(Logger);' "$file" ||
         ! grep -Fq 'RollbackBootstrap();' "$file"; then
        echo "Primary DryCycle plugin lost guarded startup: $file" >&2
        fail=1
      fi
      continue
      ;;
    src/DevUI/DevTool/RWImGui/BridgePlugin.cs)
      if ! grep -Fq 'StartupDiagnostics.Marker("BridgePlugin.OnEnable", "ENTER")' "$file" ||
         ! grep -Fq 'ShutdownBridgeState();' "$file"; then
        echo "RWImGui bridge lost guarded startup: $file" >&2
        fail=1
      fi
      continue
      ;;
    src/DryCycle.AIObservatory.RWImGui/BridgePlugin.cs)
      if ! grep -Fq '[DryCycle.Startup][AIObservatoryBridge][BEGIN] OnEnable' "$file" ||
         ! grep -Fq '[DryCycle.Startup][AIObservatoryBridge][FAIL-OPTIONAL] OnEnable' "$file" ||
         ! grep -Fq 'ShutdownBridgeState("RWImGUI bridge startup failed")' "$file"; then
        echo "AI Observatory RWImGui bridge lost fail-open traced startup: $file" >&2
        fail=1
      fi
      continue
      ;;
  esac

  enable_count="$(grep -Ec 'OnEnable[[:space:]]*\(' "$file" || true)"
  guard_count="$(grep -Fc 'AuxiliaryPluginStartupGuard.Enable' "$file" || true)"
  if [[ "$guard_count" -lt "$enable_count" ]]; then
    echo "Auxiliary BepInEx OnEnable coverage is incomplete: $file (OnEnable=$enable_count, guarded=$guard_count)" >&2
    fail=1
  fi
done < <(find src -type f -name '*.cs' -print | sort)

if [[ "$fail" -ne 0 ]]; then
  exit 1
fi

echo "Auxiliary plugin startup guard passed."
