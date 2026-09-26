param(
    [string]$UnityEditor = 'E:/Application/Unity/Editor/Unity.exe',
    [string]$RainWorldDir = 'D:/Steam/steamapps/common/Rain World',
    [string]$BuildRoot = '',
    [string]$FrontendPluginDir = '',
    [ValidateSet('all', 'connections', 'worldmap')][string]$TestScope = 'all'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($BuildRoot)) { $BuildRoot = Join-Path $repoRoot '../Build/DryCycle' }
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
if ($BuildRoot.StartsWith($repoRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'GPU test output must be outside the source checkout.'
}
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }
$pluginDir = [IO.Path]::GetFullPath((Join-Path $RainWorldDir '../../workshop/content/312520/3417372413/plugins'))
$output = Join-Path $BuildRoot 'validation/world-map-render-isolation'
$project = Join-Path $output 'UnityProject'
foreach ($required in @($UnityEditor, $dotnetExe, (Join-Path $pluginDir 'ImGui.NET.dll'), (Join-Path $pluginDir 'System.Runtime.CompilerServices.Unsafe.dll'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file missing: $required" }
}

& $dotnetExe build (Join-Path $repoRoot 'tests/WorldMapRenderIsolation.Tests/WorldMapRenderIsolation.Tests.csproj') -c Release -v quiet "-p:DryCycleBuildRoot=$BuildRoot" "-p:UnityEditorDir=$(Split-Path -Parent $UnityEditor)" "-p:RWImGuiPluginDir=$pluginDir"
if ($LASTEXITCODE -ne 0) { throw "GPU test compilation failed ($LASTEXITCODE)." }
foreach ($folder in @('Assets/Editor','ProjectSettings','Packages')) { New-Item -ItemType Directory -Path (Join-Path $project $folder) -Force | Out-Null }
if (-not (Test-Path -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt'))) {
    Copy-Item -LiteralPath (Join-Path $repoRoot 'shader-src/ProjectSettings/ProjectVersion.txt') -Destination (Join-Path $project 'ProjectSettings/ProjectVersion.txt')
}
if (-not (Test-Path -LiteralPath (Join-Path $project 'Packages/manifest.json'))) {
    '{"dependencies":{"com.unity.modules.imageconversion":"1.0.0","com.unity.modules.imgui":"1.0.0"}}' | Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') -Encoding utf8
}
Copy-Item -LiteralPath (Join-Path $BuildRoot 'bin/WorldMapRenderIsolation.Tests/Release/net48/WorldMapRenderIsolation.Tests.dll') -Destination (Join-Path $project 'Assets/Editor') -Force
# Game/plugin references are loaded by the test's AssemblyResolve handler from the real install.
# Do not make Unity import the entire game and its BepInEx plugins as editor startup assemblies.
@'
fileFormatVersion: 2
guid: b15b0223fcd1d0a4cb4ed13f2771ce45
PluginImporter:
  serializedVersion: 2
  validateReferences: 0
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        DefaultValueInitialized: true
'@ | Set-Content -LiteralPath (Join-Path $project 'Assets/Editor/WorldMapRenderIsolation.Tests.dll.meta') -Encoding utf8
foreach ($name in @('ImGui.NET.dll', 'System.Runtime.CompilerServices.Unsafe.dll')) {
    Copy-Item -LiteralPath (Join-Path $pluginDir $name) -Destination (Join-Path $project 'Assets/Editor') -Force
}
$arguments = @('-batchmode', '-force-d3d11', '-projectPath', ('"' + $project + '"'), '-executeMethod', 'MapRenderIsolationTests.Run', '-rainWorldDir', ('"' + $RainWorldDir + '"'), '-isolationOutput', ('"' + $output + '"'), '-logFile', ('"' + (Join-Path $output 'unity-gpu-test.log') + '"'))
if ($FrontendPluginDir) { $arguments += @('-frontendPluginDir', ('"' + [IO.Path]::GetFullPath($FrontendPluginDir) + '"')) }
$arguments += @('-testScope', $TestScope)
Write-Host "Unity GPU validation: $output"
$process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "Unity GPU validation failed ($($process.ExitCode)). See $output/unity-gpu-test.log and gpu-results.txt." }
Get-Content -LiteralPath (Join-Path $output 'gpu-results.txt')
