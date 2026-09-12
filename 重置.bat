@echo off
setlocal EnableExtensions EnableDelayedExpansion
title DryCycle Exact Code Reset

rem ============================================================
rem DryCycle Exact Code Reset
rem
rem Purpose:
rem   - Make tracked main exactly match origin/main.
rem   - Remove stale untracked files ONLY from source/control zones.
rem   - Clear ignored C# build outputs so old code cannot survive in obj/bin.
rem   - Preserve mod assets, lib DLLs and Unity shader caches/resources.
rem
rem Safety:
rem   - A backup is created under .git\drycycle-reset-backups before reset.
rem   - Uncommitted tracked changes are saved as binary patches and file copies.
rem   - Untracked source/control files are copied into the backup.
rem   - Local-only commits get a backup branch.
rem   - Destructive reset starts only after an explicit Y confirmation.
rem   - NEVER uses git clean -x or git clean -ffdx.
rem ============================================================

rem Run from a temporary copy. reset --hard may replace this BAT itself.
if not defined DRYCYCLE_RESET_RELAUNCHED (
    set "DRYCYCLE_RESET_RELAUNCHED=1"
    set "DRYCYCLE_RESET_ROOT=%~dp0"
    set "DRYCYCLE_RESET_TEMP=%TEMP%\DryCycleExactReset_%RANDOM%_%RANDOM%.bat"
    copy /y "%~f0" "!DRYCYCLE_RESET_TEMP!" >nul 2>&1
    if errorlevel 1 (
        echo [ERROR] Could not create temporary launcher copy.
        goto :fail_outer
    )
    call "!DRYCYCLE_RESET_TEMP!"
    set "DRYCYCLE_RESET_RC=!ERRORLEVEL!"
    del /q "!DRYCYCLE_RESET_TEMP!" >nul 2>&1
    exit /b !DRYCYCLE_RESET_RC!
)

cls
echo ============================================================
echo DryCycle Exact Code Reset
echo ============================================================
echo.

cd /d "%DRYCYCLE_RESET_ROOT%" || goto :fail

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo [ERROR] This BAT must be placed in the DryCycle repository root.
    goto :fail
)

for /f "delims=" %%R in ('git rev-parse --show-toplevel') do set "REPO=%%R"
cd /d "%REPO%" || goto :fail

for /f "delims=" %%B in ('git symbolic-ref --quiet --short HEAD 2^>nul') do set "BRANCH=%%B"
if not defined BRANCH (
    echo [ERROR] Detached HEAD detected.
    goto :fail
)
if /I not "%BRANCH%"=="main" (
    echo [STOP] Current branch is "%BRANCH%", not "main".
    echo [INFO] Exact reset is deliberately main-only.
    goto :done
)

git remote get-url origin >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Remote "origin" does not exist.
    goto :fail
)

echo Repository : %REPO%
echo Branch     : %BRANCH%
echo.

rem ------------------------------------------------------------
rem 1. Fetch the authoritative remote commit.
rem ------------------------------------------------------------
call :section "1. FETCH ORIGIN/MAIN"

git fetch origin main
if errorlevel 1 (
    echo [ERROR] Fetch failed. Nothing has been reset.
    goto :fail
)

git show-ref --verify --quiet refs/remotes/origin/main
if errorlevel 1 (
    echo [ERROR] origin/main was not found after fetch.
    goto :fail
)
echo [OK] origin/main fetched.
echo.

rem ------------------------------------------------------------
rem 2. Create a durable backup inside .git.
rem ------------------------------------------------------------
call :section "2. CREATE SAFETY BACKUP"

for /f "delims=" %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss" 2^>nul') do set "STAMP=%%T"
if not defined STAMP set "STAMP=fallback-%RANDOM%-%RANDOM%"

set "BACKUP=%REPO%\.git\drycycle-reset-backups\%STAMP%-%RANDOM%"
mkdir "%BACKUP%" >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Could not create backup directory:
    echo %BACKUP%
    goto :fail
)

git rev-parse HEAD > "%BACKUP%\local-head.txt" 2>&1
git rev-parse origin/main > "%BACKUP%\remote-head.txt" 2>&1
git status --short --untracked-files=all > "%BACKUP%\status.txt" 2>&1
git diff --binary > "%BACKUP%\working.patch" 2>&1
git diff --cached --binary > "%BACKUP%\staged.patch" 2>&1
git -c core.quotepath=false ls-files --others --exclude-standard > "%BACKUP%\untracked-all.txt"
git -c core.quotepath=false ls-files --others -i --exclude-standard > "%BACKUP%\ignored-all.txt"

