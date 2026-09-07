#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
./scripts/check-desertbatfly-r5-retention.sh

for f in \
  src/Creatures/DesertBatfly/Integration/RainWorld/DB_RainWorldHooks.cs \
  src/Creatures/DesertBatfly/Integration/RainWorld/DB_RuntimePatch.cs \
  src/Creatures/DesertBatfly/Integration/Sandbox/DB_Sandbox.cs \
  src/Creatures/DesertBatfly/Integration/Warp/DB_WarpCompatibility.cs \
  src/Debug/AIDebugger/Sources/DB_ObservatorySource.cs \
  src/Debug/AIDebugger/Sources/DB_TravelDebugSource.cs \
  src/Debug/AIDebugger/Sources/DB_SocialDebugSource.cs \
  src/Debug/AIDebugger/Sources/DB_ThreatDebugSource.cs \
  src/Debug/AIDebugger/Sources/DB_SignalDebugSource.cs \
  src/Debug/AIDebugger/Sources/DB_EnvironmentDebugSource.cs; do test -f "$f"; done

! grep -RIn --include='*.cs' -E 'DesertBatflyHooks|DesertBatflyRuntimePatch|DesertBatflySandbox|DesertBatflyWarpCompatibility|DesertBatflyTask(09|10|11|12|13)DebugSource|DesertBatflyDebugSource' src
grep -q 'DB_RainWorldHooks.Enable()' src/Plugin.cs
grep -q 'DB_RainWorldHooks.Disable()' src/Plugin.cs
grep -q 'AIDebugRegistry.Register(new DB_EnvironmentDebugSource())' src/Creatures/DesertBatfly/Integration/RainWorld/DB_RainWorldHooks.cs
grep -q 'Register(new DB_ObservatorySource())' src/Debug/AIDebugger/Core/AIDebugRegistry.cs
grep -q 'private readonly DB_ObservatorySource inner = new();' src/Debug/AIDebugger/Sources/DB_TravelDebugSource.cs
grep -q 'private readonly DB_TravelDebugSource inner = new();' src/Debug/AIDebugger/Sources/DB_SocialDebugSource.cs
grep -q 'private readonly DB_SocialDebugSource inner = new();' src/Debug/AIDebugger/Sources/DB_ThreatDebugSource.cs
grep -q 'private readonly DB_ThreatDebugSource inner = new();' src/Debug/AIDebugger/Sources/DB_SignalDebugSource.cs
grep -q 'private readonly DB_SignalDebugSource inner = new();' src/Debug/AIDebugger/Sources/DB_EnvironmentDebugSource.cs
echo 'R6 B1 source retention audit passed.'
