@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Universal Safe Git Reset

rem ============================================================
rem Universal Safe Git Reset
rem
rem Contract:
rem   - Works on the current branch; nothing is hardcoded to main.
rem   - Fetches origin/current-branch and fast-forwards only.
rem   - NEVER runs git clean.
rem   - NEVER deletes ignored or untracked local files.
rem   - NEVER discards tracked/staged edits.
rem   - NEVER discards local-only commits.
rem   - Any local work that is not safely on the remote causes a STOP.
rem
rem Intended workflow:
rem   1) Run the safe upload script.
rem   2) Confirm upload verification succeeded.
rem   3) Run this reset/sync script.
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
    goto :fail
)

git remote get-url origin >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Remote "origin" does not exist.
    goto :fail
)
for /f "delims=" %%U in ('git remote get-url origin') do set "REMOTE_URL=%%U"

echo ============================================================
echo Universal Safe Git Reset
echo ============================================================
echo Repository : %REPO%
echo Branch     : %BRANCH%
echo Remote     : %REMOTE_URL%
echo.
echo This script updates TRACKED files only.
echo Ignored and untracked local files are preserved by design.
echo.

rem ------------------------------------------------------------
rem 1. Existing conflict guard.
rem ------------------------------------------------------------
call :section "1. SAFETY CHECK"
git diff --name-only --diff-filter=U | findstr . >nul 2>&1
if not errorlevel 1 (
    echo [SAFE STOP] Unresolved merge conflicts exist:
    git diff --name-only --diff-filter=U
    goto :done
)

rem Tracked/staged edits are never discarded automatically.
git status --short --untracked-files=no > "%TEMP%\git_safe_reset_tracked.txt"
for %%F in ("%TEMP%\git_safe_reset_tracked.txt") do set "TRACKED_SIZE=%%~zF"
if not "!TRACKED_SIZE!"=="0" (
    echo [SAFE STOP] Tracked or staged local edits exist.
    echo Nothing will be reset until these changes are uploaded, committed, or handled manually.
    echo.
    type "%TEMP%\git_safe_reset_tracked.txt"
    goto :done
)

echo [OK] No tracked/staged local edits.
echo.

rem ------------------------------------------------------------
rem 2. Fetch current remote branch. Fetch does not modify files.
rem ------------------------------------------------------------
call :section "2. FETCH REMOTE"
git fetch origin "%BRANCH%"
if errorlevel 1 (
    echo [ERROR] Could not fetch origin/%BRANCH%. Nothing was changed.
    goto :fail
)

git show-ref --verify --quiet "refs/remotes/origin/%BRANCH%"
if errorlevel 1 (
    echo [ERROR] origin/%BRANCH% does not exist. Nothing was changed.
    goto :fail
)
echo [OK] origin/%BRANCH% fetched.
echo.

rem ------------------------------------------------------------
rem 3. Refuse to discard local-only commits.
rem ------------------------------------------------------------
call :section "3. COMMIT SAFETY CHECK"
for /f "tokens=1,2" %%A in ('git rev-list --left-right --count HEAD..."origin/%BRANCH%"') do (
    set "LOCAL_AHEAD=%%A"
    set "REMOTE_AHEAD=%%B"
)

echo Local-only commits  : !LOCAL_AHEAD!
echo Remote-only commits : !REMOTE_AHEAD!
echo.

if not "!LOCAL_AHEAD!"=="0" (
    echo [SAFE STOP] Local commits exist that are not on origin/%BRANCH%.
    echo They will NOT be discarded.
    echo.
    git log --oneline "origin/%BRANCH%..HEAD"
    echo.
    echo Run the safe upload script first.
    goto :done
)

echo [OK] No unpushed local commits.
echo.

rem ------------------------------------------------------------
rem 4. Report local-only resources that are intentionally preserved.
rem ------------------------------------------------------------
call :section "4. PRESERVED LOCAL CONTENT"
git ls-files --others --exclude-standard > "%TEMP%\git_safe_reset_untracked.txt"
for %%F in ("%TEMP%\git_safe_reset_untracked.txt") do set "UNTRACKED_SIZE=%%~zF"
if "!UNTRACKED_SIZE!"=="0" (
    echo Ordinary untracked files : none
) else (
    echo Ordinary untracked files : PRESERVED
    echo Inspect with: git status --short --untracked-files=all
)

git ls-files --others -i --exclude-standard > "%TEMP%\git_safe_reset_ignored.txt"
for %%F in ("%TEMP%\git_safe_reset_ignored.txt") do set "IGNORED_SIZE=%%~zF"
if "!IGNORED_SIZE!"=="0" (
    echo Ignored local files      : none
) else (
    echo Ignored local files      : PRESERVED
    echo This includes Unity Library/Temp caches, local DLLs and other ignored resources.
    echo Inspect with: git status --ignored --short
)
echo.

rem ------------------------------------------------------------
rem 5. Fast-forward only. No reset --hard and no git clean.
rem ------------------------------------------------------------
call :section "5. SAFE SYNC"
if "!REMOTE_AHEAD!"=="0" (
    echo [OK] Local tracked branch already matches origin/%BRANCH%.
) else (
    echo Fast-forwarding tracked files to origin/%BRANCH%...
    git merge --ff-only "origin/%BRANCH%"
    if errorlevel 1 (
        echo.
        echo [SAFE STOP] Fast-forward failed.
        echo No cleanup command was run.
        echo If Git says an untracked file would be overwritten, move or rename that local file first.
        goto :fail
    )
    echo [OK] Fast-forward complete.
)
echo.

rem ------------------------------------------------------------
rem 6. Verify local and remote commits are identical.
rem ------------------------------------------------------------
call :section "6. VERIFY"
for /f "delims=" %%L in ('git rev-parse HEAD') do set "LOCAL_HEAD=%%L"
for /f "delims=" %%R in ('git rev-parse "origin/%BRANCH%"') do set "REMOTE_HEAD=%%R"

echo Local HEAD  : !LOCAL_HEAD!
echo Remote HEAD : !REMOTE_HEAD!
if /I not "!LOCAL_HEAD!"=="!REMOTE_HEAD!" (
    echo [ERROR] Verification failed. Local and remote commits differ.
    goto :fail
)

echo [OK] Tracked branch exactly matches origin/%BRANCH%.
echo.
echo Preserved by design:
echo   - ordinary untracked files
echo   - .gitignore files/directories
echo   - local build caches/resources not tracked by Git
echo.
git status -sb
goto :done

:section
echo ------------------------------------------------------------
echo %~1
echo ------------------------------------------------------------
exit /b 0

:done
del /q "%TEMP%\git_safe_reset_tracked.txt" >nul 2>&1
del /q "%TEMP%\git_safe_reset_untracked.txt" >nul 2>&1
del /q "%TEMP%\git_safe_reset_ignored.txt" >nul 2>&1
echo.
echo Press any key to close...
pause >nul
exit /b 0

:fail
del /q "%TEMP%\git_safe_reset_tracked.txt" >nul 2>&1
del /q "%TEMP%\git_safe_reset_untracked.txt" >nul 2>&1
del /q "%TEMP%\git_safe_reset_ignored.txt" >nul 2>&1
echo.
echo Operation stopped safely.
echo This reset never runs git clean and never discards local work automatically.
echo.
echo Press any key to close...
pause >nul
exit /b 1
