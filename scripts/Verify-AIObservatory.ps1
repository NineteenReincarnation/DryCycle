param(
    [string]$RainWorldDir = "D:/Application/Steam/steamapps/common/Rain World",
    [string]$GameModOutputDir = "D:/Application/Steam/steamapps/common/Rain World/RainWorld_Data/StreamingAssets/mods/Ancient Site/newest/plugins",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src/DryCycle.csproj"

function Require-File([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description not found: $Path"
    }
}

function Require-Text([string]$Path, [string]$Pattern, [string]$Description) {
    Require-File $Path $Description
    if (-not (Select-String -LiteralPath $Path -SimpleMatch $Pattern -Quiet)) {
        throw "$Description is missing required text '$Pattern': $Path"
    }
}

Write-Host "DryCycle AI Observatory V3 verification" -ForegroundColor Cyan
Write-Host "RainWorldDir:      $RainWorldDir"
Write-Host "Plugin output:     $GameModOutputDir"

$requiredGameFiles = @(
    @{ Relative = "BepInEx/core/BepInEx.dll"; Name = "BepInEx" },
    @{ Relative = "BepInEx/core/MonoMod.RuntimeDetour.dll"; Name = "MonoMod.RuntimeDetour" },
    @{ Relative = "BepInEx/utils/PUBLIC-Assembly-CSharp.dll"; Name = "PUBLIC-Assembly-CSharp" },
    @{ Relative = "BepInEx/plugins/HOOKS-Assembly-CSharp.dll"; Name = "HOOKS-Assembly-CSharp" },
    @{ Relative = "RainWorld_Data/Managed/Assembly-CSharp-firstpass.dll"; Name = "Assembly-CSharp-firstpass" },
    @{ Relative = "RainWorld_Data/Managed/UnityEngine.dll"; Name = "UnityEngine" },
    @{ Relative = "RainWorld_Data/Managed/UnityEngine.CoreModule.dll"; Name = "UnityEngine.CoreModule" },
    @{ Relative = "RainWorld_Data/Managed/UnityEngine.AssetBundleModule.dll"; Name = "UnityEngine.AssetBundleModule" },
    @{ Relative = "RainWorld_Data/Managed/UnityEngine.AudioModule.dll"; Name = "UnityEngine.AudioModule" },
    @{ Relative = "RainWorld_Data/Managed/UnityEngine.InputLegacyModule.dll"; Name = "UnityEngine.InputLegacyModule" },
    @{ Relative = "RainWorld_Data/Managed/Unity.Mathematics.dll"; Name = "Unity.Mathematics" }
)

foreach ($entry in $requiredGameFiles) {
    Require-File (Join-Path $RainWorldDir $entry.Relative) $entry.Name
    Write-Host "  GAME OK  $($entry.Name)" -ForegroundColor DarkGreen
}

if (-not (Test-Path -LiteralPath $GameModOutputDir -PathType Container)) {
    throw "Plugin output directory not found: $GameModOutputDir"
}

Write-Host "`nChecking Observatory V3 source wiring..." -ForegroundColor Cyan
$debugRoot = Join-Path $repoRoot "src/Debug/AIDebugger"
$registry = Join-Path $debugRoot "Core/AIDebugRegistry.cs"
$runtime = Join-Path $debugRoot "Runtime/AIDebuggerRuntime.cs"
$trace = Join-Path $debugRoot "Core/AIDebugTrace.cs"
$advancedCapture = Join-Path $debugRoot "Core/AIDebugAdvancedCapture.cs"
$sessionExporter = Join-Path $debugRoot "Core/AIDebugSessionExporter.cs"
$windowV3 = Join-Path $debugRoot "UI/AIDebuggerWindowV3.cs"
$windowV2 = Join-Path $debugRoot "UI/AIDebuggerWindowV2.cs"

Require-File $windowV3 "V3 DockSpace window"
Require-File $sessionExporter "whole-session exporter"
Require-File (Join-Path $debugRoot "Runtime/AIDebugInputGate.cs") "RWInput input gate"
Require-File (Join-Path $debugRoot "Runtime/AIDebugSimulationControl.cs") "whole-world pause/step controller"
Require-File (Join-Path $debugRoot "UI/AIDebugCameraUtil.cs") "multi-RoomCamera adapter"
Require-File (Join-Path $debugRoot "Sources/MossySpiderDebugSource.cs") "MossySpider adapter"
Require-File (Join-Path $debugRoot "Sources/SpinebackLizardDebugSource.cs") "SpinebackLizard adapter"
Require-Text $registry "Register(new DesertBatflyDebugSource())" "DesertBatfly adapter registration"
Require-Text $registry "Register(new MossySpiderDebugSource())" "MossySpider adapter registration"
Require-Text $registry "Register(new SpinebackLizardDebugSource())" "SpinebackLizard adapter registration"
Require-Text $runtime "AIDebuggerWindowV3" "V3 runtime wiring"
Require-Text $runtime "AIDebugSessionExporter.Export()" "session export wiring"
Require-Text $trace "SimulationTick" "Rain World simulation-tick trace clock"
Require-Text $trace "CopyKeys" "session trace enumeration"
Require-Text $advancedCapture "tracker.smoothedUtility" "read-only cached Utility capture"

if (Test-Path -LiteralPath $windowV2 -PathType Leaf) {
    throw "Deprecated AIDebuggerWindowV2.cs is still present and would participate in the SDK build: $windowV2"
}

$debugCs = Get-ChildItem -LiteralPath $debugRoot -Filter *.cs -Recurse -File
$forbiddenUtilityCalls = $debugCs | Select-String -Pattern '\.SmoothedUtility\s*\(' -CaseSensitive
if ($forbiddenUtilityCalls) {
    throw "Observatory source still calls UtilityTracker.SmoothedUtility(), which can re-run AIModule.Utility(). First hit: $($forbiddenUtilityCalls[0].Path):$($forbiddenUtilityCalls[0].LineNumber)"
}

$hardCameraZero = $debugCs | Select-String -SimpleMatch 'cameras[0]'
if ($hardCameraZero) {
    throw "Observatory source still hard-codes cameras[0]. First hit: $($hardCameraZero[0].Path):$($hardCameraZero[0].LineNumber)"
}

Write-Host "  SOURCE OK  V3 workspace / adapters / trace clock / session export / camera abstraction" -ForegroundColor Green

if (-not $SkipBuild) {
    Write-Host "`nBuilding Release..." -ForegroundColor Cyan
    & dotnet build $project -c Release `
        "-p:RainWorldDir=$RainWorldDir" `
        "-p:GameModOutputDir=$GameModOutputDir"
    if ($LASTEXITCODE -ne 0) {
        throw "DryCycle Release build failed with exit code $LASTEXITCODE."
    }
}

Write-Host "`nChecking deployed Observatory runtime..." -ForegroundColor Cyan
$hardRequired = @(
    "DryCycle.dll",
    "ImGui.NET.dll",
    "cimgui.dll"
)
foreach ($file in $hardRequired) {
    Require-File (Join-Path $GameModOutputDir $file) "Observatory runtime file '$file'"
    Write-Host "  OK  $file" -ForegroundColor Green
}

# RuntimeDetour is intentionally referenced from BepInEx/core and MUST NOT be copied
# as a second private version beside DryCycle.dll. Check the source copy above instead.
$duplicateDetour = Join-Path $GameModOutputDir "MonoMod.RuntimeDetour.dll"
if (Test-Path -LiteralPath $duplicateDetour -PathType Leaf) {
    Write-Warning "A private MonoMod.RuntimeDetour.dll exists beside DryCycle.dll. Remove it unless it is byte-for-byte the BepInEx/core version; duplicate RuntimeDetour versions can break Mono hooks."
}

# ImGui.NET support dependencies can vary by resolved target/framework. These are
# warnings rather than hard failures because some Rain World Mono installations can
# already provide compatible facade/support assemblies.
$managedSupport = @(
    "System.Buffers.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll"
)
foreach ($file in $managedSupport) {
    $path = Join-Path $GameModOutputDir $file
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Write-Host "  OK  $file" -ForegroundColor Green
    }
    else {
        Write-Warning "$file is not present beside DryCycle.dll. Confirm that the Rain World Mono runtime already provides a compatible assembly before accepting the build."
    }
}

