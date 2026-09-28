param(
    [ValidateSet("Staged", "Range")]
    [string]$Source = "Staged",

    [string]$Base = "",
    [string]$Head = "HEAD",

    [switch]$Detailed
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ExitSemanticReview = 10
$ExitInvalidIndex = 20
$ExitInternalError = 30

$CodeMapName = "CODEMAP.md"
$CodeMapMarker = "<!-- codemap:v1 -->"
$EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"

$MaxDefaultLines = 6
$MaxDefaultChars = 800

$pathExistsCache = @{}
$scopeCache = @{}
$childDirectoryCache = @{}

function Invoke-Git(
    [string[]]$Arguments,
    [switch]$AllowFailure
) {
    $raw = @(& git @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $lines = @($raw | ForEach-Object { [string]$_ })

    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "git failed: $($Arguments[0])"
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Lines = $lines
    }
}

function Normalize-RepoPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    $normalized = $Path.Replace('\', '/').Trim()
    while ($normalized.StartsWith("./", [StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(2)
    }

    return $normalized.Trim('/')
}

function Get-ParentRepoPath([string]$Path) {
    $normalized = Normalize-RepoPath $Path
    if ([string]::IsNullOrEmpty($normalized)) {
        return ""
    }

    $separator = $normalized.LastIndexOf('/')
    if ($separator -lt 0) {
        return ""
    }

    return $normalized.Substring(0, $separator)
}

function Get-FileNameRepoPath([string]$Path) {
    $normalized = Normalize-RepoPath $Path
    if ([string]::IsNullOrEmpty($normalized)) {
        return ""
    }

    $separator = $normalized.LastIndexOf('/')
    if ($separator -lt 0) {
        return $normalized
    }

    return $normalized.Substring($separator + 1)
}

function Join-RepoPath(
    [string]$Left,
    [string]$Right
) {
    $leftNormalized = Normalize-RepoPath $Left
    $rightNormalized = Normalize-RepoPath $Right

    if ([string]::IsNullOrEmpty($leftNormalized)) {
        return $rightNormalized
    }
    if ([string]::IsNullOrEmpty($rightNormalized)) {
        return $leftNormalized
    }

    return "$leftNormalized/$rightNormalized"
}

function Get-CodeMapPath([string]$Scope) {
    return Join-RepoPath $Scope $CodeMapName
}

function Test-GitPathExists(
    [string]$Tree,
    [string]$Path
) {
    $normalized = Normalize-RepoPath $Path
    $key = "$Tree|$normalized"

    if ($pathExistsCache.ContainsKey($key)) {
        return [bool]$pathExistsCache[$key]
    }

    $objectSpec = $Tree + ":" + $normalized
    $result = Invoke-Git @("cat-file", "-e", $objectSpec) -AllowFailure
    $exists = $result.ExitCode -eq 0
    $pathExistsCache[$key] = $exists
    return $exists
}

function Find-NearestScope(
    [string]$Tree,
    [string]$StartDirectory
) {
    $directory = Normalize-RepoPath $StartDirectory
    $cacheKey = "$Tree|$directory"

    if ($scopeCache.ContainsKey($cacheKey)) {
        return [string]$scopeCache[$cacheKey]
    }

    $visited = New-Object System.Collections.Generic.List[string]

    while ($true) {
        $visited.Add($directory)
        if (Test-GitPathExists $Tree (Get-CodeMapPath $directory)) {
            foreach ($item in $visited) {
                $scopeCache["$Tree|$item"] = $directory
            }
            return $directory
        }

        if ([string]::IsNullOrEmpty($directory)) {
            break
        }

        $directory = Get-ParentRepoPath $directory
    }

    foreach ($item in $visited) {
        $scopeCache["$Tree|$item"] = [string]::Empty
    }

    return ""
}

function Get-DirectChildDirectories(
    [string]$Tree,
    [string]$Scope
) {
    $scopeNormalized = Normalize-RepoPath $Scope
    $cacheKey = "$Tree|$scopeNormalized"

    if ($childDirectoryCache.ContainsKey($cacheKey)) {
        return @($childDirectoryCache[$cacheKey])
    }

    if (-not [string]::IsNullOrEmpty($scopeNormalized) -and
        -not (Test-GitPathExists $Tree $scopeNormalized)) {
        $childDirectoryCache[$cacheKey] = @()
        return @()
    }

    $treeSpec = if ([string]::IsNullOrEmpty($scopeNormalized)) {
        $Tree
    }
    else {
        $Tree + ":" + $scopeNormalized
    }

    $result = Invoke-Git @(
        "-c",
        "core.quotepath=false",
        "ls-tree",
        "-d",
        "--name-only",
        $treeSpec
    )

    $directories = @(
        $result.Lines |
            ForEach-Object { ([string]$_).Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique
    )

    $childDirectoryCache[$cacheKey] = $directories
    return $directories
}

function Get-BlobText(
    [string]$Tree,
    [string]$Path
) {
    $objectSpec = $Tree + ":" + (Normalize-RepoPath $Path)
    $result = Invoke-Git @("show", $objectSpec)
    return ($result.Lines -join [Environment]::NewLine)
}

function Parse-CodeMap([string]$Content) {
    $lines = @($Content -split '\r?\n')
    $firstContentLine = $lines |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1

    $errors = New-Object System.Collections.Generic.List[string]
    $entries = @{}
    $ignored = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

    if ($firstContentLine -ne $CodeMapMarker) {
        $errors.Add("marker")
    }

    foreach ($line in $lines) {
        $ignoreMatch = [regex]::Match(
            $line,
            '^\s*<!--\s*codemap-ignore:\s*(?<items>.*?)\s*-->\s*$'
        )

        if ($ignoreMatch.Success) {
            foreach ($item in ($ignoreMatch.Groups["items"].Value -split ',')) {
                $name = $item.Trim().TrimEnd('/')
                if (-not [string]::IsNullOrWhiteSpace($name)) {
                    [void]$ignored.Add($name)
                }
            }
            continue
        }

        if ($line -notmatch '^\s*-\s+\x60') {
            continue
        }

        $match = [regex]::Match(
            $line,
            '^\s*-\s+\x60(?<path>[^\x60]+)\x60\s+(?:—|-)\s+.+$'
        )

        if (-not $match.Success) {
            $errors.Add("entry")
            continue
        }

        $path = $match.Groups["path"].Value.Trim()
        $validPath = $path -eq "./" -or $path -match '^[^/\\]+/$'

        if (-not $validPath) {
            $errors.Add("path")
            continue
        }

        if ($entries.ContainsKey($path)) {
            $errors.Add("duplicate")
            continue
        }

        $entries[$path] = $line
    }

    return [pscustomobject]@{
        Entries = $entries
        Ignored = $ignored
        Errors = @($errors | Sort-Object -Unique)
    }
}

function Get-Changes(
    [string]$BaseTree,
    [string]$TargetTree
) {
    $result = Invoke-Git @(
        "-c",
        "core.quotepath=false",
        "diff",
        "--name-status",
        "-M",
        "--no-ext-diff",
        "--no-textconv",
        $BaseTree,
        $TargetTree,
        "--"
    )

    $changes = New-Object System.Collections.Generic.List[object]

    foreach ($line in $result.Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $parts = @($line -split '\t')
        if ($parts.Count -lt 2) {
            throw "unexpected git diff name-status output"
        }

        $status = $parts[0]
        $oldPath = ""
        $newPath = ""

        if ($status.StartsWith("R", [StringComparison]::Ordinal) -or
            $status.StartsWith("C", [StringComparison]::Ordinal)) {
            if ($parts.Count -lt 3) {
                throw "unexpected git rename/copy output"
            }

            $oldPath = Normalize-RepoPath $parts[1]
            $newPath = Normalize-RepoPath $parts[2]
        }
        elseif ($status.StartsWith("D", [StringComparison]::Ordinal)) {
            $oldPath = Normalize-RepoPath $parts[1]
        }
        else {
            $newPath = Normalize-RepoPath $parts[1]
            if (-not $status.StartsWith("A", [StringComparison]::Ordinal)) {
                $oldPath = $newPath
            }
        }

        $changes.Add([pscustomobject]@{
            Status = $status
            OldPath = $oldPath
            NewPath = $newPath
        })
    }

    return $changes.ToArray()
}

function Add-Scope(
    [System.Collections.Generic.HashSet[string]]$Set,
    [string]$Scope
) {
    if ($null -eq $Scope) {
        return
    }

    [void]$Set.Add((Normalize-RepoPath $Scope))
}

function Add-ParentScopeForCodeMap(
    [System.Collections.Generic.HashSet[string]]$Set,
    [string]$Tree,
    [string]$CodeMapPath
) {
    if ((Get-FileNameRepoPath $CodeMapPath) -ne $CodeMapName) {
        return
    }

    $scope = Get-ParentRepoPath $CodeMapPath
    if ([string]::IsNullOrEmpty($scope)) {
        return
    }

    $parent = Get-ParentRepoPath $scope
    Add-Scope $Set (Find-NearestScope $Tree $parent)
}

function Get-AffectedScopes(
    [object[]]$Changes,
    [string]$BaseTree,
    [string]$TargetTree
) {
    $scopes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

    foreach ($change in $Changes) {
        $oldIsCodeMap = (-not [string]::IsNullOrEmpty($change.OldPath)) -and
            (Get-FileNameRepoPath $change.OldPath) -eq $CodeMapName
        $newIsCodeMap = (-not [string]::IsNullOrEmpty($change.NewPath)) -and
            (Get-FileNameRepoPath $change.NewPath) -eq $CodeMapName

        $isCodeMapChange = $oldIsCodeMap -or $newIsCodeMap
        $isStructuralChange =
            $change.Status.StartsWith("A", [StringComparison]::Ordinal) -or
            $change.Status.StartsWith("D", [StringComparison]::Ordinal) -or
            $change.Status.StartsWith("R", [StringComparison]::Ordinal) -or
            $change.Status.StartsWith("C", [StringComparison]::Ordinal) -or
            $change.Status.StartsWith("T", [StringComparison]::Ordinal)

        if (-not $isStructuralChange -and -not $isCodeMapChange) {
            continue
        }

        if (-not [string]::IsNullOrEmpty($change.OldPath)) {
            Add-Scope $scopes (
                Find-NearestScope $BaseTree (Get-ParentRepoPath $change.OldPath)
            )

            if ($oldIsCodeMap) {
                Add-ParentScopeForCodeMap $scopes $BaseTree $change.OldPath
            }
        }

        if (-not [string]::IsNullOrEmpty($change.NewPath)) {
            Add-Scope $scopes (
                Find-NearestScope $TargetTree (Get-ParentRepoPath $change.NewPath)
            )

            if ($newIsCodeMap) {
                Add-ParentScopeForCodeMap $scopes $TargetTree $change.NewPath
            }
        }
    }

    return @($scopes | Sort-Object)
}

function Test-StringSetEqual(
    [string[]]$Left,
    [string[]]$Right
) {
    if ($Left.Count -ne $Right.Count) {
        return $false
    }

    $set = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Left) {
        [void]$set.Add([string]$item)
    }

    foreach ($item in $Right) {
        if (-not $set.Contains([string]$item)) {
            return $false
        }
    }

    return $true
}

function Test-CodeMapChanged(
    [string]$BaseTree,
    [string]$TargetTree,
    [string]$Scope
) {
    $codeMapPath = Get-CodeMapPath $Scope
    $baseExists = Test-GitPathExists $BaseTree $codeMapPath
    $targetExists = Test-GitPathExists $TargetTree $codeMapPath

    if ($baseExists -ne $targetExists) {
        return $true
    }

    if (-not $targetExists) {
        return $false
    }

    $result = Invoke-Git @(
        "diff",
        "--quiet",
        $BaseTree,
        $TargetTree,
        "--",
        $codeMapPath
    ) -AllowFailure

    if ($result.ExitCode -eq 0) {
        return $false
    }

    if ($result.ExitCode -eq 1) {
        return $true
    }

    throw "git diff quiet failed"
}

function Should-ValidateScope(
    [string]$BaseTree,
    [string]$TargetTree,
    [string]$Scope
) {
    $codeMapPath = Get-CodeMapPath $Scope
    if (-not (Test-GitPathExists $TargetTree $codeMapPath)) {
        return $false
    }

    if (Test-CodeMapChanged $BaseTree $TargetTree $Scope) {
        return $true
    }

    $baseDirectories = @(Get-DirectChildDirectories $BaseTree $Scope)
    $targetDirectories = @(Get-DirectChildDirectories $TargetTree $Scope)

    return -not (Test-StringSetEqual $baseDirectories $targetDirectories)
}

function Test-Scope(
    [string]$TargetTree,
    [string]$Scope
) {
    $codeMapPath = Get-CodeMapPath $Scope
    if (-not (Test-GitPathExists $TargetTree $codeMapPath)) {
        return $null
    }

    $parsed = Parse-CodeMap (Get-BlobText $TargetTree $codeMapPath)

    if ($parsed.Errors.Count -gt 0) {
        return [pscustomobject]@{
            CodeMap = $codeMapPath
            FormatErrors = $parsed.Errors
            Missing = @()
            Stale = @()
        }
    }

    $directories = @(Get-DirectChildDirectories $TargetTree $Scope)

    $directorySet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($directory in $directories) {
        [void]$directorySet.Add("$directory/")
    }

    $entrySet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $parsed.Entries.Keys) {
        if ($path -ne "./") {
            [void]$entrySet.Add([string]$path)
        }
    }

    $missing = @(
        $directorySet |
            Where-Object {
                $name = ([string]$_).TrimEnd('/')
                -not $entrySet.Contains($_) -and
                    -not $parsed.Ignored.Contains($name)
            } |
            Sort-Object
    )

    $stale = @(
        $entrySet |
            Where-Object { -not $directorySet.Contains($_) } |
            Sort-Object
    )

    return [pscustomobject]@{
        CodeMap = $codeMapPath
        FormatErrors = @()
        Missing = $missing
        Stale = $stale
    }
}

function Write-Trace([string]$Message) {
    if ($Detailed) {
        [Console]::Out.WriteLine("trace $Message")
    }
}

function Write-MinimalOutput([string[]]$Lines) {
    if ($Detailed) {
        foreach ($line in $Lines) {
            [Console]::Out.WriteLine($line)
        }
        return
    }

    $written = 0
    $chars = 0
    $remaining = 0

    foreach ($line in $Lines) {
        if ($written -ge $MaxDefaultLines -or
            ($chars + $line.Length) -gt $MaxDefaultChars) {
            $remaining++
            continue
        }

        [Console]::Out.WriteLine($line)
        $written++
        $chars += $line.Length
    }

    if ($remaining -gt 0) {
        [Console]::Out.WriteLine("more $remaining")
    }
}

try {
    if ($null -eq (Get-Command git -ErrorAction SilentlyContinue)) {
        throw "git not found"
    }

    $repoHint = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $repoResult = Invoke-Git @("-C", $repoHint, "rev-parse", "--show-toplevel")
    if ($repoResult.Lines.Count -ne 1) {
        throw "repository root unavailable"
    }

    $repoRoot = $repoResult.Lines[0]
    Push-Location $repoRoot

    try {
        if ($Source -eq "Staged") {
            $baseResult = Invoke-Git @(
                "rev-parse",
                "--verify",
                "HEAD^{tree}"
            ) -AllowFailure

            $baseTree = if ($baseResult.ExitCode -eq 0) {
                $baseResult.Lines[0].Trim()
            }
            else {
                $EmptyTree
            }

            $targetResult = Invoke-Git @("write-tree")
            $targetTree = $targetResult.Lines[0].Trim()
        }
        else {
            if ([string]::IsNullOrWhiteSpace($Base) -or
                [string]::IsNullOrWhiteSpace($Head)) {
                throw "Range requires Base and Head"
            }

            $baseResult = Invoke-Git @(
                "rev-parse",
                "--verify",
                "$Base^{tree}"
            )

            $targetResult = Invoke-Git @(
                "rev-parse",
                "--verify",
                "$Head^{tree}"
            )

            $baseTree = $baseResult.Lines[0].Trim()
            $targetTree = $targetResult.Lines[0].Trim()
        }

        $changes = @(Get-Changes $baseTree $targetTree)
        Write-Trace ("changes=" + $changes.Count)
        if ($changes.Count -eq 0) {
            exit 0
        }

        $rootMapExisted = Test-GitPathExists $baseTree $CodeMapName
        $rootMapExists = Test-GitPathExists $targetTree $CodeMapName

        if ($rootMapExisted -and -not $rootMapExists) {
            Write-MinimalOutput @("idx $CodeMapName -root")
            exit $ExitInvalidIndex
        }

        $affectedScopes = @(
            Get-AffectedScopes $changes $baseTree $targetTree
        )

        Write-Trace ("scopes=" + ($affectedScopes -join ","))
        if ($affectedScopes.Count -eq 0) {
            exit 0
        }

        $semanticLines = New-Object System.Collections.Generic.List[string]
        $invalidLines = New-Object System.Collections.Generic.List[string]

        foreach ($scope in $affectedScopes) {
            $shouldValidate = Should-ValidateScope $baseTree $targetTree $scope
            Write-Trace ("scope=" + $scope + " validate=" + $shouldValidate)
            if (-not $shouldValidate) {
                continue
            }

            $result = Test-Scope $targetTree $scope
            if ($null -eq $result) {
                Write-Trace ("scope=" + $scope + " result=null")
                continue
            }

            Write-Trace (
                "scope=" + $scope +
                " missing=" + ($result.Missing -join ",") +
                " stale=" + ($result.Stale -join ",")
            )

            if ($result.FormatErrors.Count -gt 0) {
                $invalidLines.Add("fmt $($result.CodeMap)")
                continue
            }

            if ($result.Stale.Count -gt 0) {
                $invalidLines.Add(
                    "idx $($result.CodeMap) -$($result.Stale -join ',-')"
                )
            }

            if ($result.Missing.Count -gt 0) {
                $semanticLines.Add(
                    "sem $($result.CodeMap) +$($result.Missing -join ',+')"
                )
            }
        }

        if ($invalidLines.Count -gt 0) {
            Write-MinimalOutput @($invalidLines + $semanticLines)
            exit $ExitInvalidIndex
        }

        if ($semanticLines.Count -gt 0) {
            Write-MinimalOutput @($semanticLines)
            exit $ExitSemanticReview
        }

        exit 0
    }
    finally {
        Pop-Location
    }
}
catch {
    if ($Detailed) {
        $line = $_.InvocationInfo.ScriptLineNumber
        [Console]::Out.WriteLine(
            "internal line=$line $($_.Exception.Message)"
        )
    }
    else {
        [Console]::Out.WriteLine("internal")
    }

    exit $ExitInternalError
}
