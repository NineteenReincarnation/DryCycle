param(
    [string]$RainWorldDir = "D:\Steam\steamapps\common\Rain World",
    [string]$RWImGuiPluginDir = "",
    [string]$Configuration = "Release",
    [string]$BuildRoot = "",
    [switch]$BackendOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$GuardRoot = Split-Path -Parent $ScriptRoot
$RepoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $GuardRoot))
$DefaultBuildRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot "../Build/DryCycle/local-build"))
$ValidationMarkerName = ".drycycle-build-validation"
$ValidationMarkerText = "DryCycle Local Build Validation"
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }

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

    & $dotnetExe @arguments
    if ($LASTEXITCODE -ne 0) {
        Fail "$Label failed with exit code $LASTEXITCODE."
    }
    Pass $Label
}

function Initialize-BuildRoot {
    $buildDriveRoot = [System.IO.Path]::GetPathRoot($BuildRoot)
    $insideRepository = Is-PathWithin $BuildRoot $RepoRoot
    if ((Same-Path $BuildRoot $buildDriveRoot) -or
        (Is-PathWithin $BuildRoot $RainWorldDir) -or
        $insideRepository) {
        Fail "Unsafe BuildRoot. Use ../Build/DryCycle/local-build, or a dedicated directory outside the repository and Rain World installation."
    }

    $marker = Join-Path $BuildRoot $ValidationMarkerName
    if (Test-Path -LiteralPath $BuildRoot) {
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            Fail "Refusing to delete existing BuildRoot because it is not owned by DryCycle validation: $BuildRoot"
        }
        $markerContent = (Get-Content -LiteralPath $marker -Raw).Trim()
        if ($markerContent -ne $ValidationMarkerText) {
            Fail "Refusing to delete existing BuildRoot because its validation marker is invalid: $BuildRoot"
        }
        Remove-Item -LiteralPath $BuildRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $BuildRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $BuildRoot $ValidationMarkerName) -Value $ValidationMarkerText -Encoding UTF8
}

function Validate-BackendArtifact([string]$AssemblyPath) {
    Write-Host ""
    Write-Host "=== DryCycle.dll artifact ===" -ForegroundColor Cyan

    $cecilPath = Join-Path $RainWorldDir "BepInEx/core/Mono.Cecil.dll"
    Require-File $cecilPath "Mono.Cecil artifact inspector"

    try {
        Add-Type -Path $cecilPath
        $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($AssemblyPath)
        try {
            $references = @($assembly.MainModule.AssemblyReferences | ForEach-Object { $_.Name })
            $forbiddenOptionalReferences = @(
                "ImGui.NET",
                "rain-world-imgui-api",
                "DryCycle.DevTool.RWImGui",
                "DryCycle.AIObservatory.RWImGui"
            )
            $badOptionalReferences = @($references | Where-Object { $forbiddenOptionalReferences -contains $_ })
            if ($badOptionalReferences.Count -gt 0) {
                Fail ("DryCycle.dll has optional frontend/runtime AssemblyRef(s): " + ($badOptionalReferences -join ", "))
            }

            $externalNAudioReferences = @($references | Where-Object { $_ -eq "NAudio.Core" -or $_ -eq "NAudio.Wasapi" -or $_ -eq "NAudio" })
            if ($externalNAudioReferences.Count -gt 0) {
                Fail ("DryCycle.dll still requires external NAudio managed assembly/assemblies: " + ($externalNAudioReferences -join ", "))
            }

            $containsNAudio = $false
            foreach ($module in $assembly.Modules) {
                foreach ($type in $module.Types) {
                    if ($type.Namespace -eq "NAudio" -or $type.Namespace.StartsWith("NAudio.", [System.StringComparison]::Ordinal)) {
                        $containsNAudio = $true
                        break
                    }
                }
                if ($containsNAudio) { break }
            }
            if (-not $containsNAudio) {
                Fail "DryCycle.dll has no NAudio TypeDef. The managed NAudio implementation was not verified as merged into the gameplay DLL."
            }
        }
        finally {
            $assembly.Dispose()
        }
    }
    catch {
        Fail ("DryCycle.dll artifact inspection failed: " + $_.Exception.Message)
    }

    Pass "DryCycle.dll managed dependency and merge contract"
}

