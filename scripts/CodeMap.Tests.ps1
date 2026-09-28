param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
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

function Invoke-Git([string]$WorkingDirectory, [string[]]$Arguments) {
    Push-Location $WorkingDirectory
    try {
        $output = @(& git @Arguments 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "git failed: $($Arguments -join ' ')"
        }
        return @($output | ForEach-Object { [string]$_ })
    }
    finally {
        Pop-Location
    }
}

function Write-Utf8([string]$Path, [string]$Content) {
    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrEmpty($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding($false)))
}

function New-TestRepo {
    $path = Join-Path ([IO.Path]::GetTempPath()) ("drycycle-codemap-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $path | Out-Null

    Invoke-Git $path @("init", "-q") | Out-Null
    Invoke-Git $path @("config", "user.email", "codemap-tests@example.invalid") | Out-Null
    Invoke-Git $path @("config", "user.name", "CodeMap Tests") | Out-Null

    Write-Utf8 (Join-Path $path "CODEMAP.md") @'
<!-- codemap:v1 -->

# Root

- `src/` — source
'@
    Write-Utf8 (Join-Path $path "src/Area/CODEMAP.md") @'
<!-- codemap:v1 -->

# Area

- `Feature/` — feature
'@
    Write-Utf8 (Join-Path $path "src/Area/Feature/File.cs") "class File { }"

    Invoke-Git $path @("add", ".") | Out-Null
    Invoke-Git $path @("commit", "-q", "-m", "baseline") | Out-Null
    return $path
}

function Invoke-Checker(
    [string]$WorkingDirectory,
    [string[]]$Arguments
) {
    Push-Location $WorkingDirectory
    try {
        $output = @(& $powerShell -NoProfile -File $checker @Arguments -Repository $WorkingDirectory 2>&1)
        $exitCode = $LASTEXITCODE
        $lines = @($output | ForEach-Object { [string]$_ })
        if ($exitCode -notin @(0, 10, 20, 30)) {
            throw "checker host failure exit=$exitCode output=[$($lines -join ' | ')]"
        }
        if ($exitCode -eq 30) {
            $detail = @(& $powerShell -NoProfile -File $checker @Arguments -Repository $WorkingDirectory -Detailed 2>&1)
            throw "checker internal output=[$($detail -join ' | ')]"
        }
        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = $lines
        }
    }
    finally {
        Pop-Location
    }
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) {
        throw "$Message expected=[$Expected] actual=[$Actual]"
    }
}

function Assert-Empty([object[]]$Value, [string]$Message) {
    if ($Value.Count -ne 0) {
        throw "$Message output=[$($Value -join ' | ')]"
    }
}

function Assert-Contains([object[]]$Value, [string]$Expected, [string]$Message) {
    if (-not ($Value -contains $Expected)) {
        throw "$Message expected=[$Expected] output=[$($Value -join ' | ')]"
    }
}

$tests = New-Object System.Collections.Generic.List[scriptblock]

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/Feature/File.cs") "class File { int X; }"
        Invoke-Git $repo @("add", "src/Area/Feature/File.cs") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $result.ExitCode "modified file"
        Assert-Empty $result.Output "modified file must be silent"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/Feature/New.cs") "class NewFile { }"
        Invoke-Git $repo @("add", "src/Area/Feature/New.cs") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $result.ExitCode "new file in existing module"
        Assert-Empty $result.Output "new file in existing module must be silent"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/NewFeature/New.cs") "class NewFeature { }"
        Invoke-Git $repo @("add", "src/Area/NewFeature/New.cs") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged", "-Detailed")
        if ($result.ExitCode -ne 10) {
            throw "new direct module expected=[10] actual=[$($result.ExitCode)] output=[$($result.Output -join ' | ')]"
        }
        Assert-Contains $result.Output "sem src/Area/CODEMAP.md +NewFeature/" "new direct module"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Invoke-Git $repo @("rm", "-q", "src/Area/Feature/File.cs") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 20 $result.ExitCode "deleted direct module"
        Assert-Contains $result.Output "idx src/Area/CODEMAP.md -Feature/" "deleted direct module"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/Feature/CODEMAP.md") @'
<!-- codemap:v1 -->

# Feature
'@
        Invoke-Git $repo @("add", "src/Area/Feature/CODEMAP.md") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $result.ExitCode "nested codemap"
        Assert-Empty $result.Output "nested codemap must be silent"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/CODEMAP.md") "# invalid"
        Invoke-Git $repo @("add", "src/Area/CODEMAP.md") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 20 $result.ExitCode "invalid codemap"
        Assert-Contains $result.Output "fmt src/Area/CODEMAP.md" "invalid codemap"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/Feature/File.cs") "class File { int Staged; }"
        Invoke-Git $repo @("add", "src/Area/Feature/File.cs") | Out-Null
        Write-Utf8 (Join-Path $repo "src/Area/Unstaged/New.cs") "class Unstaged { }"

        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $result.ExitCode "partial staging"
        Assert-Empty $result.Output "unstaged structure must be ignored"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        $base = @(Invoke-Git $repo @("rev-parse", "HEAD"))[0].Trim()
        Write-Utf8 (Join-Path $repo "src/Area/NewFeature/New.cs") "class NewFeature { }"
        Invoke-Git $repo @("add", ".") | Out-Null
        Invoke-Git $repo @("commit", "-q", "-m", "new module without map") | Out-Null
        $head = @(Invoke-Git $repo @("rev-parse", "HEAD"))[0].Trim()

        $result = Invoke-Checker $repo @("-Source", "Range", "-Base", $base, "-Head", $head)
        Assert-Equal 10 $result.ExitCode "range mode"
        Assert-Contains $result.Output "sem src/Area/CODEMAP.md +NewFeature/" "range mode"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$passed = 0
foreach ($test in $tests) {
    & $test
    $passed++
}

[Console]::Out.WriteLine("CodeMap tests: $passed passed")
