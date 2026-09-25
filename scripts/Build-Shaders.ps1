param(
    [string]$UnityEditor = $env:UNITY_EDITOR,
    [string]$RainWorldDir = "D:\Steam\steamapps\common\Rain World",
    [string]$BuildRoot = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($BuildRoot)) { $BuildRoot = Join-Path $repoRoot '../Build/DryCycle' }
$BuildRoot = [System.IO.Path]::GetFullPath($BuildRoot)
$versionFile = Join-Path $repoRoot "shader-src/ProjectSettings/ProjectVersion.txt"
$versionMatch = [regex]::Match((Get-Content -LiteralPath $versionFile -Raw), '(?m)^m_EditorVersion:\s*(\S+)')
if (-not $versionMatch.Success) {
    throw "Cannot read the required Unity Editor version from $versionFile"
}
$editorVersion = $versionMatch.Groups[1].Value

if ([string]::IsNullOrWhiteSpace($UnityEditor)) {
    # Pick up a newly configured editor even in terminals opened before installation.
    $UnityEditor = [Environment]::GetEnvironmentVariable("UNITY_EDITOR", "User")
}
if ([string]::IsNullOrWhiteSpace($UnityEditor)) {
    $standardEditor = Join-Path $env:ProgramFiles "Unity/Hub/Editor/$editorVersion/Editor/Unity.exe"
    if (Test-Path -LiteralPath $standardEditor -PathType Leaf) {
        $UnityEditor = $standardEditor
    }
}
if ([string]::IsNullOrWhiteSpace($UnityEditor) -or
    -not (Test-Path -LiteralPath $UnityEditor -PathType Leaf)) {
    throw "Unity Editor $editorVersion was not found. Install it, then pass -UnityEditor 'path/to/Unity.exe' or set UNITY_EDITOR. Expected standard path: $env:ProgramFiles/Unity/Hub/Editor/$editorVersion/Editor/Unity.exe"
}
$UnityEditor = (Resolve-Path -LiteralPath $UnityEditor).Path

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe = if ($null -ne $dotnetCommand) { $dotnetCommand.Source } else {
    Join-Path $env:ProgramFiles "dotnet/dotnet.exe"
}
if (-not (Test-Path -LiteralPath $dotnetExe -PathType Leaf)) {
    throw ".NET SDK was not found. Install the SDK and restart the terminal."
}

$RainWorldDir = [System.IO.Path]::GetFullPath($RainWorldDir)
$modRoot = Join-Path $RainWorldDir "RainWorld_Data/StreamingAssets/mods/Ancient Site"
if (-not (Test-Path -LiteralPath $modRoot -PathType Container)) {
    throw "Ancient Site mod directory not found: $modRoot"
}

Write-Host "Unity Editor: $UnityEditor (project version: $editorVersion)"
Write-Host "Unity project/cache: $(Join-Path $BuildRoot 'shader-project')"
Write-Host "Shader build output: $(Join-Path $BuildRoot 'shader-bundles')"
Write-Host "Redistributable mod assets: $(Join-Path $repoRoot 'mod/assets/drycycle')"
Write-Host "Shader deployment: $(Join-Path $modRoot 'assets/drycycle')"
Write-Host "DLL deployment: $(Join-Path $modRoot 'newest/plugins')"

# Use the project's existing build/deployment boundary for both shader bundles and DLLs.
& $dotnetExe build (Join-Path $repoRoot "src/DryCycle.csproj") -c Release `
    "-p:BuildDryCycleAssets=true" "-p:DryCycleUnityEditor=$UnityEditor" `
    "-p:RainWorldDir=$RainWorldDir" "-p:GameModRootDir=$modRoot" "-p:DryCycleBuildRoot=$BuildRoot"
if ($LASTEXITCODE -ne 0) {
    throw "Shader/DLL build or deployment failed with exit code $LASTEXITCODE. See the original build errors above."
}
