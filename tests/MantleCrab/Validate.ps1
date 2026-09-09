param(
    [string]$RainWorldDir = 'D:/Application/Steam/steamapps/common/Rain World',
    [string]$UnityEditor = 'E:/Unity/2020.3.45f1/Editor/Unity.exe',
    [switch]$BuildBundle
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifacts = Join-Path $repository 'artifacts/mantlecrab'
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
& dotnet build (Join-Path $PSScriptRoot 'MantleCrab.Tests.csproj') -c Debug -p:DeployToGame=false --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Managed build failed.' }
& (Join-Path $PSScriptRoot 'bin/Debug/net48/MantleCrab.Tests.exe') $RainWorldDir $artifacts
if ($LASTEXITCODE -ne 0) { throw 'Managed rig/material/geometry validation failed.' }
if (!(Test-Path -LiteralPath $UnityEditor)) { throw "Unity editor not found: $UnityEditor" }
$methods = @('DryCycle.Editor.ValidateMantleCrab.Run')
if ($BuildBundle) { $methods += 'DryCycle.Editor.BuildDryCycleWeatherBundle.BuildCreaturesFromCommandLine' }
foreach ($method in $methods) {
    $log = Join-Path $artifacts ($method.Split('.')[-1] + '.log')
    $arguments = @('-batchmode', '-quit', '-projectPath', ('"' + (Join-Path $repository 'shader-src') + '"'),
        '-executeMethod', $method, '-logFile', ('"' + $log + '"'))
    $process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Unity validation/build failed; see $log" }
}
Get-Content (Join-Path $artifacts 'unity-validation.txt')
