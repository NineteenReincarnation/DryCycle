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
    @{ Relative = "BepInEx/utils/PUBLIC-Assembly-CSharp.dll"; Name = "PUBLIC-Assembly-CSharp" },
    @{ Relative = "BepInEx/plugins/HOOKS-Assembly-CSharp.dll"; Name = "HOOKS-Assembly-CSharp" },
    @{ Relative = "RainWorld_Data/Managed/UnityEngine.dll"; Name = "UnityEngine" },
    @{ Relative = "RainWorld_Data/Managed/UnityEngine.CoreModule.dll"; Name = "UnityEngine.CoreModule" },
    @{ Relative = "RainWorld_Data/Managed/UnityEngine.InputLegacyModule.dll"; Name = "UnityEngine.InputLegacyModule" }
)

foreach ($entry in $requiredGameFiles) {
    Require-File (Join-Path $RainWorldDir $entry.Relative) $entry.Name
}

if (-not $SkipBuild) {
    Write-Host "\nBuilding Release..." -ForegroundColor Cyan
    & dotnet build $project -c Release `
        "-p:RainWorldDir=$RainWorldDir" `
        "-p:GameModOutputDir=$GameModOutputDir"
    if ($LASTEXITCODE -ne 0) {
        throw "DryCycle Release build failed with exit code $LASTEXITCODE."
    }
}

Write-Host "\nChecking deployed Observatory runtime..." -ForegroundColor Cyan
$hardRequired = @(
    "DryCycle.dll",
    "ImGui.NET.dll",
    "cimgui.dll"
)
foreach ($file in $hardRequired) {
    Require-File (Join-Path $GameModOutputDir $file) "Observatory runtime file '$file'"
    Write-Host "  OK  $file" -ForegroundColor Green
}

# ImGui.NET 1.91.6.1 targets netstandard2.0 for this net48 project and currently
# depends on these managed support packages. CopyLocalLockFileAssemblies=true should
# place the runtime assets beside DryCycle.dll. A future package version may make one
# or more framework-provided; keep these as warnings rather than hard failures.
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

Write-Host "\nStatic deployment checks passed." -ForegroundColor Green
Write-Host "Next live checks:" -ForegroundColor Cyan
Write-Host "  1. Start Rain World with F7 closed and verify normal gameplay."
Write-Host "  2. F7 -> Observatory Compact; F6 -> Full."
Write-Host "  3. Alt+Left Click a realized creature."
Write-Host "  4. Exercise Timeline / Freeze / Events / Utility / Perception / Path / Compare."
Write-Host "  5. Confirm F7-closed overhead is effectively zero and F7-open overhead is acceptable."
