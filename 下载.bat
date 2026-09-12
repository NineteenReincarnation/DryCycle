@echo off
setlocal EnableExtensions EnableDelayedExpansion
title DryCycle Safe Download

rem ============================================================
rem DryCycle Safe Download
rem
rem Purpose:
rem   - Update local main by fast-forward only.
rem   - Keep local resources outside source/control zones.
rem   - Detect stale untracked source files instead of compiling them silently.
rem   - Clear ignored C# build output so Visual Studio cannot reuse stale obj/bin state.
rem
rem This script NEVER uses reset --hard and NEVER cleans mod/lib/shader caches.
rem ============================================================

rem Run from a temporary copy so Git may safely update this BAT itself.
if not defined DRYCYCLE_DOWNLOAD_RELAUNCHED (
    set "DRYCYCLE_DOWNLOAD_RELAUNCHED=1"
    set "DRYCYCLE_DOWNLOAD_ROOT=%~dp0"
    set "DRYCYCLE_DOWNLOAD_TEMP=%TEMP%\DryCycleSafeDownload_%RANDOM%_%RANDOM%.bat"
    copy /y "%~f0" "!DRYCYCLE_DOWNLOAD_TEMP!" >nul 2>&1
    if errorlevel 1 (
        echo [ERROR] Could not create temporary launcher copy.
        goto :fail
    )
    call "!DRYCYCLE_DOWNLOAD_TEMP!"
    set "DRYCYCLE_DOWNLOAD_RC=!ERRORLEVEL!"
    del /q "!DRYCYCLE_DOWNLOAD_TEMP!" >nul 2>&1
    exit /b !DRYCYCLE_DOWNLOAD_RC!
)

cls
echo ============================================================
echo DryCycle Safe Download
echo ============================================================
echo.

cd /d "%DRYCYCLE_DOWNLOAD_ROOT%" || goto :fail

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
    echo [INFO] Nothing was changed.
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
rem 1. Tracked/staged local work must be uploaded first.
rem Untracked resources outside code zones are intentionally allowed.
rem ------------------------------------------------------------
call :section "1. CHECK TRACKED LOCAL WORK"

git status --short --untracked-files=no > "%TEMP%\drycycle_download_tracked.txt"
for %%F in ("%TEMP%\drycycle_download_tracked.txt") do set "TRACKED_SIZE=%%~zF"
if not "!TRACKED_SIZE!"=="0" (
    echo [SAFE STOP] Tracked or staged local edits exist.
    echo Commit/upload them first. No files were overwritten.
    echo.
    type "%TEMP%\drycycle_download_tracked.txt"
    goto :done
)
echo [OK] No tracked/staged local edits.
echo.

rem ------------------------------------------------------------
rem 2. Fetch remote main. Fetch does not modify working files.
rem ------------------------------------------------------------
call :section "2. FETCH ORIGIN/MAIN"

