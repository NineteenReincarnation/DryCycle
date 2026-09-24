@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Universal Safe Git Upload - ALL FILES

rem ============================================================
rem Universal Safe Git Upload - ALL FILES
rem
rem Goals:
rem   - Works on the current branch. Nothing is hardcoded to main.
rem   - Uploads ALL project/worktree files, including files ignored by .gitignore.
rem   - Enables Git for Windows long-path support for this repository.
rem   - Flattens embedded Git working trees while staging so their actual files
rem     are committed instead of only an embedded-repository gitlink.
rem   - Root repository .git metadata and nested .git metadata are NOT committed.
rem   - Never uses reset --hard, git clean, checkout overwrite, or force push.
rem   - Fetches and merges remote work before pushing.
rem   - Shows the exact push payload and asks before push.
rem ============================================================

cls
cd /d "%~dp0"

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo [ERROR] This BAT must be placed inside a Git repository.
    goto :fail
)

for /f "delims=" %%R in ('git rev-parse --show-toplevel') do set "REPO=%%R"
cd /d "%REPO%"

for /f "delims=" %%B in ('git symbolic-ref --quiet --short HEAD 2^>nul') do set "BRANCH=%%B"
if not defined BRANCH (
    echo [ERROR] Detached HEAD is not supported.
    echo Switch to a normal branch first.
    goto :fail
)

git remote get-url origin >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Remote "origin" does not exist.
    goto :fail
)
for /f "delims=" %%U in ('git remote get-url origin') do set "REMOTE_URL=%%U"

rem ------------------------------------------------------------
rem Long path support
rem ------------------------------------------------------------
git config --local core.longpaths true
if errorlevel 1 (
    echo [ERROR] Could not enable Git long-path support.
    goto :fail
)

set "NESTED_BACKUP=%TEMP%\git_all_upload_nested_%RANDOM%_%RANDOM%"
set "NESTED_MAP=%TEMP%\git_all_upload_nested_map_%RANDOM%_%RANDOM%.txt"
set "NESTED_ROOTS=%TEMP%\git_all_upload_nested_roots_%RANDOM%_%RANDOM%.txt"
set "NESTED_DETACHED=0"

echo ============================================================
echo Universal Safe Git Upload - ALL FILES
echo ============================================================
echo Repository : %REPO%
echo Branch     : %BRANCH%
echo Remote     : %REMOTE_URL%
echo Mode       : ALL FILES, including .gitignore matches
echo Long paths : enabled for this repository
echo.

rem ------------------------------------------------------------
rem 0. Existing conflict guard
rem ------------------------------------------------------------
git diff --name-only --diff-filter=U | findstr . >nul 2>&1
if not errorlevel 1 (
    call :section "STOP: EXISTING MERGE CONFLICT"
    git diff --name-only --diff-filter=U
    echo.
    echo Resolve the conflict before uploading.
    goto :fail
)

rem ------------------------------------------------------------
rem 1. Fetch only. Fetch never changes working files.
rem ------------------------------------------------------------
call :section "1. FETCH REMOTE"
git fetch origin
if errorlevel 1 (
    echo [ERROR] git fetch failed. Local files were not modified.
    goto :fail
)

git show-ref --verify --quiet "refs/remotes/origin/%BRANCH%"
if errorlevel 1 (
    set "REMOTE_EXISTS=0"
    echo [INFO] origin/%BRANCH% does not exist yet. This will be the first push.
) else (
    set "REMOTE_EXISTS=1"
    echo [OK] origin/%BRANCH% fetched.
)
echo.

rem ------------------------------------------------------------
rem 2. Show upload candidates.
rem    Normal status and ignored/untracked files are both upload candidates.
rem ------------------------------------------------------------
call :section "2. LOCAL CHANGES - ALL FILE MODE"

git status --short --untracked-files=all > "%TEMP%\git_all_upload_local.txt"
for %%F in ("%TEMP%\git_all_upload_local.txt") do set "LOCAL_SIZE=%%~zF"

git ls-files --others -i --exclude-standard > "%TEMP%\git_all_upload_ignored.txt"
for %%F in ("%TEMP%\git_all_upload_ignored.txt") do set "IGNORED_SIZE=%%~zF"

set "HAS_LOCAL=0"

if not "!LOCAL_SIZE!"=="0" (
    echo Tracked / non-ignored changes:
    type "%TEMP%\git_all_upload_local.txt"
    set "HAS_LOCAL=1"
) else (
    echo No tracked/non-ignored changes.
)

echo.

if not "!IGNORED_SIZE!"=="0" (
    echo Ignored files that WILL ALSO be uploaded in ALL FILE mode:
    type "%TEMP%\git_all_upload_ignored.txt"
    set "HAS_LOCAL=1"
) else (
    echo No ignored untracked files detected.
)
echo.

