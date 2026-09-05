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

Write-Host "DryCycle AI Observatory verification" -ForegroundColor Cyan
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
Write-Host "  2. F7 -> Compact, F6 -> Full DockSpace; save/reset layout."
Write-Host "  3. Switch Chinese/English and verify old Event entries re-render without losing raw data."
Write-Host "  4. Alt+Left Click a creature; test normal and Jolly split-screen cameras if available."
Write-Host "  5. Test LIVE/INTERACT: INTERACT must neutralize player gameplay input and LIVE must restore it."
Write-Host "  6. Pause World -> Step 1 Tick repeatedly; Timeline must advance by one RainWorldGame.clock tick per step and must not fill while merely paused."
Write-Host "  7. Exercise Timeline / Freeze / Utility / Perception / Path / Compare / Candidates."
Write-Host "  8. Trigger a manual capture; after five simulated seconds export JSON and inspect pre/post history."
Write-Host "  9. Exercise a conditional breakpoint and verify it pauses the whole world rather than one AI."
Write-Host " 10. Test DesertBatfly Sentinel/Bully/Opportunist overlays and AttackSlots labels."
Write-Host " 11. Enable AImap overlay and confirm creature-specific accessibility/connection rendering."
Write-Host " 12. Verify selected/pinned identity survives shortcut, den, unrealize/realize and room transitions."
Write-Host " 13. Verify F7-closed overhead is effectively zero and F7-open profiler values are acceptable."
