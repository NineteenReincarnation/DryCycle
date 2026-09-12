@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Universal Safe Git Reset

rem ============================================================
rem Universal Safe Git Reset
rem
rem This script synchronizes TRACKED files with origin/current-branch.
rem It deliberately does NOT run git clean and does NOT delete ignored or
rem ordinary untracked local files.
rem
rem Safety rules:
rem   - Current branch is detected automatically; nothing is hardcoded to main.
rem   - Unpushed local commits cause a hard STOP. They are never discarded.
rem   - Tracked working-tree/staged changes require explicit confirmation.
rem   - Untracked and .gitignore files are preserved.
rem   - Remote update uses ff-only, so divergence/conflicts stop safely.
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

echo ============================================================
echo Universal Safe Git Reset
echo ============================================================
echo Repository : %REPO%
echo Branch     : %BRANCH%
echo Remote     : %REMOTE_URL%
echo.
echo This resets TRACKED files only.
echo Untracked and ignored local files are preserved; git clean is never used.
echo.

rem ------------------------------------------------------------
rem 0. Existing conflict guard.
rem ------------------------------------------------------------
git diff --name-only --diff-filter=U | findstr . >nul 2>&1
if not errorlevel 1 (
    call :section "STOP: EXISTING MERGE CONFLICT"
    git diff --name-only --diff-filter=U
    echo.
    echo Resolve or abort the existing merge before using safe reset.
    goto :fail
)

rem ------------------------------------------------------------
rem 1. Fetch current remote branch. Fetch does not modify working files.
rem ------------------------------------------------------------
call :section "1. FETCH REMOTE"
git fetch origin "%BRANCH%"
if errorlevel 1 (
    echo [ERROR] Could not fetch origin/%BRANCH%.
    echo Nothing was reset.
    goto :fail
)

git show-ref --verify --quiet "refs/remotes/origin/%BRANCH%"
if errorlevel 1 (
    echo [ERROR] origin/%BRANCH% does not exist.
    echo Nothing was reset.
    goto :fail
)
echo [OK] origin/%BRANCH% fetched.
echo.

rem ------------------------------------------------------------
rem 2. Refuse to discard local-only commits.
rem ------------------------------------------------------------
call :section "2. COMMIT SAFETY CHECK"
for /f "tokens=1,2" %%A in ('git rev-list --left-right --count HEAD..."origin/%BRANCH%"') do (
    set "LOCAL_AHEAD=%%A"
    set "REMOTE_AHEAD=%%B"
)

echo Local-only commits  : !LOCAL_AHEAD!
echo Remote-only commits : !REMOTE_AHEAD!
echo.

if not "!LOCAL_AHEAD!"=="0" (
    echo [SAFE STOP] This branch contains local commits that are not on origin/%BRANCH%.
    echo Safe reset will NOT discard them.
    echo.
    echo Local-only commits:
    git log --oneline "origin/%BRANCH%..HEAD"
    echo.
    echo Upload them first, or handle them manually if you intentionally want to delete them.
    goto :done
)

echo [OK] No unpushed local commits would be lost.
echo.

rem ------------------------------------------------------------
rem 3. Report local-only files that will be preserved.
rem ------------------------------------------------------------
call :section "3. LOCAL FILES THAT RESET WILL PRESERVE"
git ls-files --others --exclude-standard > "%TEMP%\git_safe_reset_untracked.txt"
for %%F in ("%TEMP%\git_safe_reset_untracked.txt") do set "UNTRACKED_SIZE=%%~zF"
if "!UNTRACKED_SIZE!"=="0" (
    echo No ordinary untracked files detected.
) else (
    echo [PRESERVED] Ordinary untracked files exist and will NOT be deleted.
    echo Inspect them with: git status --short --untracked-files=all
)

git ls-files --others -i --exclude-standard > "%TEMP%\git_safe_reset_ignored.txt"
for %%F in ("%TEMP%\git_safe_reset_ignored.txt") do set "IGNORED_SIZE=%%~zF"
if "!IGNORED_SIZE!"=="0" (
    echo No ignored local files detected.
) else (
    echo [PRESERVED] .gitignore files/directories exist and will NOT be deleted.
    echo This includes local caches, local DLLs, Unity Library data, and other ignored resources.
    echo Inspect them with: git status --ignored --short
)
echo.

