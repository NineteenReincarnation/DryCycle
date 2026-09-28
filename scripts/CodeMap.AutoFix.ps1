param(
    [string]$Repository = "",
    [switch]$Detailed
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$checker = Join-Path $PSScriptRoot "CodeMap.ps1"
$powerShell = if (Get-Command pwsh -ErrorAction SilentlyContinue) {
    (Get-Command pwsh).Source
}
elseif (Get-Command powershell.exe -ErrorAction SilentlyContinue) {
    (Get-Command powershell.exe).Source
}
else {
    $null
}

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

    return $Path.Replace('\', '/').Trim().Trim('/')
}

function Get-ParentRepoPath([string]$Path) {
    $normalized = Normalize-RepoPath $Path
    $separator = $normalized.LastIndexOf('/')
    if ($separator -lt 0) {
        return ""
    }

    return $normalized.Substring(0, $separator)
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

    return "$leftNormalized/$rightNormalized"
}

function Test-UnderDirectory(
    [string]$Path,
    [string]$Directory
) {
    $pathNormalized = Normalize-RepoPath $Path
    $directoryNormalized = Normalize-RepoPath $Directory

    if ([string]::IsNullOrEmpty($pathNormalized) -or
        [string]::IsNullOrEmpty($directoryNormalized)) {
        return $false
    }

    return $pathNormalized.StartsWith(
        $directoryNormalized + "/",
        [StringComparison]::Ordinal
    )
}

function Invoke-Checker([string]$RepoRoot) {
    if ($null -eq $powerShell) {
        throw "PowerShell host not found"
    }

    $arguments = @(
        "-NoProfile",
        "-File",
        $checker,
        "-Source",
        "Staged",
        "-Repository",
        $RepoRoot
    )

    if ($Detailed) {
        $arguments += "-Detailed"
    }

    $output = @(& $powerShell @arguments 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Lines = @($output | ForEach-Object { [string]$_ })
    }
}

function Get-StagedChanges {
    $result = Invoke-Git @(
        "-c",
        "core.quotepath=false",
        "diff",
        "--cached",
        "--name-status",
        "-M100%",
        "--no-ext-diff",
        "--no-textconv",
        "--"
    )

    $changes = New-Object System.Collections.Generic.List[object]

    foreach ($line in $result.Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $parts = @($line -split '\t')
        $status = $parts[0]
        $oldPath = ""
        $newPath = ""

        if ($status.StartsWith("R", [StringComparison]::Ordinal)) {
            if ($parts.Count -lt 3) {
                continue
            }

            $oldPath = Normalize-RepoPath $parts[1]
            $newPath = Normalize-RepoPath $parts[2]
        }
        elseif ($status.StartsWith("D", [StringComparison]::Ordinal)) {
            $oldPath = Normalize-RepoPath $parts[1]
        }
        else {
            if ($parts.Count -ge 2) {
                $newPath = Normalize-RepoPath $parts[1]
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

function Test-PureDirectoryMove(
    [object[]]$Changes,
    [string]$OldDirectory,
    [string]$NewDirectory
) {
    $oldPrefix = (Normalize-RepoPath $OldDirectory) + "/"
    $newPrefix = (Normalize-RepoPath $NewDirectory) + "/"

    $relevant = @(
        $Changes |
            Where-Object {
                Test-UnderDirectory $_.OldPath $OldDirectory -or
                Test-UnderDirectory $_.NewPath $NewDirectory
            }
    )

    if ($relevant.Count -eq 0) {
        return $false
    }

    foreach ($change in $relevant) {
        if ($change.Status -ne "R100") {
            return $false
        }

        if (-not $change.OldPath.StartsWith($oldPrefix, [StringComparison]::Ordinal) -or
            -not $change.NewPath.StartsWith($newPrefix, [StringComparison]::Ordinal)) {
            return $false
        }

        $oldSuffix = $change.OldPath.Substring($oldPrefix.Length)
        $newSuffix = $change.NewPath.Substring($newPrefix.Length)
        if ($oldSuffix -ne $newSuffix) {
            return $false
        }
    }

    return $true
}

function Parse-CheckerOutput([string[]]$Lines) {
    $stale = New-Object System.Collections.Generic.List[object]
    $missing = New-Object System.Collections.Generic.List[object]

    foreach ($line in $Lines) {
        if ($line.StartsWith("idx ", [StringComparison]::Ordinal)) {
            $split = $line.LastIndexOf(" -", [StringComparison]::Ordinal)
            if ($split -lt 4) {
                continue
            }

            $map = $line.Substring(4, $split - 4)
            $items = $line.Substring($split + 2) -split ',-'
            foreach ($item in $items) {
                if (-not [string]::IsNullOrWhiteSpace($item)) {
                    $entry = $item.Trim()
                    $scope = Get-ParentRepoPath $map
                    $stale.Add([pscustomobject]@{
                        Map = $map
                        Scope = $scope
                        Entry = $entry
                        Directory = Join-RepoPath $scope $entry.TrimEnd('/')
                    })
                }
            }
        }
        elseif ($line.StartsWith("sem ", [StringComparison]::Ordinal)) {
            $split = $line.LastIndexOf(" +", [StringComparison]::Ordinal)
            if ($split -lt 4) {
                continue
            }

            $map = $line.Substring(4, $split - 4)
            $items = $line.Substring($split + 2) -split ',\+'
            foreach ($item in $items) {
                if (-not [string]::IsNullOrWhiteSpace($item)) {
                    $entry = $item.Trim()
                    $scope = Get-ParentRepoPath $map
                    $missing.Add([pscustomobject]@{
                        Map = $map
                        Scope = $scope
                        Entry = $entry
                        Directory = Join-RepoPath $scope $entry.TrimEnd('/')
                    })
                }
            }
        }
    }

    return [pscustomobject]@{
        Stale = $stale.ToArray()
        Missing = $missing.ToArray()
    }
}

function Test-MapEditable(
    [string]$RepoRoot,
    [string]$MapPath
) {
    $absolute = Join-Path $RepoRoot ($MapPath.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) {
        return $false
    }

    $result = Invoke-Git @("diff", "--quiet", "--", $MapPath) -AllowFailure
    return $result.ExitCode -eq 0
}

function New-EditState {
    return [pscustomobject]@{
        Remove = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        Replace = @{}
        Add = New-Object System.Collections.Generic.List[string]
    }
}

function Get-EditState(
    [hashtable]$Edits,
    [string]$MapPath
) {
    if (-not $Edits.ContainsKey($MapPath)) {
        $Edits[$MapPath] = New-EditState
    }

    return $Edits[$MapPath]
}

function Read-EntryLines(
    [string]$RepoRoot,
    [string]$MapPath
) {
    $absolute = Join-Path $RepoRoot ($MapPath.Replace('/', [IO.Path]::DirectorySeparatorChar))
    $content = [IO.File]::ReadAllText($absolute)
    $entries = @{}

    foreach ($line in @($content -split '\r?\n')) {
        $match = [regex]::Match(
            $line,
            '^\s*-\s+\x60(?<path>[^\x60]+)\x60\s+(?:—|-)\s+.+$'
        )

        if ($match.Success) {
            $entries[$match.Groups["path"].Value.Trim()] = $line
        }
    }

    return [pscustomobject]@{
        Content = $content
        Entries = $entries
    }
}

function Convert-EntryLine(
    [string]$Line,
    [string]$OldEntry,
    [string]$NewEntry
) {
    $tick = [string][char]96
    return $Line.Replace(
        $tick + $OldEntry + $tick,
        $tick + $NewEntry + $tick
    )
}

function Apply-MapEdit(
    [string]$RepoRoot,
    [string]$MapPath,
    $Edit
) {
    if (-not (Test-MapEditable $RepoRoot $MapPath)) {
        return $false
    }

    $snapshot = Read-EntryLines $RepoRoot $MapPath
    $content = $snapshot.Content
    $newline = if ($content.Contains(([string][char]13) + ([string][char]10))) {
        ([string][char]13) + ([string][char]10)
    }
    else {
        [string][char]10
    }

    $lines = @($content -split '\r?\n')
    if ($lines.Count -gt 0 -and
        [string]::IsNullOrEmpty($lines[$lines.Count - 1])) {
        $lines = @($lines[0..($lines.Count - 2)])
    }

    $result = New-Object System.Collections.Generic.List[string]
    $lastEntryOutputIndex = -1

    foreach ($line in $lines) {
        $match = [regex]::Match(
            $line,
            '^\s*-\s+\x60(?<path>[^\x60]+)\x60\s+(?:—|-)\s+.+$'
        )

        if ($match.Success) {
            $entry = $match.Groups["path"].Value.Trim()
            if ($Edit.Remove.Contains($entry)) {
                continue
            }

            if ($Edit.Replace.ContainsKey($entry)) {
                $line = Convert-EntryLine $line $entry $Edit.Replace[$entry]
            }

            $lastEntryOutputIndex = $result.Count
        }

        $result.Add($line)
    }

    if ($Edit.Add.Count -gt 0) {
        $insertAt = $lastEntryOutputIndex + 1
        foreach ($line in $Edit.Add) {
            $result.Insert($insertAt, $line)
            $insertAt++
        }
    }

    $newContent = ($result.ToArray() -join $newline) + $newline
    $absolute = Join-Path $RepoRoot ($MapPath.Replace('/', [IO.Path]::DirectorySeparatorChar))
    [IO.File]::WriteAllText(
        $absolute,
        $newContent,
        (New-Object Text.UTF8Encoding($false))
    )

    Invoke-Git @("add", "--", $MapPath) | Out-Null
    return $true
}

try {
    if ($null -eq $powerShell) {
        throw "PowerShell host not found"
    }

    $repoArgs = if ([string]::IsNullOrWhiteSpace($Repository)) {
        @("rev-parse", "--show-toplevel")
    }
    else {
        @("-C", ([IO.Path]::GetFullPath($Repository)), "rev-parse", "--show-toplevel")
    }

    $repoResult = Invoke-Git $repoArgs
    if ($repoResult.Lines.Count -ne 1) {
        throw "repository root unavailable"
    }

    $repoRoot = $repoResult.Lines[0].Trim()
    Push-Location $repoRoot

    try {
        $check = Invoke-Checker $repoRoot
        if ($check.ExitCode -eq 0) {
            exit 0
        }

        if ($check.ExitCode -eq 30 -or
            ($check.Lines | Where-Object { $_.StartsWith("fmt ", [StringComparison]::Ordinal) }).Count -gt 0) {
            foreach ($line in $check.Lines) {
                [Console]::Out.WriteLine($line)
            }
            exit $check.ExitCode
        }

        $issues = Parse-CheckerOutput $check.Lines
        if ($issues.Stale.Count -eq 0) {
            foreach ($line in $check.Lines) {
                [Console]::Out.WriteLine($line)
            }
            exit $check.ExitCode
        }

        $changes = @(Get-StagedChanges)
        $edits = @{}
        $handledMissing = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

        foreach ($stale in $issues.Stale) {
            if (-not (Test-MapEditable $repoRoot $stale.Map)) {
                continue
            }

            $candidates = @(
                $issues.Missing |
                    Where-Object {
                        -not $handledMissing.Contains($_.Map + "|" + $_.Entry) -and
                        (Test-MapEditable $repoRoot $_.Map) -and
                        (Test-PureDirectoryMove $changes $stale.Directory $_.Directory)
                    }
            )

            if ($candidates.Count -eq 1) {
                $target = $candidates[0]
                $sourceSnapshot = Read-EntryLines $repoRoot $stale.Map
                if (-not $sourceSnapshot.Entries.ContainsKey($stale.Entry)) {
                    continue
                }

                $newLine = Convert-EntryLine (
                    $sourceSnapshot.Entries[$stale.Entry]
                ) $stale.Entry $target.Entry

                if ($stale.Map -eq $target.Map) {
                    $edit = Get-EditState $edits $stale.Map
                    $edit.Replace[$stale.Entry] = $target.Entry
                }
                else {
                    $sourceEdit = Get-EditState $edits $stale.Map
                    [void]$sourceEdit.Remove.Add($stale.Entry)

                    $targetEdit = Get-EditState $edits $target.Map
                    $targetEdit.Add.Add($newLine)
                }

                [void]$handledMissing.Add($target.Map + "|" + $target.Entry)
                continue
            }

            $edit = Get-EditState $edits $stale.Map
            [void]$edit.Remove.Add($stale.Entry)
        }

        $applied = 0
        foreach ($mapPath in @($edits.Keys | Sort-Object)) {
            if (Apply-MapEdit $repoRoot $mapPath $edits[$mapPath]) {
                $applied++
            }
        }

        if ($applied -eq 0) {
            foreach ($line in $check.Lines) {
                [Console]::Out.WriteLine($line)
            }
            exit $check.ExitCode
        }

        $final = Invoke-Checker $repoRoot
        foreach ($line in $final.Lines) {
            [Console]::Out.WriteLine($line)
        }

        exit $final.ExitCode
    }
    finally {
        Pop-Location
    }
}
catch {
    if ($Detailed) {
        [Console]::Out.WriteLine(
            "internal $($_.Exception.Message)"
        )
    }
    else {
        [Console]::Out.WriteLine("internal")
    }

    exit 30
}
