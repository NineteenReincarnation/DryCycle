@echo off
setlocal EnableExtensions EnableDelayedExpansion
title DryCycle Full Reset and Recloning

rem ============================================================
rem DryCycle FULL RESET
rem
rem Deletes EVERYTHING INSIDE the local DryCycle folder, then
rem clones a brand-new copy of origin/main into the same folder.
rem
rem Important Windows detail:
rem Explorer / OneDrive may keep the DryCycle DIRECTORY itself open.
rem Therefore this script does NOT require deleting the outer folder.
rem It removes every child entry, including .git/hidden/ignored files,
rem verifies the folder is empty, and clones main into that empty folder.
rem ============================================================

set "REPO_URL=https://github.com/NineteenReincarnation/DryCycle.git"
set "BRANCH=main"

if /I "%~1"=="--worker" goto :worker

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%.") do set "SCRIPT_DIR=%%~fI"

if not "%~1"=="" (
    for %%I in ("%~1") do set "TARGET=%%~fI"
) else if exist "%SCRIPT_DIR%\.git" (
    set "TARGET=%SCRIPT_DIR%"
) else (
    set "TARGET=%USERPROFILE%\Desktop\DryCycle"
)

call :validate_target "%TARGET%"
if errorlevel 1 goto :fail_outer

cls
echo ============================================================
echo DryCycle FULL RESET
echo ============================================================
echo.
echo Repository : %REPO_URL%
echo Branch     : %BRANCH%
echo Target     : %TARGET%
echo.
echo WARNING:
echo   EVERYTHING INSIDE the target folder will be deleted.
echo   This includes .git, tracked changes, untracked files,
echo   ignored files, local commits, mod assets, lib files and caches.
echo   There is NO backup and NO recovery branch.
echo.

where git >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Git was not found in PATH.
    goto :fail_outer
)

echo [CHECK] Verifying remote main before deleting local files...
git ls-remote --exit-code "%REPO_URL%" "refs/heads/%BRANCH%" >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Cannot reach %REPO_URL% or branch %BRANCH% does not exist.
    echo [SAFE] Local files were NOT deleted.
    goto :fail_outer
)
echo [OK] Remote main is reachable.
echo.

choice /C YN /N /M "Delete ALL local DryCycle contents and clone main again? [Y/N]: "
if errorlevel 2 (
    echo.
    echo Cancelled. Nothing was deleted.
    goto :done_outer
)

set "TEMP_BAT=%TEMP%\DryCycleFullReset_%RANDOM%_%RANDOM%.bat"
copy /y "%~f0" "%TEMP_BAT%" >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Could not create the temporary reset launcher.
    goto :fail_outer
)

echo.
echo [INFO] Handing reset to temporary worker...
start "DryCycle Full Reset" cmd.exe /d /c ""%TEMP_BAT%" --worker "%TARGET%""
exit /b 0


:worker
set "TARGET=%~2"
if not defined TARGET goto :worker_fail
for %%I in ("%TARGET%") do set "TARGET=%%~fI"

cd /d "%TEMP%" >nul 2>&1
>nul 2>&1 timeout /t 1 /nobreak

call :validate_target "%TARGET%"
if errorlevel 1 goto :worker_fail

cls
echo ============================================================
echo DryCycle FULL RESET - WORKER
echo ============================================================
echo.
echo Target: %TARGET%
echo.

rem ------------------------------------------------------------
rem 1. Keep the outer directory, but delete every entry inside it.
rem This avoids Windows Explorer / OneDrive locking the directory
rem object itself while still producing a completely empty checkout.
rem ------------------------------------------------------------
echo [1/3] Deleting ALL contents inside DryCycle...
if not exist "%TARGET%" mkdir "%TARGET%" >nul 2>&1
if not exist "%TARGET%" (
    echo [ERROR] Could not create target directory.
    goto :worker_fail
)

set "DRYCYCLE_RESET_TARGET=%TARGET%"

rem Remove read-only/system/hidden attributes where possible first.
attrib -R -S -H "%TARGET%\*" /S /D >nul 2>&1

rem PowerShell removes normal, hidden and .git entries but leaves the
rem outer DryCycle directory intact, so Explorer may keep viewing it.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$p=$env:DRYCYCLE_RESET_TARGET; Get-ChildItem -LiteralPath $p -Force -ErrorAction Stop | Remove-Item -Recurse -Force -ErrorAction Stop" >nul 2>&1

rem CMD fallback for anything PowerShell did not remove.
del /f /s /q "%TARGET%\*" >nul 2>&1
for /d %%D in ("%TARGET%\*") do rd /s /q "%%~fD" >nul 2>&1

rem Verify there is literally nothing left inside the directory.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$p=$env:DRYCYCLE_RESET_TARGET; if((Get-ChildItem -LiteralPath $p -Force -ErrorAction Stop | Measure-Object).Count -ne 0){exit 2}" >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Some local files are still locked and could not be deleted:
    powershell -NoProfile -ExecutionPolicy Bypass -Command ^
        "$p=$env:DRYCYCLE_RESET_TARGET; Get-ChildItem -LiteralPath $p -Force | ForEach-Object { Write-Host ('  ' + $_.FullName) }"
    echo.
    echo Close Visual Studio, terminals, Git clients, or any program that
    echo is holding an actual FILE inside DryCycle, then run this BAT again.
    echo Explorer may stay open on the empty DryCycle folder.
    goto :worker_fail
)