rem ------------------------------------------------------------
rem 2.5 Git identity guard
rem ------------------------------------------------------------
git config user.name >nul 2>&1
if errorlevel 1 (
    echo [NOTICE] Git user.name is not configured.
    set "GIT_DEFAULT_NAME=NineteenReincarnation"
    git config user.name "!GIT_DEFAULT_NAME!"
    echo [OK] Set local repository user.name to !GIT_DEFAULT_NAME!
)

git config user.email >nul 2>&1
if errorlevel 1 (
    echo [NOTICE] Git user.email is not configured.
    set "GIT_DEFAULT_EMAIL=floatcatd@outlook.com"
    git config user.email "!GIT_DEFAULT_EMAIL!"
    echo [OK] Set local repository user.email to !GIT_DEFAULT_EMAIL!.
)
echo.

rem ------------------------------------------------------------
rem 2.6 Large-file warning.
rem     GitHub normally rejects individual files >= 100 MiB without Git LFS.
rem     We warn only; ALL FILE mode does not silently skip them.
rem ------------------------------------------------------------
call :section "2.6 LARGE FILE CHECK"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$repo=[IO.Path]::GetFullPath($env:REPO); " ^
  "$big=Get-ChildItem -LiteralPath $repo -File -Force -Recurse -ErrorAction SilentlyContinue | " ^
  "Where-Object { $_.FullName -notmatch '[\\/]\.git([\\/]|$)' -and $_.Length -ge 100MB }; " ^
  "if($big){ Write-Host '[WARNING] Files >= 100 MiB found. GitHub may reject the push:'; " ^
  "$big | ForEach-Object { '{0,10:N1} MiB  {1}' -f ($_.Length/1MB), $_.FullName } } " ^
  "else { Write-Host '[OK] No individual worktree file >= 100 MiB found.' }"
echo.

rem ------------------------------------------------------------
rem 3. Stage and commit EVERYTHING.
rem ------------------------------------------------------------
if "!HAS_LOCAL!"=="1" (
    call :section "3. COMMIT ALL LOCAL FILES"
    echo This mode uses:
    echo   git add -f -A -- .
    echo so .gitignore exclusions are intentionally overridden.
    echo.
    echo Embedded Git working trees are temporarily detached while staging
    echo so their real files are committed instead of only a gitlink.
    echo Their .git metadata is restored locally after the commit.
    echo.

    choice /C YN /N /M "Stage and commit ALL project files? [Y/N]: "
    if errorlevel 2 goto :cancel

    call :detach_nested_git
    if errorlevel 1 (
        echo [ERROR] Could not prepare embedded Git working trees. Any moved nested .git metadata was restored.
        goto :fail
    )

    rem Remove any previously staged gitlinks for the embedded repositories
    rem that we are flattening into ordinary files.
    if exist "%NESTED_ROOTS%" (
        for /f "usebackq delims=" %%P in ("%NESTED_ROOTS%") do (
            git rm -r --cached --ignore-unmatch -- "%%P" >nul 2>&1
        )
    )

    git add -f -A -- .
    if errorlevel 1 (
        echo [ERROR] git add -f -A failed.
        goto :fail
    )

    git diff --cached --quiet
    if not errorlevel 1 (
        echo [INFO] Nothing was staged after git add -f -A.
        call :restore_nested_git
        if errorlevel 1 goto :fail
    ) else (
        echo.
        echo Staged payload:
        git diff --cached --name-status
        echo.

        for /f %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd_HH-mm-ss"') do set "STAMP=%%T"
        set "MSG=sync-all: local files !STAMP!"

        git commit -m "!MSG!"
        if errorlevel 1 (
            echo [ERROR] git commit failed. Staged files remain intact.
            goto :fail
        )

        call :restore_nested_git
        if errorlevel 1 (
            echo [ERROR] Commit succeeded, but nested .git metadata could not be fully restored.
            echo The committed project files are safe. Restore the nested repository metadata manually.
            goto :fail
        )

        echo [OK] All project files committed.
    )
) else (
    call :section "3. COMMIT ALL LOCAL FILES"
    echo Nothing new to commit, including ignored files.
)
echo.

