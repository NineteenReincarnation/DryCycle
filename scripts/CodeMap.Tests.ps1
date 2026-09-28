param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$checker = Join-Path $PSScriptRoot "CodeMap.ps1"
$autofixer = Join-Path $PSScriptRoot "CodeMap.AutoFix.ps1"
$installer = Join-Path $PSScriptRoot "Install-CodeMapHooks.ps1"
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
    Write-Utf8 (Join-Path $path "src/Area/Feature/Keep.cs") "class Keep { }"

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

function Invoke-AutoFix(
    [string]$WorkingDirectory,
    [string[]]$Arguments = @()
) {
    Push-Location $WorkingDirectory
    try {
        $output = @(& $powerShell -NoProfile -File $autofixer @Arguments -Repository $WorkingDirectory 2>&1)
        $exitCode = $LASTEXITCODE
        $lines = @($output | ForEach-Object { [string]$_ })
        if ($exitCode -notin @(0, 10, 20, 30)) {
            throw "autofix host failure exit=$exitCode output=[$($lines -join ' | ')]"
        }
        if ($exitCode -eq 30) {
            $detail = @(& $powerShell -NoProfile -File $autofixer @Arguments -Repository $WorkingDirectory -Detailed 2>&1)
            throw "autofix internal output=[$($detail -join ' | ')]"
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

function Invoke-Installer(
    [string]$WorkingDirectory,
    [string[]]$Arguments = @()
) {
    $scripts = Join-Path $WorkingDirectory "scripts"
    $hooks = Join-Path $WorkingDirectory ".githooks"
    New-Item -ItemType Directory -Path $scripts -Force | Out-Null
    New-Item -ItemType Directory -Path $hooks -Force | Out-Null
    Copy-Item -LiteralPath $installer -Destination (Join-Path $scripts "Install-CodeMapHooks.ps1") -Force
    $hookText = "#!/bin/sh" + [Environment]::NewLine + "exit 0" + [Environment]::NewLine
    Write-Utf8 (Join-Path $hooks "pre-commit") $hookText
    Write-Utf8 (Join-Path $hooks "pre-push") $hookText

    $copy = Join-Path $scripts "Install-CodeMapHooks.ps1"
    $output = @(& $powerShell -NoProfile -File $copy @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "installer failed exit=$exitCode output=[$($output -join ' | ')]"
    }
    return @($output | ForEach-Object { [string]$_ })
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
        Invoke-Git $repo @("rm", "-r", "-q", "src/Area/Feature") | Out-Null
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

$tests.Add({
    $repo = New-TestRepo
    try {
        Invoke-Git $repo @("rm", "-q", "src/Area/Feature/File.cs") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $result.ExitCode "delete ordinary file"
        Assert-Empty $result.Output "delete ordinary file must be silent"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Invoke-Git $repo @("mv", "src/Area/Feature/File.cs", "src/Area/Feature/Renamed.cs") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $result.ExitCode "rename ordinary file"
        Assert-Empty $result.Output "rename ordinary file must be silent"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        New-Item -ItemType Directory -Path (Join-Path $repo "src/Area/Feature/Internal") -Force | Out-Null
        Invoke-Git $repo @("mv", "src/Area/Feature/File.cs", "src/Area/Feature/Internal/File.cs") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $result.ExitCode "move inside module"
        Assert-Empty $result.Output "move inside module must be silent"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Invoke-Git $repo @("mv", "src/Area/Feature", "src/Area/Renamed") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 20 $result.ExitCode "rename direct module"
        Assert-Contains $result.Output "idx src/Area/CODEMAP.md -Feature/" "rename direct module stale"
        Assert-Contains $result.Output "sem src/Area/CODEMAP.md +Renamed/" "rename direct module missing"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/Feature/Child/File.cs") "class Child { }"
        Write-Utf8 (Join-Path $repo "src/Area/Feature/CODEMAP.md") @'
<!-- codemap:v1 -->

# Feature

- `Child/` — child
'@
        Invoke-Git $repo @("add", ".") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $result.ExitCode "nested codemap with child"
        Assert-Empty $result.Output "valid nested codemap must be silent"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/Feature/Child/File.cs") "class Child { }"
        Write-Utf8 (Join-Path $repo "src/Area/Feature/CODEMAP.md") @'
<!-- codemap:v1 -->

# Feature
'@
        Invoke-Git $repo @("add", ".") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 10 $result.ExitCode "nested codemap missing child"
        Assert-Contains $result.Output "sem src/Area/Feature/CODEMAP.md +Child/" "nested codemap missing child"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/Feature/Child/File.cs") "class Child { }"
        Write-Utf8 (Join-Path $repo "src/Area/Feature/CODEMAP.md") @'
<!-- codemap:v1 -->

# Feature

- `Child/` — child
'@
        Invoke-Git $repo @("add", ".") | Out-Null
        Invoke-Git $repo @("commit", "-q", "-m", "nested scope") | Out-Null
        Invoke-Git $repo @("rm", "-q", "src/Area/Feature/CODEMAP.md") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $result.ExitCode "delete nested codemap"
        Assert-Empty $result.Output "deleted nested codemap must fall back silently"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/中文功能/File.cs") "class UnicodeFeature { }"
        Invoke-Git $repo @("add", ".") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 10 $result.ExitCode "unicode module"
        Assert-Contains $result.Output "sem src/Area/CODEMAP.md +中文功能/" "unicode module"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/Feature Space/File.cs") "class SpacedFeature { }"
        Invoke-Git $repo @("add", ".") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 10 $result.ExitCode "space module"
        Assert-Contains $result.Output "sem src/Area/CODEMAP.md +Feature Space/" "space module"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/Generated/File.cs") "class Generated { }"
        Write-Utf8 (Join-Path $repo "src/Area/CODEMAP.md") @'
<!-- codemap:v1 -->
<!-- codemap-ignore: Generated -->

# Area

- `Feature/` — feature
'@
        Invoke-Git $repo @("add", ".") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $result.ExitCode "ignored module"
        Assert-Empty $result.Output "ignored module must be silent"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/CODEMAP.md") @'
<!-- codemap:v1 -->

# Area

- `./` — direct implementation
- `Feature/` — feature
'@
        Invoke-Git $repo @("add", "src/Area/CODEMAP.md") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $result.ExitCode "dot entry"
        Assert-Empty $result.Output "dot entry must be silent"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "CODEMAP.md") @'
<!-- codemap:v1 -->

# Root

- `src/` — source
- DevTool route -> `src/Area/`
'@
        Invoke-Git $repo @("add", "CODEMAP.md") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $result.ExitCode "non-index bullet"
        Assert-Empty $result.Output "non-index bullet must be ignored"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/CODEMAP.md") @'
<!-- codemap:v1 -->

# Area
'@
        Invoke-Git $repo @("add", "src/Area/CODEMAP.md") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 10 $result.ExitCode "missing existing entry"
        Assert-Contains $result.Output "sem src/Area/CODEMAP.md +Feature/" "missing existing entry"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/CODEMAP.md") @'
<!-- codemap:v1 -->

# Area

- `Feature/` — feature
- `Ghost/` — stale
'@
        Invoke-Git $repo @("add", "src/Area/CODEMAP.md") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 20 $result.ExitCode "stale map entry"
        Assert-Contains $result.Output "idx src/Area/CODEMAP.md -Ghost/" "stale map entry"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Invoke-Git $repo @("rm", "-q", "CODEMAP.md") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 20 $result.ExitCode "delete root map"
        Assert-Contains $result.Output "idx CODEMAP.md -root" "delete root map"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        $base = @(Invoke-Git $repo @("rev-parse", "HEAD"))[0].Trim()
        Write-Utf8 (Join-Path $repo "src/Area/NewFeature/New.cs") "class NewFeature { }"
        Invoke-Git $repo @("add", ".") | Out-Null
        Invoke-Git $repo @("commit", "-q", "-m", "new module default head") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Range", "-Base", $base)
        Assert-Equal 10 $result.ExitCode "range default head"
        Assert-Contains $result.Output "sem src/Area/CODEMAP.md +NewFeature/" "range default head"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Other/CODEMAP.md") "# intentionally invalid"
        Write-Utf8 (Join-Path $repo "src/Other/File.cs") "class Other { }"
        Invoke-Git $repo @("add", ".") | Out-Null
        Invoke-Git $repo @("commit", "-q", "-m", "unrelated invalid scope") | Out-Null
        Write-Utf8 (Join-Path $repo "src/Area/Feature/File.cs") "class File { int X; }"
        Invoke-Git $repo @("add", "src/Area/Feature/File.cs") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $result.ExitCode "unrelated invalid scope"
        Assert-Empty $result.Output "unrelated scope must not be scanned"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = Join-Path ([IO.Path]::GetTempPath()) ("drycycle-codemap-initial-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $repo | Out-Null
    try {
        Invoke-Git $repo @("init", "-q") | Out-Null
        Invoke-Git $repo @("config", "user.email", "codemap-tests@example.invalid") | Out-Null
        Invoke-Git $repo @("config", "user.name", "CodeMap Tests") | Out-Null
        Write-Utf8 (Join-Path $repo "CODEMAP.md") @'
<!-- codemap:v1 -->

# Root

- `src/` — source
'@
        Write-Utf8 (Join-Path $repo "src/Area/File.cs") "class Initial { }"
        Invoke-Git $repo @("add", ".") | Out-Null
        $result = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $result.ExitCode "initial commit"
        Assert-Empty $result.Output "initial commit must be silent"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})
$tests.Add({
    $repo = New-TestRepo
    try {
        Invoke-Git $repo @("mv", "src/Area/Feature", "src/Area/Renamed") | Out-Null
        $result = Invoke-AutoFix $repo
        Assert-Equal 0 $result.ExitCode "autofix pure rename"
        Assert-Empty $result.Output "autofix pure rename must be silent"

        $map = [IO.File]::ReadAllText((Join-Path $repo "src/Area/CODEMAP.md"))
        if ($map -notmatch [regex]::Escape([string][char]96 + "Renamed/" + [string][char]96)) {
            throw "autofix pure rename did not update target entry"
        }
        if ($map -match [regex]::Escape([string][char]96 + "Feature/" + [string][char]96)) {
            throw "autofix pure rename left stale entry"
        }

        $check = Invoke-Checker $repo @("-Source", "Staged")
        Assert-Equal 0 $check.ExitCode "autofix pure rename final check"
        Assert-Empty $check.Output "autofix pure rename final check must be silent"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Invoke-Git $repo @("rm", "-r", "-q", "src/Area/Feature") | Out-Null
        $result = Invoke-AutoFix $repo
        Assert-Equal 0 $result.ExitCode "autofix delete module"
        Assert-Empty $result.Output "autofix delete module must be silent"

        $map = [IO.File]::ReadAllText((Join-Path $repo "src/Area/CODEMAP.md"))
        if ($map -match [regex]::Escape([string][char]96 + "Feature/" + [string][char]96)) {
            throw "autofix delete module left stale entry"
        }
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Area/NewFeature/New.cs") "class NewFeature { }"
        Invoke-Git $repo @("add", ".") | Out-Null

        $before = [IO.File]::ReadAllText((Join-Path $repo "src/Area/CODEMAP.md"))
        $result = Invoke-AutoFix $repo
        Assert-Equal 10 $result.ExitCode "autofix new semantic module"
        Assert-Contains $result.Output "sem src/Area/CODEMAP.md +NewFeature/" "autofix new semantic module"

        $after = [IO.File]::ReadAllText((Join-Path $repo "src/Area/CODEMAP.md"))
        Assert-Equal $before $after "autofix must not invent semantic description"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Invoke-Git $repo @("rm", "-r", "-q", "src/Area/Feature") | Out-Null
        Add-Content -LiteralPath (Join-Path $repo "src/Area/CODEMAP.md") -Value ([Environment]::NewLine + "<!-- user edit -->")

        $before = [IO.File]::ReadAllText((Join-Path $repo "src/Area/CODEMAP.md"))
        $result = Invoke-AutoFix $repo
        Assert-Equal 20 $result.ExitCode "autofix protects unstaged map edit"

        $after = [IO.File]::ReadAllText((Join-Path $repo "src/Area/CODEMAP.md"))
        Assert-Equal $before $after "autofix overwrote unstaged map edit"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Write-Utf8 (Join-Path $repo "src/Other/CODEMAP.md") @'
<!-- codemap:v1 -->

# Other

- `Base/` — base
'@
        Write-Utf8 (Join-Path $repo "src/Other/Base/File.cs") "class Base { }"
        Invoke-Git $repo @("add", ".") | Out-Null
        Invoke-Git $repo @("commit", "-q", "-m", "other scope") | Out-Null

        New-Item -ItemType Directory -Path (Join-Path $repo "src/Other") -Force | Out-Null
        Invoke-Git $repo @("mv", "src/Area/Feature", "src/Other/Moved") | Out-Null

        $result = Invoke-AutoFix $repo
        Assert-Equal 0 $result.ExitCode "autofix cross-scope move"
        Assert-Empty $result.Output "autofix cross-scope move must be silent"

        $oldMap = [IO.File]::ReadAllText((Join-Path $repo "src/Area/CODEMAP.md"))
        $newMap = [IO.File]::ReadAllText((Join-Path $repo "src/Other/CODEMAP.md"))
        if ($oldMap -match [regex]::Escape([string][char]96 + "Feature/" + [string][char]96)) {
            throw "autofix cross-scope move left old entry"
        }
        if ($newMap -notmatch [regex]::Escape([string][char]96 + "Moved/" + [string][char]96)) {
            throw "autofix cross-scope move did not migrate entry"
        }
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        $head = @(Invoke-Git $repo @("rev-parse", "HEAD"))[0].Trim()
        $result = Invoke-Checker $repo @("-Source", "Range", "-Base", "EMPTY", "-Head", $head)
        Assert-Equal 0 $result.ExitCode "empty range baseline"
        Assert-Empty $result.Output "empty range baseline must be silent"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Invoke-Git $repo @("rm", "-r", "-q", "src/Area/Feature") | Out-Null
        Write-Utf8 (Join-Path $repo "src/Area/NewFeature/New.cs") "class NewFeature { }"
        Invoke-Git $repo @("add", ".") | Out-Null

        $result = Invoke-AutoFix $repo
        Assert-Equal 10 $result.ExitCode "autofix delete plus unrelated semantic add"
        Assert-Contains $result.Output "sem src/Area/CODEMAP.md +NewFeature/" "autofix keeps semantic add"

        $mapText = [IO.File]::ReadAllText((Join-Path $repo "src/Area/CODEMAP.md"))
        if ($mapText -match [regex]::Escape([string][char]96 + "Feature/" + [string][char]96)) {
            throw "autofix delete plus unrelated add left stale entry"
        }
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        $output = Invoke-Installer $repo
        Assert-Empty $output "installer normal install must be silent"
        $configured = @(Invoke-Git $repo @("config", "--local", "--get", "core.hooksPath"))
        Assert-Equal ".githooks" $configured[0].Trim() "installer hooks path"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        Invoke-Git $repo @("config", "--local", "core.hooksPath", ".custom-hooks") | Out-Null
        $output = Invoke-Installer $repo @("-IfUnset")
        Assert-Empty $output "installer IfUnset must be silent"
        $configured = @(Invoke-Git $repo @("config", "--local", "--get", "core.hooksPath"))
        Assert-Equal ".custom-hooks" $configured[0].Trim() "installer must preserve custom hooks path"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

$tests.Add({
    $repo = New-TestRepo
    try {
        [void](Invoke-Installer $repo)
        [void](Invoke-Installer $repo @("-Disable"))
        $raw = @(& git -C $repo config --local --get core.hooksPath 2>&1)
        Assert-Equal 1 $LASTEXITCODE "installer disable must unset hooks path"
        Assert-Empty $raw "installer disable must leave no hooks path"
    }
    finally { Remove-Item -LiteralPath $repo -Recurse -Force }
})

foreach ($test in $tests) {
    & $test
}

exit 0
