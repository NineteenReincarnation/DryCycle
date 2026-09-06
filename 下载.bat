@echo off
setlocal EnableExtensions EnableDelayedExpansion
title DryCycle Safe Download

rem ============================================================
rem DryCycle Safe Download
rem Pure ASCII / Windows CMD safe.
rem This script NEVER uses reset --hard, clean -fd, restore ., checkout ., or force.
rem It will not delete untracked files or discard local commits.
rem ============================================================

cls
echo ============================================================
echo DryCycle Safe Download
echo ============================================================
echo.

cd /d "%~dp0"

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    if exist "C:\Users\Float\Desktop\DryCycle\.git" (
        cd /d "C:\Users\Float\Desktop\DryCycle"
    ) else (
        echo [ERROR] This BAT must be placed inside the DryCycle repository.
        goto :fail
    )
)

for /f "delims=" %%R in ('git rev-parse --show-toplevel') do set "REPO=%%R"
cd /d "%REPO%"

for /f "delims=" %%B in ('git symbolic-ref --quiet --short HEAD 2^>nul') do set "BRANCH=%%B"
if not defined BRANCH (
    echo [ERROR] Detached HEAD detected.
    echo Switch to a normal branch before downloading.
    goto :fail
)

if /I not "%BRANCH%"=="main" (
    echo [ERROR] Current branch is "%BRANCH%", not "main".
    echo Switch to main first. No files were changed.
    goto :fail
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
rem 1. Refuse to touch a dirty working tree.
rem Includes modified, staged, deleted, and untracked files.
rem ------------------------------------------------------------
echo ------------------------------------------------------------
echo 1. CHECK LOCAL WORK
echo ------------------------------------------------------------

git status --porcelain > "%TEMP%\drycycle_download_status.txt"
for %%F in ("%TEMP%\drycycle_download_status.txt") do set "STATUS_SIZE=%%~zF"

if not "%STATUS_SIZE%"=="0" (
    echo Local work exists. Download is stopped before any merge.
    echo.
    type "%TEMP%\drycycle_download_status.txt"
    echo.
    echo [SAFE STOP] Your local files were not reset, deleted, restored, or overwritten.
    echo Commit/upload your work first, or handle it manually, then run this BAT again.
    goto :done
)

echo [OK] Working tree is clean.
echo.

rem ------------------------------------------------------------
rem 2. Fetch only. Fetch does not modify working files.
rem ------------------------------------------------------------
echo ------------------------------------------------------------
echo 2. FETCH ORIGIN/MAIN
echo ------------------------------------------------------------

git fetch origin main
if errorlevel 1 (
    echo [ERROR] Fetch failed. Local files were not changed.
    goto :fail
)

git show-ref --verify --quiet refs/remotes/origin/main
if errorlevel 1 (
    echo [ERROR] origin/main was not found after fetch.
    goto :fail
)

echo [OK] Remote information updated.
echo.

rem ------------------------------------------------------------
rem 3. Show divergence before merging.
rem ------------------------------------------------------------
echo ------------------------------------------------------------
echo 3. COMPARE LOCAL AND REMOTE
echo ------------------------------------------------------------

for /f "tokens=1,2" %%A in ('git rev-list --left-right --count HEAD...origin/main') do (
    set "LOCAL_AHEAD=%%A"
    set "REMOTE_AHEAD=%%B"
)

echo Local commits not on remote : !LOCAL_AHEAD!
echo Remote commits not in local : !REMOTE_AHEAD!
echo.

if "!REMOTE_AHEAD!"=="0" (
    echo [OK] No remote commits need to be downloaded.
    goto :done
)

echo Remote commits to merge:
git log --oneline HEAD..origin/main
echo.
echo Files changed by those remote commits:
git diff --name-status HEAD..origin/main
echo.

rem ------------------------------------------------------------
rem 4. Merge. This preserves local commits and remote commits.
rem No hard reset is used.
rem ------------------------------------------------------------
echo ------------------------------------------------------------
echo 4. MERGE ORIGIN/MAIN INTO LOCAL MAIN
echo ------------------------------------------------------------
echo Non-conflicting changes will merge automatically.
echo If the same lines conflict, Git will stop and preserve both sides.
echo.

choice /C YN /N /M "Merge remote changes into local main? [Y/N]: "
if errorlevel 2 (
    echo.
    echo Cancelled. Fetch was completed, but no merge was performed.
    goto :done
)

git merge --no-edit origin/main
if errorlevel 1 (
    echo.
    git diff --name-only --diff-filter=U | findstr . >nul 2>&1
    if not errorlevel 1 (
        echo [CONFLICT] Merge stopped because both sides changed the same content.
        echo.
        echo Conflict files:
        git diff --name-only --diff-filter=U
        echo.
        echo No local commit or remote commit was discarded.
        echo Resolve the conflicts manually, then run:
        echo   git add -A
        echo   git commit
        goto :done
    )

    echo [ERROR] Merge failed for a non-conflict reason.
    echo Run: git status
    goto :fail
)

echo.
echo ============================================================
echo DOWNLOAD / MERGE COMPLETE
echo ============================================================
git status -sb
git log -1 --oneline
goto :done

:done
echo.
echo Press any key to close...
pause >nul
exit /b 0

:fail
echo.
echo Operation stopped safely.
echo No hard reset, clean, restore, checkout overwrite, or force operation was used.
echo.
echo Press any key to close...
pause >nul
exit /b 1