rem ------------------------------------------------------------
rem 4. Merge remote work into local branch.
rem ------------------------------------------------------------
if "!REMOTE_EXISTS!"=="1" (
    call :section "4. MERGE REMOTE"
    for /f "tokens=1,2" %%A in ('git rev-list --left-right --count HEAD..."origin/%BRANCH%"') do (
        set "LOCAL_AHEAD=%%A"
        set "REMOTE_AHEAD=%%B"
    )

    echo Local-only commits  : !LOCAL_AHEAD!
    echo Remote-only commits : !REMOTE_AHEAD!
    echo.

    if not "!REMOTE_AHEAD!"=="0" (
        echo Remote commits will be merged before push:
        git log --oneline HEAD.."origin/%BRANCH%"
        echo.

        git merge --no-edit "origin/%BRANCH%"
        if errorlevel 1 (
            echo.
            git diff --name-only --diff-filter=U | findstr . >nul 2>&1
            if not errorlevel 1 (
                echo [CONFLICT] Merge stopped. Nothing was pushed.
                echo Conflict files:
                git diff --name-only --diff-filter=U
                echo.
                echo Resolve them, git add -f -A -- ., git commit, then run upload again.
                goto :fail
            )
            echo [ERROR] Merge failed for a non-conflict reason. Nothing was pushed.
            goto :fail
        )
        echo [OK] Remote commits merged.
    ) else (
        echo [OK] Local branch already contains all remote commits.
    )
) else (
    call :section "4. MERGE REMOTE"
    echo No remote branch exists yet; merge is not required.
)
echo.

rem ------------------------------------------------------------
rem 5. Show exact push payload.
rem ------------------------------------------------------------
call :section "5. EXACT PUSH PAYLOAD"
if "!REMOTE_EXISTS!"=="1" (
    for /f %%N in ('git rev-list --count "origin/%BRANCH%..HEAD"') do set "PUSH_COUNT=%%N"
    if "!PUSH_COUNT!"=="0" (
        echo Nothing to push. Local and origin/%BRANCH% are already synchronized.
        goto :done
    )

    echo Commits to push:
    git log --oneline "origin/%BRANCH%..HEAD"
    echo.
    echo Files changed by those commits:
    git diff --name-status "origin/%BRANCH%..HEAD"
) else (
    echo First push of branch %BRANCH%.
    echo Recent commits:
    git log --oneline --decorate -n 20
)
echo.

choice /C YN /N /M "Push exactly the content shown above? [Y/N]: "
if errorlevel 2 goto :cancel

rem ------------------------------------------------------------
rem 6. Push, then verify remote/local agreement.
rem ------------------------------------------------------------
call :section "6. PUSH"
if "!REMOTE_EXISTS!"=="1" (
    git push origin "%BRANCH%"
) else (
    git push -u origin "%BRANCH%"
)
if errorlevel 1 (
    echo [ERROR] Push failed. Local commits remain intact.
    goto :fail
)

echo [OK] Push completed.
git fetch origin "%BRANCH%" >nul 2>&1
if not errorlevel 1 (
    for /f "tokens=1,2" %%A in ('git rev-list --left-right --count HEAD..."origin/%BRANCH%"') do (
        set "VERIFY_LOCAL=%%A"
        set "VERIFY_REMOTE=%%B"
    )
    if "!VERIFY_LOCAL!"=="0" if "!VERIFY_REMOTE!"=="0" (
        echo [OK] Verified: local HEAD and origin/%BRANCH% match.
    ) else (
        echo [WARNING] Push returned success but local/remote still differ.
        echo Local-only=!VERIFY_LOCAL! Remote-only=!VERIFY_REMOTE!
    )
)

echo.
git status -sb
goto :done

rem ============================================================
rem Helper: temporarily move every nested repository's .git
rem metadata outside the outer repository.
rem
rem This makes the outer Git repository see nested working-tree files as
rem normal files. Root %REPO%\.git is never touched.
rem ============================================================
:detach_nested_git
if exist "%NESTED_MAP%" del /q "%NESTED_MAP%" >nul 2>&1
if exist "%NESTED_ROOTS%" del /q "%NESTED_ROOTS%" >nul 2>&1
if exist "%NESTED_BACKUP%" rmdir /s /q "%NESTED_BACKUP%" >nul 2>&1