Write-Host "============================================================"
Write-Host "DryCycle Local Build Validation"
Write-Host "============================================================"
Write-Host "Repository : $RepoRoot"
Write-Host "Rain World : $RainWorldDir"
Write-Host "Build root : $BuildRoot"
Write-Host "Config     : $Configuration"
Write-Host "Mode       : $(if ($BackendOnly) { 'Backend only' } else { 'Backend + optional RWImGui frontends' })"
Write-Host ""

if (-not (Test-Path -LiteralPath $dotnetExe -PathType Leaf)) {
    Fail "dotnet SDK was not found on PATH or in Program Files/dotnet."
}

Require-Directory $RainWorldDir "Rain World directory"

$mainProject = Join-Path $RepoRoot "src/DryCycle.csproj"
$aiFrontendProject = Join-Path $RepoRoot "src/DryCycle.AIObservatory.RWImGui/DryCycle.AIObservatory.RWImGui.csproj"
$devToolFrontendProject = Join-Path $RepoRoot "src/DevUI/DevTool/RWImGui/DryCycle.DevTool.RWImGui.csproj"
Require-File $mainProject "DryCycle project"
if (-not $BackendOnly) {
    Require-File $aiFrontendProject "AI Observatory RWImGui project"
    Require-File $devToolFrontendProject "DevTool RWImGui project"
}

Initialize-BuildRoot

# The validation build uses an isolated output directory and explicitly disables deployment.
# Build targets are expected to respect that contract; this script does not claim to prove arbitrary
# MSBuild data flow or runtime behavior.
$backendProperties = @(
    "RainWorldDir=$RainWorldDir",
    "DeployToGame=false",
    "OutputPath=$BuildRoot"
)
Invoke-DotNetBuild $mainProject $backendProperties "DryCycle.dll"

$dryCycleDll = Join-Path $BuildRoot "DryCycle.dll"
Require-File $dryCycleDll "compiled DryCycle.dll"
Validate-BackendArtifact $dryCycleDll

if ($BackendOnly) {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Green
    Write-Host "Backend build and artifact validation passed." -ForegroundColor Green
    Write-Host "============================================================" -ForegroundColor Green
    Write-Host "Backend : $dryCycleDll"
    Write-Host ""
    Write-Host "Runtime behavior was not validated."
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
    Fail "RWImGui plugin directory was not found. Pass -RWImGuiPluginDir explicitly, or use -BackendOnly."
}
$RWImGuiPluginDir = [System.IO.Path]::GetFullPath($RWImGuiPluginDir)

Write-Host ""
Write-Host "=== RWImGui references ===" -ForegroundColor Cyan
Require-Directory $RWImGuiPluginDir "RWImGui plugin directory"
Require-File (Join-Path $RWImGuiPluginDir "rain-world-imgui-api.dll") "rain-world-imgui-api"
Require-File (Join-Path $RWImGuiPluginDir "ImGui.NET.dll") "RWImGui ImGui.NET"

# Both optional frontends compile against the exact isolated DryCycle.dll produced above.
$frontendProperties = @(
    "RainWorldDir=$RainWorldDir",
    "DeployToGame=false",
    "GameModOutputDir=$BuildRoot",
    "RWImGuiPluginDir=$RWImGuiPluginDir"
)
Invoke-DotNetBuild $aiFrontendProject $frontendProperties "DryCycle.AIObservatory.RWImGui.dll"
Invoke-DotNetBuild $devToolFrontendProject $frontendProperties "DryCycle.DevTool.RWImGui.dll"

$aiFrontendDll = Join-Path $BuildRoot "DryCycle.AIObservatory.RWImGui.dll"
$devToolFrontendDll = Join-Path $BuildRoot "DryCycle.DevTool.RWImGui.dll"
Require-File $aiFrontendDll "compiled DryCycle.AIObservatory.RWImGui.dll"
Require-File $devToolFrontendDll "compiled DryCycle.DevTool.RWImGui.dll"

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "Build and artifact validation passed." -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host "Backend             : $dryCycleDll"
Write-Host "AI Observatory UI   : $aiFrontendDll"
Write-Host "DevTool UI          : $devToolFrontendDll"
Write-Host ""
Write-Host "Runtime behavior was not validated."