echo [OK] DryCycle contents are completely empty.
echo [OK] The outer DryCycle folder was intentionally kept.
echo.

rem ------------------------------------------------------------
rem 2. Git supports cloning into an existing directory when it is empty.
rem ------------------------------------------------------------
echo [2/3] Cloning a fresh copy of main into the empty folder...
git clone --branch "%BRANCH%" --single-branch "%REPO_URL%" "%TARGET%"
if errorlevel 1 (
    echo.
    echo [ERROR] Fresh clone failed.
    echo [INFO] The old local contents were already removed.
    echo [INFO] The DryCycle directory itself was preserved.
    echo [INFO] Fix the network/Git problem and run this BAT again.
    goto :worker_fail
)
echo [OK] Fresh clone completed.
echo.

rem ------------------------------------------------------------
rem 3. Verify exact main identity and a clean working tree.
rem ------------------------------------------------------------
echo [3/3] Verifying fresh checkout...
set "LOCAL_BRANCH="
set "LOCAL_HEAD="
set "REMOTE_HEAD="
set "DIRTY="
for /f "delims=" %%B in ('git -C "%TARGET%" branch --show-current 2^>nul') do set "LOCAL_BRANCH=%%B"
for /f "delims=" %%L in ('git -C "%TARGET%" rev-parse HEAD 2^>nul') do set "LOCAL_HEAD=%%L"
for /f "delims=" %%R in ('git -C "%TARGET%" rev-parse origin/%BRANCH% 2^>nul') do set "REMOTE_HEAD=%%R"

if /I not "!LOCAL_BRANCH!"=="%BRANCH%" (
    echo [ERROR] Expected branch %BRANCH%, got !LOCAL_BRANCH!.
    goto :worker_fail
)
if not defined LOCAL_HEAD (
    echo [ERROR] Could not read local HEAD.
    goto :worker_fail
)
if not defined REMOTE_HEAD (
    echo [ERROR] Could not read origin/%BRANCH%.
    goto :worker_fail
)
if /I not "!LOCAL_HEAD!"=="!REMOTE_HEAD!" (
    echo [ERROR] Local HEAD does not match origin/%BRANCH%.
    echo Local  : !LOCAL_HEAD!
    echo Remote : !REMOTE_HEAD!
    goto :worker_fail
)

for /f "delims=" %%S in ('git -C "%TARGET%" status --porcelain') do set "DIRTY=1"
if defined DIRTY (
    echo [ERROR] Fresh checkout is unexpectedly dirty.
    git -C "%TARGET%" status --short
    goto :worker_fail
)

echo.
echo ============================================================
echo FULL RESET COMPLETE
echo ============================================================
echo [OK] Every old local file was removed.
echo [OK] A brand-new main checkout was cloned.
echo [OK] Working tree is clean.
echo.
echo Branch : !LOCAL_BRANCH!
echo HEAD   : !LOCAL_HEAD!
echo Path   : %TARGET%
echo.
pause

start "" /b cmd.exe /d /c "timeout /t 2 /nobreak ^>nul ^& del /f /q ""%~f0"""
exit /b 0


:validate_target
set "CHECK_TARGET=%~1"
if not defined CHECK_TARGET (
    echo [ERROR] Target directory is empty.
    exit /b 1
)
for %%I in ("%CHECK_TARGET%") do (
    set "CHECK_TARGET=%%~fI"
    set "CHECK_NAME=%%~nxI"
)

if /I "!CHECK_TARGET!"=="%SystemDrive%\" (
    echo [ERROR] Refusing to reset a drive root.
    exit /b 1
)
if /I "!CHECK_TARGET!"=="%USERPROFILE%" (
    echo [ERROR] Refusing to reset the user profile.
    exit /b 1
)
if /I "!CHECK_TARGET!"=="%USERPROFILE%\Desktop" (
    echo [ERROR] Refusing to reset the Desktop directory.
    exit /b 1
)

if /I "!CHECK_NAME!"=="DryCycle" exit /b 0

if exist "!CHECK_TARGET!\.git" (
    set "CHECK_ORIGIN="
    for /f "delims=" %%U in ('git -C "!CHECK_TARGET!" remote get-url origin 2^>nul') do set "CHECK_ORIGIN=%%U"
    if defined CHECK_ORIGIN (
        echo !CHECK_ORIGIN! | findstr /I /C:"NineteenReincarnation/DryCycle" >nul
        if not errorlevel 1 exit /b 0
    )
)

echo [ERROR] Refusing destructive reset because the target is not clearly DryCycle:
echo   !CHECK_TARGET!
exit /b 1


:done_outer
echo.
echo Press any key to close...
pause >nul
exit /b 0

:fail_outer
echo.
echo Operation stopped. Nothing was deleted.
echo.
echo Press any key to close...
pause >nul
exit /b 1

:worker_fail
echo.
echo ============================================================
echo RESET FAILED
echo ============================================================
echo Review the error above.
echo.
pause
exit /b 1
