#!/usr/bin/env bash
set -euo pipefail

# DevTool Architecture Guard
#
# Only keep cheap, static dependency-boundary checks here.
# Runtime behavior, private class/method names, hook topology/order, performance strategy,
# Phase/Stage history and UI composition details belong in compilation, tests or AGENTS.md.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

root="src/DevUI/DevTool"
api_dir="$root/Extensions"
frontend="$root/RWImGui"

if [[ ! -d "$root" ]]; then
  echo "DevTool source root is missing: $root" >&2
  exit 1
fi

# The public extension surface must remain backend-neutral. Third-party integrations belong in
# adapters/compatibility code, not in the API contract that every extension consumer compiles against.
if [[ -d "$api_dir" ]]; then
  api_dependency_hits="$(
    grep -RInE --include='*.cs'       '(^|[^A-Za-z0-9_])(ImGuiNET|RWImGui|RegionKit|PomManaged|Fisobs|M4r)([^A-Za-z0-9_]|$)'       "$api_dir" || true
  )"
  if [[ -n "$api_dependency_hits" ]]; then
    echo "DevTool public extension surface acquired a frontend/third-party implementation dependency:" >&2
    echo "$api_dependency_hits" >&2
    exit 1
  fi
fi

# Backend code must not compile against the optional RWImGui/ImGui frontend. This is deliberately
# broad and name-based only at the assembly/framework boundary; private page/view names are not frozen.
backend_frontend_hits="$(
  find "$root" -type f -name '*.cs' ! -path "$frontend/*" -print0 |
    xargs -0 -r grep -nE       '(^|[^A-Za-z0-9_])(ImGuiNET|rain_world_imgui_api|DryCycle\.DevUI\.DevTool\.RWImGui)([^A-Za-z0-9_]|$)'       || true
)"
if [[ -n "$backend_frontend_hits" ]]; then
  echo "DevTool backend acquired an optional RWImGui/ImGui frontend dependency:" >&2
  echo "$backend_frontend_hits" >&2
  exit 1
fi

echo "DevTool architecture guard passed: public extension and backend/frontend dependency boundaries are intact."