Write-Host "`nStatic deployment checks passed." -ForegroundColor Green
Write-Host "Live acceptance checklist:" -ForegroundColor Cyan
Write-Host "  1. Start Rain World with F7 closed; verify ordinary gameplay and no Observatory exception."
Write-Host "  2. F7 -> Compact, F6 -> Full DockSpace; save/reset layout and verify restart persistence."
Write-Host "  3. Switch Chinese/English and verify old Event entries re-render without losing raw data."
Write-Host "  4. Alt+Left Click a creature; test normal and Jolly split-screen/multiple RoomCamera conditions if available."
Write-Host "  5. Test LIVE/INTERACT plus text/mouse widgets: captured ImGui input must not leak into player gameplay controls."
Write-Host "  6. Pause World -> Step 1 Tick repeatedly; Timeline must advance by one RainWorldGame.clock tick per step and must not fill while merely paused."
Write-Host "  7. Exercise Timeline / Freeze / Utility / Perception / Path / Compare / Candidates."
Write-Host "  8. On vanilla UtilityComparer AI, verify missing non-retained values show dash and the creature behavior is unchanged by opening Utility."
Write-Host "  9. Trigger a manual capture; after five simulated seconds export JSON and inspect roughly 10 s pre + 5 s post history."
Write-Host " 10. Exercise automatic anomaly detection; debugger Pause must not create PossibleStuck."
Write-Host " 11. Exercise a conditional breakpoint and verify it pauses the whole world rather than one AI."
Write-Host " 12. Test DesertBatfly Sentinel/Bully/Opportunist overlays and AttackSlots labels."
Write-Host " 13. Select MossySpider and verify the dedicated Roaming/Waiting, RoamTarget and Pather diagnostics."
Write-Host " 14. Select SpinebackLizard and verify Green-baseline ownership plus LizardAI Utility/Tracker/Path diagnostics."
Write-Host " 15. Enable AImap overlay and confirm creature-specific accessibility/connection rendering."
Write-Host " 16. Verify selected/pinned identity survives shortcut, den, unrealize/realize and room transitions."
Write-Host " 17. Press Ctrl+Shift+F8; validate BepInEx/config/DryCycle.AIObservatory.Sessions/*.json and confirm traces/history/captures exist."
Write-Host " 18. Verify non-finite diagnostic floats export as JSON null rather than NaN/Infinity."
Write-Host " 19. Verify a non-ASCII Windows/Rain World path can save/load DockSpace layout."
Write-Host " 20. Verify F7-closed overhead is effectively zero and F7-open profiler values are acceptable in a populated stress room."