git fetch origin main
if errorlevel 1 (
    echo [ERROR] Fetch failed. Local working files were not changed.
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
rem 3. Do not merge local-only/diverged commits.
rem ------------------------------------------------------------
call :section "3. CHECK COMMIT HISTORY"

for /f "tokens=1,2" %%A in ('git rev-list --left-right --count HEAD...origin/main') do (
    set "LOCAL_AHEAD=%%A"
    set "REMOTE_AHEAD=%%B"
)

echo Local-only commits  : !LOCAL_AHEAD!
echo Remote-only commits : !REMOTE_AHEAD!
echo.

if not "!LOCAL_AHEAD!"=="0" (
    echo [SAFE STOP] Local main contains commits not present on origin/main.
    echo Upload/push them first. This BAT will not rewrite history.
    echo.
    git log --oneline origin/main..HEAD
    goto :done
)

rem ------------------------------------------------------------
rem 4. Stale untracked source/control files are dangerous with SDK-style csproj
rem because *.cs is compiled automatically. Stop and require exact reset.
rem Resource files outside these zones are preserved and do not block download.
rem ------------------------------------------------------------
call :section "4. CHECK STALE LOCAL SOURCE"

set "LOCAL_SOURCE="
git -c core.quotepath=false ls-files --others --exclude-standard -- ^
    src scripts tools .github ^
    shader-src/Assets shader-src/Packages shader-src/ProjectSettings ^
    > "%TEMP%\drycycle_download_untracked_code.txt"

for %%F in ("%TEMP%\drycycle_download_untracked_code.txt") do set "SOURCE_SIZE=%%~zF"
if not "!SOURCE_SIZE!"=="0" (
    echo [SAFE STOP] Untracked source/control files exist in code zones:
    echo.
    type "%TEMP%\drycycle_download_untracked_code.txt"
    echo.
    echo These may be new local work or stale files deleted from GitHub.
    echo Commit/upload them if they are wanted.
    echo Otherwise run the exact reset BAT to back them up and remove them.
    goto :done
)
echo [OK] No stale untracked source/control files.
echo.

rem ------------------------------------------------------------
rem 5. Main may only move by fast-forward.
rem ------------------------------------------------------------
call :section "5. FAST-FORWARD MAIN"

if "!REMOTE_AHEAD!"=="0" (
    echo [OK] Local main already has the current remote commit.
) else (
    git merge --ff-only origin/main
    if errorlevel 1 (
        echo.
        echo [SAFE STOP] Fast-forward failed.
        echo If Git reports an untracked file would be overwritten, move that resource first.
        goto :fail
    )
    echo [OK] Fast-forward complete.
)
echo.

rem ------------------------------------------------------------
rem 6. Clear ignored build products only.
rem This is intentionally scoped. No mod, lib, or Unity shader cache cleanup occurs.
rem ------------------------------------------------------------
call :section "6. CLEAR STALE BUILD OUTPUT"

git clean -fdX -- src tools .vs artifacts
if errorlevel 1 (
    echo [ERROR] Build-output cleanup failed.
    goto :fail
)
echo [OK] Ignored build output under src/tools/.vs/artifacts was cleared.
echo [OK] mod, lib, shader-src/Library, Temp, Logs and UserSettings were untouched.
echo.

rem ------------------------------------------------------------
rem 7. Verify exact commit and source-zone cleanliness.
rem ------------------------------------------------------------
call :section "7. VERIFY"

for /f "delims=" %%L in ('git rev-parse HEAD') do set "LOCAL_HEAD=%%L"
for /f "delims=" %%R in ('git rev-parse origin/main') do set "REMOTE_HEAD=%%R"

echo Local HEAD  : !LOCAL_HEAD!
echo Remote HEAD : !REMOTE_HEAD!

if /I not "!LOCAL_HEAD!"=="!REMOTE_HEAD!" (
    echo [ERROR] Verification failed: local HEAD differs from origin/main.
    goto :fail
)

git -c core.quotepath=false ls-files --others --exclude-standard -- ^
    src scripts tools .github ^
    shader-src/Assets shader-src/Packages shader-src/ProjectSettings ^
    > "%TEMP%\drycycle_download_verify_code.txt"

for %%F in ("%TEMP%\drycycle_download_verify_code.txt") do set "VERIFY_CODE_SIZE=%%~zF"
if not "!VERIFY_CODE_SIZE!"=="0" (
    echo [ERROR] Untracked source/control files remain after update:
    type "%TEMP%\drycycle_download_verify_code.txt"
    goto :fail
)

echo.
echo ============================================================
echo DOWNLOAD COMPLETE
echo ============================================================
echo [OK] main exactly matches origin/main.
echo [OK] Source/control zones contain no untracked stale files.
echo [OK] C# build output was cleared for a fresh next build.
echo [OK] Local resource/cache folders outside the cleanup scopes were preserved.
git log -1 --oneline
goto :done

:section
echo ------------------------------------------------------------
echo %~1
echo ------------------------------------------------------------
exit /b 0

:cleanup_temp
del /q "%TEMP%\drycycle_download_tracked.txt" >nul 2>&1
del /q "%TEMP%\drycycle_download_untracked_code.txt" >nul 2>&1
del /q "%TEMP%\drycycle_download_verify_code.txt" >nul 2>&1
exit /b 0

:done
call :cleanup_temp
echo.
echo Press any key to close...
pause >nul
exit /b 0

:fail
call :cleanup_temp
echo.
echo Operation stopped.
echo Review the message above. No hard reset was used by this download script.
echo.
echo Press any key to close...
pause >nul
exit /b 1