git -c core.quotepath=false diff HEAD --name-only --diff-filter=ACMRTUXB > "%BACKUP%\changed-tracked.txt"
git -c core.quotepath=false ls-files --others --exclude-standard -- ^
    src scripts tools .github ^
    shader-src/Assets shader-src/Packages shader-src/ProjectSettings ^
    > "%BACKUP%\untracked-code.txt"

rem Copy current bytes of changed tracked files and untracked code files.
rem Patches remain the authoritative recovery record; these copies are an extra convenience layer.
set "DRYCYCLE_BACKUP_REPO=%REPO%"
set "DRYCYCLE_BACKUP_DIR=%BACKUP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$root=$env:DRYCYCLE_BACKUP_REPO; $base=$env:DRYCYCLE_BACKUP_DIR; $dstRoot=Join-Path $base 'files';" ^
    "$lists=@('changed-tracked.txt','untracked-code.txt');" ^
    "foreach($listName in $lists){$list=Join-Path $base $listName; if(Test-Path -LiteralPath $list){" ^
    "Get-Content -LiteralPath $list | ForEach-Object { $rel=$_; if(-not [string]::IsNullOrWhiteSpace($rel)){" ^
    "$src=Join-Path $root $rel; if(Test-Path -LiteralPath $src -PathType Leaf){" ^
    "$dst=Join-Path $dstRoot $rel; $parent=Split-Path -Parent $dst;" ^
    "if($parent){New-Item -ItemType Directory -Force -Path $parent | Out-Null};" ^
    "Copy-Item -LiteralPath $src -Destination $dst -Force }}}}}"
if errorlevel 1 (
    echo [ERROR] Could not copy local changed/source files into the backup.
    echo Backup directory is still available at:
    echo %BACKUP%
    goto :fail
)

for /f "delims=" %%C in ('git rev-list --count origin/main..HEAD') do set "LOCAL_ONLY=%%C"
if not defined LOCAL_ONLY set "LOCAL_ONLY=0"

if not "!LOCAL_ONLY!"=="0" (
    set "BACKUP_BRANCH=backup/pre-reset-%STAMP%-%RANDOM%"
    git branch "!BACKUP_BRANCH!" HEAD
    if errorlevel 1 (
        echo [ERROR] Could not create backup branch for local-only commits.
        goto :fail
    )
    > "%BACKUP%\backup-branch.txt" echo !BACKUP_BRANCH!
    echo [OK] Local-only commits protected by branch: !BACKUP_BRANCH!
) else (
    echo [OK] No local-only commits require a backup branch.
)

echo [OK] Backup directory:
echo %BACKUP%
echo.

rem ------------------------------------------------------------
rem 3. Show exactly what reset is allowed to remove.
rem ------------------------------------------------------------
call :section "3. REVIEW RESET SCOPE"

echo This WILL replace:
echo   - tracked files so they exactly match origin/main
echo.
echo This WILL remove untracked non-ignored files only inside:
echo   - src
echo   - scripts
echo   - tools
echo   - .github
echo   - shader-src/Assets
echo   - shader-src/Packages
echo   - shader-src/ProjectSettings
echo.
echo This WILL clear ignored build output only inside:
echo   - src
echo   - tools
echo   - .vs
echo   - artifacts
echo.
echo This WILL NOT clean:
echo   - mod
echo   - lib
echo   - shader-src/Library
echo   - shader-src/Temp
echo   - shader-src/Logs
echo   - shader-src/UserSettings
echo   - other untracked/ignored resource folders outside the source/control scopes
echo.
echo Untracked source/control files scheduled for removal were backed up here:
echo %BACKUP%\files
echo.

choice /C YN /N /M "Continue with exact code reset? [Y/N]: "
if errorlevel 2 (
    echo.
    echo Cancelled. Fetch and backup were completed; no reset/clean was performed.
    goto :done
)
echo.

rem ------------------------------------------------------------
rem 4. Reset tracked files to the remote commit.
rem ------------------------------------------------------------
call :section "4. RESET TRACKED FILES"

