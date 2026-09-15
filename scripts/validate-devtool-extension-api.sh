#!/usr/bin/env bash
set -euo pipefail

api_dir="src/DevUI/DevTool/Extensions"
api_file="$api_dir/DevToolExtensionApi.cs"
phase_doc="src/DevUI/DevTool/PHASE5.md"

if [[ ! -f "$api_file" ]]; then
  echo "DevTool extension API entry point is missing: $api_file" >&2
  exit 1
fi

# The public ABI must stay backend-neutral and must not grow compile/runtime coupling to a
# third-party framework. Scan C# only so documentation can explain these forbidden dependencies.
forbidden='ImGuiNET|RWImGui|RegionKit|PomManaged|POM|Fisobs|M4r|BepInEx\.Bootstrap';
if grep -RInE --include='*.cs' "$forbidden" "$api_dir"; then
  echo "DevTool public extension API contains a forbidden frontend/third-party dependency." >&2
  exit 1
fi

required_symbols=(
  'public static class DevToolApi'
  'public readonly struct DevToolApiVersion'
  'public enum DevToolCapability'
  'public sealed class DevToolExtensionScope'
  'public sealed class DevToolRegistration'
  'TryRegisterExtension'
  'RegisterObjectDescriptor'
  'RegisterInspector'
  'GetExtensions'
)

for symbol in "${required_symbols[@]}"; do
  if ! grep -Fq "$symbol" "$api_file"; then
    echo "DevTool extension API contract guard: missing $symbol" >&2
    exit 1
  fi
done

if [[ ! -f "$phase_doc" ]]; then
  echo "Phase 5 contract documentation is missing: $phase_doc" >&2
  exit 1
fi

# Stage 5 deliberately keeps custom frontend drawing out of the ABI. If this sentence disappears,
# force a deliberate review rather than silently widening the public compatibility contract.
if ! grep -Fq 'ImGui / RWImGui 绘制回调' "$phase_doc"; then
  echo "Phase 5 boundary documentation no longer records the frontend-injection exclusion." >&2
  exit 1
fi

echo "DevTool extension API architecture guard passed."
