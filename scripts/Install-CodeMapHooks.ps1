param(
    [switch]$Disable,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoHint = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$repo = (& git -C $repoHint rev-parse --show-toplevel 2>$null).Trim()
if ([string]::IsNullOrWhiteSpace($repo)) {
    throw "repository root unavailable"
}

$current = (& git -C $repo config --local --get core.hooksPath 2>$null)
$current = if ($null -eq $current) { "" } else { ([string]$current).Trim() }

if ($Disable) {
    if ($current -eq ".githooks") {
        & git -C $repo config --local --unset core.hooksPath
        if ($LASTEXITCODE -ne 0) {
            throw "cannot disable repository hooks"
        }
    }
    exit 0
}

if (-not [string]::IsNullOrWhiteSpace($current) -and
    $current -ne ".githooks" -and
    -not $Force) {
    throw "custom core.hooksPath already configured: $current"
}

foreach ($hook in @(".githooks/pre-commit", ".githooks/pre-push")) {
    if (-not (Test-Path -LiteralPath (Join-Path $repo $hook) -PathType Leaf)) {
        throw "missing hook: $hook"
    }
}

& git -C $repo config --local core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) {
    throw "cannot configure core.hooksPath"
}

$verified = (& git -C $repo config --local --get core.hooksPath 2>$null)
if (([string]$verified).Trim() -ne ".githooks") {
    throw "core.hooksPath verification failed"
}

exit 0