git reset --hard origin/main
if errorlevel 1 (
    echo [ERROR] git reset --hard failed.
    echo Recovery data is at:
    echo %BACKUP%
    goto :fail
)
echo [OK] Tracked files reset to origin/main.
echo.

rem ------------------------------------------------------------
rem 5. Remove stale untracked source/control files only.
rem This is the missing step that the previous conservative reset did not perform.
rem ------------------------------------------------------------
call :section "5. REMOVE STALE SOURCE"

git clean -fd -- ^
    src scripts tools .github ^
    shader-src/Assets shader-src/Packages shader-src/ProjectSettings
if errorlevel 1 (
    echo [ERROR] Source-zone cleanup failed.
    echo Recovery data is at:
    echo %BACKUP%
    goto :fail
)
echo [OK] Untracked source/control files in reset scopes were removed.
echo.

rem ------------------------------------------------------------
rem 6. Clear ignored build outputs/caches that can make VS report "latest".
rem Never point -X at mod, lib, or shader-src cache/resource folders.
rem ------------------------------------------------------------
call :section "6. CLEAR STALE BUILD OUTPUT"

git clean -fdX -- src tools .vs artifacts
if errorlevel 1 (
    echo [ERROR] Build-output cleanup failed.
    echo Recovery data is at:
    echo %BACKUP%
    goto :fail
)
echo [OK] Ignored build output under src/tools/.vs/artifacts was cleared.
echo.

rem ------------------------------------------------------------
rem 7. Verify both commit identity and source-zone cleanliness.
rem ------------------------------------------------------------
call :section "7. VERIFY EXACT CODE STATE"

for /f "delims=" %%L in ('git rev-parse HEAD') do set "LOCAL_HEAD=%%L"
for /f "delims=" %%R in ('git rev-parse origin/main') do set "REMOTE_HEAD=%%R"

echo Local HEAD  : !LOCAL_HEAD!
echo Remote HEAD : !REMOTE_HEAD!

if /I not "!LOCAL_HEAD!"=="!REMOTE_HEAD!" (
    echo [ERROR] HEAD verification failed.
    goto :verify_fail
)

git diff --quiet
if errorlevel 1 (
    echo [ERROR] Working tree still has tracked modifications.
    goto :verify_fail
)
git diff --cached --quiet
if errorlevel 1 (
    echo [ERROR] Index still has staged modifications.
    goto :verify_fail
)

git -c core.quotepath=false ls-files --others --exclude-standard -- ^
    src scripts tools .github ^
    shader-src/Assets shader-src/Packages shader-src/ProjectSettings ^
    > "%TEMP%\drycycle_reset_verify_code.txt"

for %%F in ("%TEMP%\drycycle_reset_verify_code.txt") do set "VERIFY_CODE_SIZE=%%~zF"
if not "!VERIFY_CODE_SIZE!"=="0" (
    echo [ERROR] Untracked source/control files remain:
    type "%TEMP%\drycycle_reset_verify_code.txt"
    goto :verify_fail
)

echo.
echo ============================================================
echo EXACT CODE RESET COMPLETE
echo ============================================================
echo [OK] Tracked main exactly matches origin/main.
echo [OK] Stale untracked source/control files are gone.
echo [OK] Stale C# build output is gone; the next build must rebuild.
echo [OK] Local mod/lib/Unity cache/resource areas were not cleaned.
echo [BACKUP] %BACKUP%
git log -1 --oneline
goto :done

:verify_fail
echo.
echo [ERROR] Reset verification failed.
echo Recovery data is at:
echo %BACKUP%
goto :fail

:section
echo ------------------------------------------------------------
echo %~1
echo ------------------------------------------------------------
exit /b 0

:done
del /q "%TEMP%\drycycle_reset_verify_code.txt" >nul 2>&1
echo.
echo Press any key to close...
pause >nul
exit /b 0

:fail
del /q "%TEMP%\drycycle_reset_verify_code.txt" >nul 2>&1
echo.
echo Operation stopped. Review the message above.
if defined BACKUP echo Recovery data: %BACKUP%
echo.
echo Press any key to close...
pause >nul
exit /b 1

:fail_outer
echo.
echo Operation stopped before the reset script was relaunched.
echo.
pause
exit /b 1
