param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$autoFix = Join-Path $PSScriptRoot "CodeMap.AutoFix.ps1"
$checker = Join-Path $PSScriptRoot "CodeMap.ps1"
$powerShell = if (Get-Command pwsh -ErrorAction SilentlyContinue) {
    (Get-Command pwsh).Source
}
elseif (Get-Command powershell.exe -ErrorAction SilentlyContinue) {
    (Get-Command powershell.exe).Source
}
else {
    throw "PowerShell host not found"
}

function Invoke-Git([string]$Repo, [string[]]$Args) {
    Push-Location $Repo
    try {
        $output = @(& git @Args 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "git failed: $($Args -join ' ')"
        }
        return @($output | ForEach-Object { [string]$_ })
    }
    finally {
        Pop-Location
    }
}

function Write-Utf8([string]$Path, [string]$Content) {
    $parent = Split-Path -Parent $Path
    if ($parent) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding($false)))
}

function New-TestRepo {
    $repo = Join-Path ([IO.Path]::GetTempPath()) ("drycycle-codemap-autofix-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $repo | Out-Null
    Invoke-Git $repo @("init", "-q") | Out-Null
    Invoke-Git $repo @("config", "user.email", "codemap-tests@example.invalid") | Out-Null
    Invoke-Git $repo @("config", "user.name", "CodeMap Tests") | Out-Null

    Write-Utf8 (Join-Path $repo "CODEMAP.md") @'
<!-- codemap:v1 -->

# Root

- `src/` — source
'@
    Write-Utf8 (Join-Path $repo "src/Area/CODEMAP.md") @'
<!-- codemap:v1 -->

# Area

- `Feature/` — feature description
'@
    Write-Utf8 (Join-Path $repo "src/Area/Feature/File.cs") "class File { }"
    Write-Utf8 (Join-Path $repo "src/Area/Feature/Keep.cs") "class Keep { }"

    Invoke-Git $repo @("add", ".") | Out-Null
    Invoke-Git $repo @("commit", "-q", "-m", "baseline") | Out-Null
    return $repo
}

function Invoke-Script(
    [string]$Script,
    [string]$Repo,
    [string[]]$Args = @()
) {
    $output = @(
        & $powerShell -NoProfile -File $Script @Args -Repository $Repo 2>&1
    )
    $exitCode = $LASTEXITCODE
    $lines = @($output | ForEach-Object { [string]$_ })

    if ($exitCode -eq 30) {
        $detail = @(
            & $powerShell -NoProfile -File $Script @Args -Repository $Repo -Detailed 2>&1
        )
        throw "internal [$($detail -join ' | ')]"
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $lines
    }
}

function Get-IndexText([string]$Repo, [string]$Path) {
    $result = Invoke-Git $Repo @("show", ":$Path")
    return ($result -join [Environment]::NewLine)
}

function Assert-Equal($Expected, $Actual, [string]$Label) {
    if ($Expected -ne $Actual) {
        throw "$Label expected=[$Expected] actual=[$Actual]"
    }
}

function Assert-True([bool]$Value, [string]$Label) {
    if (-not $Value) {
        throw $Label
    }
}

function Assert-Empty([object[]]$Value, [string]$Label) {
    if ($Value.Count -ne 0) {
        throw "$Label output=[$($Value -join ' | ')]"
    }
}

function Assert-Contains([string]$Text, [string]$Needle, [string]$Label) {
    if (-not $Text.Contains($Needle, [StringComparison]::Ordinal)) {
        throw "$Label missing=[$Needle]"
    }
}

function Assert-NotContains([string]$Text, [string]$Needle, [string]$Label) {
    if ($Text.Contains($Needle, [StringComparison]::Ordinal)) {
        throw "$Label unexpected=[$Needle]"
    }
}

$tests = New-Object System.Collections.Generic.List[scriptblock]

# Pure deletion: stale entry is mechanically removable.
$tests.Add({
    $repo = New-TestRepo
    try {
        Invoke-Git $repo @("rm", "-r", "-q", "src/Area/Feature") | Out-Null
        $result = Invoke-Script $autoFix $repo
        Assert-Equal 0 $result.ExitCode "pure delete"
        Assert-Empty $result.Output "pure delete must be silent"

        $map = Get-IndexText $repo "src/Area/CODEMAP.md"
        Assert-NotContains $map "`Feature/`" "pure delete map cleanup"

        $verify = Invoke-Script $checker $repo @("-Source", "Staged")
        Assert-Equal 0 $verify.ExitCode "pure delete verify"
        Assert-Empty $verify.Output "pure delete verify must be silent"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

# Exact directory rename: preserve semantic description and only change path.
$tests.Add({
    $repo = New-TestRepo
    try {
        Invoke-Git $repo @("mv", "src/Area/Feature", "src/Area/Renamed") | Out-Null
        $result = Invoke-Script $autoFix $repo
        Assert-Equal 0 $result.ExitCode "pure rename"
        Assert-Empty $result.Output "pure rename must be silent"

        $map = Get-IndexText $repo "src/Area/CODEMAP.md"
        Assert-Contains $map "`Renamed/` — feature description" "pure rename preserve description"
        Assert-NotContains $map "`Feature/`" "pure rename remove old path"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

# New module has no existing semantic description. Never invent one.
$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/NewFeature/New.cs") "class NewFeature { }"
        Invoke-Git $repo @("add", "src/Area/NewFeature/New.cs") | Out-Null

        $before = Get-IndexText $repo "src/Area/CODEMAP.md"
        $result = Invoke-Script $autoFix $repo
        Assert-Equal 10 $result.ExitCode "new module"
        Assert-True (($result.Output -contains "sem src/Area/CODEMAP.md +NewFeature/")) "new module semantic signal"
        $after = Get-IndexText $repo "src/Area/CODEMAP.md"
        Assert-Equal $before $after "new module must not invent description"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

# Never overwrite or stage unrelated unstaged CODEMAP edits.
$tests.Add({
    $repo = New-TestRepo
    try {
        Invoke-Git $repo @("rm", "-r", "-q", "src/Area/Feature") | Out-Null
        Add-Content -LiteralPath (Join-Path $repo "src/Area/CODEMAP.md") -Value "<!-- user edit -->"

        $result = Invoke-Script $autoFix $repo
        Assert-Equal 20 $result.ExitCode "dirty map protection"
        Assert-True (($result.Output -contains "idx src/Area/CODEMAP.md -Feature/")) "dirty map stale signal"

        $working = [IO.File]::ReadAllText((Join-Path $repo "src/Area/CODEMAP.md"))
        Assert-Contains $working "<!-- user edit -->" "dirty map user edit preserved"

        $staged = @(Invoke-Git $repo @("diff", "--cached", "--name-only", "--", "src/Area/CODEMAP.md"))
        Assert-Equal 0 $staged.Count "dirty map must not be staged"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

# Exact R100 move across two CODEMAP scopes transfers the existing description.
$tests.Add({
    $repo = New-TestRepo
    try {
        New-Item -ItemType Directory -Path (Join-Path $repo "src/Area/A") -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $repo "src/Area/B") -Force | Out-Null

        Write-Utf8 (Join-Path $repo "src/Area/CODEMAP.md") @'
<!-- codemap:v1 -->

# Area

- `A/` — source scope
- `B/` — target scope
'@
        Write-Utf8 (Join-Path $repo "src/Area/A/CODEMAP.md") @'
<!-- codemap:v1 -->

# A

- `Feature/` — transferred description
'@
        Write-Utf8 (Join-Path $repo "src/Area/B/CODEMAP.md") @'
<!-- codemap:v1 -->

# B
'@
        New-Item -ItemType Directory -Path (Join-Path $repo "src/Area/A/Feature") -Force | Out-Null
        Invoke-Git $repo @("mv", "src/Area/Feature/File.cs", "src/Area/A/Feature/File.cs") | Out-Null
        Invoke-Git $repo @("mv", "src/Area/Feature/Keep.cs", "src/Area/A/Feature/Keep.cs") | Out-Null
        Invoke-Git $repo @("add", ".") | Out-Null
        Invoke-Git $repo @("commit", "-q", "-m", "two scopes") | Out-Null

        Invoke-Git $repo @("mv", "src/Area/A/Feature", "src/Area/B/Feature") | Out-Null
        $result = Invoke-Script $autoFix $repo
        Assert-Equal 0 $result.ExitCode "cross-scope pure move"
        Assert-Empty $result.Output "cross-scope pure move must be silent"

        $sourceMap = Get-IndexText $repo "src/Area/A/CODEMAP.md"
        $targetMap = Get-IndexText $repo "src/Area/B/CODEMAP.md"
        Assert-NotContains $sourceMap "`Feature/`" "cross-scope remove source entry"
        Assert-Contains $targetMap "`Feature/` — transferred description" "cross-scope preserve description"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

# A rename with content changes is not mechanically proven. Preserve old semantics.
$tests.Add({
    $repo = New-TestRepo
    try {
        Invoke-Git $repo @("mv", "src/Area/Feature", "src/Area/Renamed") | Out-Null
        Write-Utf8 (Join-Path $repo "src/Area/Renamed/File.cs") "class File { int Changed; }"
        Invoke-Git $repo @("add", "src/Area/Renamed/File.cs") | Out-Null

        $result = Invoke-Script $autoFix $repo
        Assert-True ($result.ExitCode -in @(10, 20)) "ambiguous rename must require review"

        $map = Get-IndexText $repo "src/Area/CODEMAP.md"
        Assert-Contains $map "`Feature/` — feature description" "ambiguous rename preserves old semantics"
        Assert-NotContains $map "`Renamed/` — feature description" "ambiguous rename must not auto-transfer"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

foreach ($test in $tests) {
    & $test
}

exit 0