set "NESTED_DETACHED=0"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; " ^
  "$repo=[IO.Path]::GetFullPath($env:REPO).TrimEnd('\'); " ^
  "$backup=$env:NESTED_BACKUP; $map=$env:NESTED_MAP; $roots=$env:NESTED_ROOTS; " ^
  "$moved=New-Object 'System.Collections.Generic.List[object]'; " ^
  "try { " ^
  "  New-Item -ItemType Directory -Force -Path $backup | Out-Null; " ^
  "  $queue=New-Object 'System.Collections.Generic.Queue[string]'; " ^
  "  Get-ChildItem -LiteralPath $repo -Directory -Force | Where-Object { $_.Name -ne '.git' } | ForEach-Object { $queue.Enqueue($_.FullName) }; " ^
  "  $n=0; " ^
  "  while($queue.Count -gt 0){ " ^
  "    $dir=$queue.Dequeue(); " ^
  "    try { $children=Get-ChildItem -LiteralPath $dir -Force -ErrorAction Stop } catch { continue }; " ^
  "    foreach($child in $children){ " ^
  "      if($child.Name -eq '.git'){ " ^
  "        $gitPath=$child.FullName; " ^
  "        $nestedRoot=[IO.Path]::GetDirectoryName($gitPath); " ^
  "        if([string]::IsNullOrWhiteSpace($nestedRoot)){ throw ('Cannot determine parent for '+$gitPath) }; " ^
  "        $n++; $dest=Join-Path $backup ([string]$n); " ^
  "        Move-Item -LiteralPath $gitPath -Destination $dest -Force; " ^
  "        $moved.Add([pscustomobject]@{Original=$gitPath;Backup=$dest}) | Out-Null; " ^
  "        Add-Content -LiteralPath $map -Value ($gitPath+'|'+$dest); " ^
  "        $root=$nestedRoot.Substring($repo.Length).TrimStart('\').Replace('\','/'); " ^
  "        Add-Content -LiteralPath $roots -Value $root; " ^
  "        continue " ^
  "      }; " ^
  "      if($child.PSIsContainer){ $queue.Enqueue($child.FullName) } " ^
  "    } " ^
  "  }; " ^
  "  Write-Host ('[INFO] Embedded Git metadata detached: '+$n) " ^
  "} catch { " ^
  "  Write-Host ('[ERROR] Nested Git preparation failed: '+$_.Exception.Message); " ^
  "  for($i=$moved.Count-1; $i -ge 0; $i--){ " ^
  "    $item=$moved[$i]; " ^
  "    if(Test-Path -LiteralPath $item.Backup){ Move-Item -LiteralPath $item.Backup -Destination $item.Original -Force -ErrorAction SilentlyContinue } " ^
  "  }; " ^
  "  exit 1 " ^
  "}"
if errorlevel 1 exit /b 1

set "NESTED_DETACHED=1"
exit /b 0

rem ============================================================
rem Helper: restore temporarily moved nested .git metadata.
rem ============================================================
:restore_nested_git
if not "!NESTED_DETACHED!"=="1" exit /b 0

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; " ^
  "$map=$env:NESTED_MAP; " ^
  "if(Test-Path -LiteralPath $map){ " ^
  "  Get-Content -LiteralPath $map | ForEach-Object { " ^
  "    $p=$_.Split('|',2); " ^
  "    if($p.Count -eq 2 -and (Test-Path -LiteralPath $p[1])){ " ^
  "      Move-Item -LiteralPath $p[1] -Destination $p[0] -Force " ^
  "    } " ^
  "  } " ^
  "}"
if errorlevel 1 (
    echo [ERROR] Failed to restore one or more nested .git entries.
    exit /b 1
)

set "NESTED_DETACHED=0"
if exist "%NESTED_BACKUP%" rmdir /s /q "%NESTED_BACKUP%" >nul 2>&1
exit /b 0

:section
echo ------------------------------------------------------------
echo %~1
echo ------------------------------------------------------------
exit /b 0

:cancel
echo.
call :restore_nested_git >nul 2>&1
echo Cancelled. No destructive cleanup was performed.
goto :done

:done
call :restore_nested_git >nul 2>&1
del /q "%TEMP%\git_all_upload_local.txt" >nul 2>&1
del /q "%TEMP%\git_all_upload_ignored.txt" >nul 2>&1
if exist "%NESTED_MAP%" del /q "%NESTED_MAP%" >nul 2>&1
if exist "%NESTED_ROOTS%" del /q "%NESTED_ROOTS%" >nul 2>&1
if exist "%NESTED_BACKUP%" rmdir /s /q "%NESTED_BACKUP%" >nul 2>&1
echo.
echo Press any key to close...
pause >nul
exit /b 0

:fail
echo.
call :restore_nested_git >nul 2>&1
del /q "%TEMP%\git_all_upload_local.txt" >nul 2>&1
del /q "%TEMP%\git_all_upload_ignored.txt" >nul 2>&1
if exist "%NESTED_MAP%" del /q "%NESTED_MAP%" >nul 2>&1
if exist "%NESTED_ROOTS%" del /q "%NESTED_ROOTS%" >nul 2>&1
if exist "%NESTED_BACKUP%" rmdir /s /q "%NESTED_BACKUP%" >nul 2>&1
echo Operation stopped safely.
echo This uploader never runs git clean, reset --hard, checkout overwrite, or force push.
echo.
echo Press any key to close...
pause >nul
exit /b 1