rem ------------------------------------------------------------
rem 4. Show tracked changes that reset WOULD discard.
rem ------------------------------------------------------------
call :section "4. TRACKED LOCAL CHANGES"
git status --short --untracked-files=no > "%TEMP%\git_safe_reset_tracked.txt"
for %%F in ("%TEMP%\git_safe_reset_tracked.txt") do set "TRACKED_SIZE=%%~zF"

if "!TRACKED_SIZE!"=="0" (
    echo No tracked/staged local changes.
    set "HAS_TRACKED=0"
) else (
    type "%TEMP%\git_safe_reset_tracked.txt"
    set "HAS_TRACKED=1"
    echo.
    echo These TRACKED edits are the only local working-tree changes that safe reset may discard.
    choice /C YN /N /M "Discard the tracked changes shown above? [Y/N]: "
    if errorlevel 2 goto :cancel
)
echo.

rem ------------------------------------------------------------
rem 5. Restore tracked files to local HEAD if needed.
rem    No git clean is run, so ignored/untracked files remain.
rem ------------------------------------------------------------
if "!HAS_TRACKED!"=="1" (
    call :section "5. RESTORE TRACKED FILES"
    git reset --hard HEAD
    if errorlevel 1 (
        echo [ERROR] Could not restore tracked files to local HEAD.
        goto :fail
    )
    echo [OK] Tracked local edits discarded.
    echo Untracked/ignored files were not cleaned.
    echo.
)

rem ------------------------------------------------------------
rem 6. Fast-forward to fetched remote branch.
rem    ff-only prevents accidental merge commits or history rewriting.
rem    Git also stops if an untracked file would be overwritten.
rem ------------------------------------------------------------
call :section "6. FAST-FORWARD TO REMOTE"
if "!REMOTE_AHEAD!"=="0" (
    echo Local tracked state is already at origin/%BRANCH%.
) else (
    echo Updating tracked files to origin/%BRANCH%...
    git merge --ff-only "origin/%BRANCH%"
    if errorlevel 1 (
        echo.
        echo [SAFE STOP] Fast-forward failed.
        echo No git clean was run. Local-only resources were not intentionally deleted.
        echo If Git reports an untracked file would be overwritten, move/rename that file and rerun.
        goto :fail
    )
    echo [OK] Fast-forward complete.
)
echo.

rem ------------------------------------------------------------
rem 7. Verify tracked branch equality.
rem ------------------------------------------------------------
call :section "7. VERIFY"
for /f "tokens=1,2" %%A in ('git rev-list --left-right --count HEAD..."origin/%BRANCH%"') do (
    set "VERIFY_LOCAL=%%A"
    set "VERIFY_REMOTE=%%B"
)

if "!VERIFY_LOCAL!"=="0" if "!VERIFY_REMOTE!"=="0" (
    echo [OK] Tracked branch matches origin/%BRANCH%.
) else (
    echo [WARNING] Local/remote commit state still differs.
    echo Local-only=!VERIFY_LOCAL! Remote-only=!VERIFY_REMOTE!
)

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

:cancel
echo.
echo Cancelled before tracked local changes were discarded.
goto :done

:done
del /q "%TEMP%\git_safe_reset_untracked.txt" >nul 2>&1
del /q "%TEMP%\git_safe_reset_ignored.txt" >nul 2>&1
del /q "%TEMP%\git_safe_reset_tracked.txt" >nul 2>&1
echo.
echo Press any key to close...
pause >nul
exit /b 0

:fail
del /q "%TEMP%\git_safe_reset_untracked.txt" >nul 2>&1
del /q "%TEMP%\git_safe_reset_ignored.txt" >nul 2>&1
del /q "%TEMP%\git_safe_reset_tracked.txt" >nul 2>&1
echo.
echo Operation stopped.
echo This reset never runs git clean and never intentionally discards unpushed commits.
echo.
echo Press any key to close...
pause >nul
exit /b 1
