param(
    [string]$RainWorldDir = "D:\Application\Steam\steamapps\common\Rain World",
    [string]$RWImGuiPluginDir = "",
    [string]$Configuration = "Release",
    [string]$BuildRoot = "",
    [switch]$BackendOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $ScriptRoot))
$DefaultBuildRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot ".phase6-validation"))

if ([string]::IsNullOrWhiteSpace($BuildRoot)) {
    $BuildRoot = $DefaultBuildRoot
}
$BuildRoot = [System.IO.Path]::GetFullPath($BuildRoot)
$RainWorldDir = [System.IO.Path]::GetFullPath($RainWorldDir)

function Fail([string]$Message) {
    Write-Host "[FAIL] $Message" -ForegroundColor Red
    exit 1
}

function Pass([string]$Message) {
    Write-Host "[ OK ] $Message" -ForegroundColor Green
}

function Require-File([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail "$Label not found: $Path"
    }
    Pass $Label
}

function Require-Directory([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        Fail "$Label not found: $Path"
    }
    Pass $Label
}

function Same-Path([string]$Left, [string]$Right) {
    $leftNormalized = [System.IO.Path]::GetFullPath($Left).TrimEnd('\', '/')
    $rightNormalized = [System.IO.Path]::GetFullPath($Right).TrimEnd('\', '/')
    return [string]::Equals($leftNormalized, $rightNormalized, [System.StringComparison]::OrdinalIgnoreCase)
}

function Is-PathWithin([string]$Child, [string]$Parent) {
    $childNormalized = [System.IO.Path]::GetFullPath($Child).TrimEnd('\', '/')
    $parentNormalized = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    if ([string]::Equals($childNormalized, $parentNormalized, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $prefix = $parentNormalized + [System.IO.Path]::DirectorySeparatorChar
    return $childNormalized.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Invoke-DotNetBuild(
    [string]$Project,
    [string[]]$Properties,
    [string]$Label
) {
    Write-Host ""
    Write-Host "=== $Label ===" -ForegroundColor Cyan

    $arguments = @(
        "build",
        $Project,
        "-c", $Configuration,
        "--nologo",
        "-v:minimal"
    )
    foreach ($property in $Properties) {
        $arguments += "-p:$property"
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        Fail "$Label failed with exit code $LASTEXITCODE."
    }
    Pass $Label
}

Write-Host "============================================================"
Write-Host "DryCycle DevTool Phase 6 - Local Validation"
Write-Host "============================================================"
Write-Host "Repository : $RepoRoot"
Write-Host "Rain World : $RainWorldDir"
Write-Host "Build root : $BuildRoot"
Write-Host "Config     : $Configuration"
Write-Host "Mode       : $(if ($BackendOnly) { 'Backend only' } else { 'Backend + RWImGui frontend' })"
Write-Host ""

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail "dotnet SDK was not found on PATH."
}

Require-Directory $RainWorldDir "Rain World directory"

$buildDriveRoot = [System.IO.Path]::GetPathRoot($BuildRoot)
$insideRepository = Is-PathWithin $BuildRoot $RepoRoot
$insideSafeRepositoryOutput = Is-PathWithin $BuildRoot $DefaultBuildRoot
if ((Same-Path $BuildRoot $buildDriveRoot) -or
    (Is-PathWithin $BuildRoot $RainWorldDir) -or
    ($insideRepository -and -not $insideSafeRepositoryOutput)) {
    Fail "Unsafe BuildRoot. Use .phase6-validation (or a child of it), or a dedicated directory outside the repository and Rain World installation."
}

$requiredRainWorldFiles = [ordered]@{
    "BepInEx" = "BepInEx/core/BepInEx.dll"
    "MonoMod.RuntimeDetour" = "BepInEx/core/MonoMod.RuntimeDetour.dll"
    "MonoMod.Utils" = "BepInEx/core/MonoMod.Utils.dll"
    "Mono.Cecil" = "BepInEx/core/Mono.Cecil.dll"
    "PUBLIC-Assembly-CSharp" = "BepInEx/utils/PUBLIC-Assembly-CSharp.dll"
    "HOOKS-Assembly-CSharp" = "BepInEx/plugins/HOOKS-Assembly-CSharp.dll"
    "Assembly-CSharp-firstpass" = "RainWorld_Data/Managed/Assembly-CSharp-firstpass.dll"
    "UnityEngine" = "RainWorld_Data/Managed/UnityEngine.dll"
    "UnityEngine.CoreModule" = "RainWorld_Data/Managed/UnityEngine.CoreModule.dll"
    "UnityEngine.AssetBundleModule" = "RainWorld_Data/Managed/UnityEngine.AssetBundleModule.dll"
    "UnityEngine.AudioModule" = "RainWorld_Data/Managed/UnityEngine.AudioModule.dll"
    "UnityEngine.InputLegacyModule" = "RainWorld_Data/Managed/UnityEngine.InputLegacyModule.dll"
    "Unity.Mathematics" = "RainWorld_Data/Managed/Unity.Mathematics.dll"
}

Write-Host "=== Rain World references ===" -ForegroundColor Cyan
foreach ($entry in $requiredRainWorldFiles.GetEnumerator()) {
    Require-File (Join-Path $RainWorldDir $entry.Value) $entry.Key
}

$mainProject = Join-Path $RepoRoot "src/DryCycle.csproj"
$frontendProject = Join-Path $RepoRoot "src/DevUI/DevTool/RWImGui/DryCycle.DevTool.RWImGui.csproj"
Require-File $mainProject "DryCycle project"
if (-not $BackendOnly) {
    Require-File $frontendProject "DevTool RWImGui project"
}

if (Test-Path -LiteralPath $BuildRoot) {
    Remove-Item -LiteralPath $BuildRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $BuildRoot | Out-Null

# Build the backend into an isolated directory. DeployToGame=false guarantees that the validation
# run does not overwrite the user's active Rain World mod installation.
$backendProperties = @(
    "RainWorldDir=$RainWorldDir",
    "DeployToGame=false",
    "OutputPath=$BuildRoot"
)
Invoke-DotNetBuild $mainProject $backendProperties "DryCycle.dll"

$dryCycleDll = Join-Path $BuildRoot "DryCycle.dll"
Require-File $dryCycleDll "compiled DryCycle.dll"

if ($BackendOnly) {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Green
    Write-Host "Phase 6 backend compile validation passed." -ForegroundColor Green
    Write-Host "============================================================" -ForegroundColor Green
    Write-Host "Backend  : $dryCycleDll"
    Write-Host ""
    Write-Host "BackendOnly validates DryCycle.dll compilation only. RWImGui frontend, Rain World runtime, and performance regression are still pending."
    exit 0
}

if ([string]::IsNullOrWhiteSpace($RWImGuiPluginDir)) {
    $candidates = @(
        (Join-Path $RainWorldDir "../../workshop/content/312520/3417372413/plugins"),
        (Join-Path $RainWorldDir "../../workshop/content/312520/3417372413/rwimgui/plugins"),
        (Join-Path $RainWorldDir "RainWorld_Data/StreamingAssets/mods/rwimgui/plugins")
    )

    foreach ($candidate in $candidates) {
        $api = Join-Path $candidate "rain-world-imgui-api.dll"
        $imgui = Join-Path $candidate "ImGui.NET.dll"
        if ((Test-Path -LiteralPath $api -PathType Leaf) -and
            (Test-Path -LiteralPath $imgui -PathType Leaf)) {
            $RWImGuiPluginDir = [System.IO.Path]::GetFullPath($candidate)
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($RWImGuiPluginDir)) {
    Fail "RWImGui plugin directory was not found. Pass -RWImGuiPluginDir explicitly, or use -BackendOnly to validate DryCycle.dll first."
}
$RWImGuiPluginDir = [System.IO.Path]::GetFullPath($RWImGuiPluginDir)

Write-Host ""
Write-Host "=== RWImGui references ===" -ForegroundColor Cyan
Require-Directory $RWImGuiPluginDir "RWImGui plugin directory"
Require-File (Join-Path $RWImGuiPluginDir "rain-world-imgui-api.dll") "rain-world-imgui-api"
Require-File (Join-Path $RWImGuiPluginDir "ImGui.NET.dll") "RWImGui ImGui.NET"

# The frontend project resolves DryCycle.dll through GameModOutputDir. Point that property at the
# isolated validation output so it compiles against the exact backend produced above.
$frontendProperties = @(
    "RainWorldDir=$RainWorldDir",
    "GameModOutputDir=$BuildRoot",
    "RWImGuiPluginDir=$RWImGuiPluginDir"
)
Invoke-DotNetBuild $frontendProject $frontendProperties "DryCycle.DevTool.RWImGui.dll"

$frontendDll = Join-Path $BuildRoot "DryCycle.DevTool.RWImGui.dll"
Require-File $frontendDll "compiled DryCycle.DevTool.RWImGui.dll"

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "Phase 6 compile validation passed." -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host "Backend  : $dryCycleDll"
Write-Host "Frontend : $frontendDll"
Write-Host ""
Write-Host "This script validates compilation only. It does not claim Rain World runtime regression or performance validation."
Write-Host "Next runtime checks:"
Write-Host "  1. New UI / Vanilla switching"
Write-Host "  2. Save / Undo / Redo"
Write-Host "  3. Objects / Room / Sound / Triggers / Map / Dialog / Relationships"
Write-Host "  4. Generic DevInterface fallback"
Write-Host "  5. DevTool Extension API register / dispose / reload"
Write-Host "  6. Stable-frame performance and snapshot-cache regression"
