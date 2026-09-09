param(
    [string]$RainWorldDir = 'D:/Application/Steam/steamapps/common/Rain World',
    [string]$UnityEditor = 'E:/Unity/2020.3.45f1/Editor/Unity.exe',
    [switch]$BuildBundle
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifacts = Join-Path $repository 'artifacts/mantlecrab'
[IO.Directory]::CreateDirectory($artifacts) | Out-Null

$total = if ($BuildBundle) { 4 } else { 3 }
Write-Host "[1/$total] Managed build"
& dotnet build (Join-Path $PSScriptRoot 'MantleCrab.Tests.csproj') `
    -c Debug `
    -p:DeployToGame=false `
    "-p:RainWorldDir=$RainWorldDir" `
    --nologo `
    -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Managed build failed.' }

Write-Host "[2/$total] MantleCrab managed validation"
& (Join-Path $PSScriptRoot 'bin/Debug/net48/MantleCrab.Tests.exe') $RainWorldDir $artifacts
if ($LASTEXITCODE -ne 0) { throw 'Managed rig/material/geometry validation failed.' }

if (!(Test-Path -LiteralPath $UnityEditor)) { throw "Unity editor not found: $UnityEditor" }

Write-Host "[3/$total] Unity GPU/material validation"
$validationLog = Join-Path $artifacts 'Run.log'
$arguments = @(
    '-batchmode', '-quit',
    '-projectPath', ('"' + (Join-Path $repository 'shader-src') + '"'),
    '-executeMethod', 'DryCycle.Editor.ValidateMantleCrab.Run',
    '-logFile', ('"' + $validationLog + '"'))
$process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Unity validation failed; see $validationLog" }

if ($BuildBundle)
{
    Write-Host "[4/$total] Creature AssetBundle"
    $bundleLog = Join-Path $artifacts 'BuildCreaturesFromCommandLine.log'
    $bundleArgs = @(
        '-batchmode', '-quit',
        '-projectPath', ('"' + (Join-Path $repository 'shader-src') + '"'),
        '-executeMethod', 'DryCycle.Editor.BuildDryCycleWeatherBundle.BuildCreaturesFromCommandLine',
        '-logFile', ('"' + $bundleLog + '"'))
    $bundleProcess = Start-Process -FilePath $UnityEditor -ArgumentList $bundleArgs -WindowStyle Hidden -PassThru -Wait
    if ($bundleProcess.ExitCode -ne 0) { throw "Creature AssetBundle build failed; see $bundleLog" }
}

Get-Content (Join-Path $artifacts 'unity-validation.txt')
