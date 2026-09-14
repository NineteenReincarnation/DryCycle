@echo off
setlocal EnableExtensions EnableDelayedExpansion
title DryCycle Full Reset and Recloning

rem ============================================================
rem DryCycle FULL RESET
rem
rem This script intentionally destroys the entire local DryCycle
rem directory and clones a brand-new copy of branch main.
rem
rem NOTHING inside the target directory is preserved:
rem   - tracked changes
rem   - untracked files
rem   - ignored files
rem   - local commits / branches
rem   - mod assets
rem   - lib files
rem   - build outputs and caches
rem   - the local .git directory
rem
rem The script copies itself to %%TEMP%% before deletion so it can
rem safely remove the directory that originally contained this BAT.
rem ============================================================

set "REPO_URL=https://github.com/NineteenReincarnation/DryCycle.git"
set "BRANCH=main"

if /I "%~1"=="--worker" goto :worker

rem ------------------------------------------------------------
rem Resolve target directory.
rem If this BAT is inside a Git checkout, reset that directory.
rem Otherwise default to %%USERPROFILE%%\Desktop\DryCycle.
rem An explicit first argument may override the target directory.
rem ------------------------------------------------------------
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
echo   EVERYTHING inside the target directory will be deleted.
echo   There is NO backup and NO recovery branch.
echo.

rem ------------------------------------------------------------
rem Preflight before destroying anything.
rem ------------------------------------------------------------
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

choice /C YN /N /M "Delete the ENTIRE local DryCycle directory and clone main again? [Y/N]: "
if errorlevel 2 (
    echo.
    echo Cancelled. Nothing was deleted.
    goto :done_outer
)

rem ------------------------------------------------------------
rem Relaunch from TEMP. The original cmd process must exit before
rem the worker removes the repository directory.
rem ------------------------------------------------------------
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

rem The worker must not keep its current directory inside TARGET.
cd /d "%TEMP%" >nul 2>&1

rem Give the original cmd.exe a moment to release its working dir
rem and the original BAT file before removing the repository.
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
rem 1. Delete the entire local repository directory.
rem ------------------------------------------------------------
echo [1/3] Deleting ALL local files...
if exist "%TARGET%" (
    attrib -R -S -H "%TARGET%\*" /S /D >nul 2>&1
    rd /s /q "%TARGET%" >nul 2>&1
)

rem PowerShell fallback handles stubborn read-only/hidden entries.
if exist "%TARGET%" (
    set "DRYCYCLE_RESET_TARGET=%TARGET%"
    powershell -NoProfile -ExecutionPolicy Bypass -Command ^
        "Remove-Item -LiteralPath $env:DRYCYCLE_RESET_TARGET -Recurse -Force -ErrorAction Stop" >nul 2>&1
)

if exist "%TARGET%" (
    echo [ERROR] Could not completely delete the target directory.
    echo Close Visual Studio, terminals, Explorer windows, or other programs
    echo that may be holding files under:
    echo   %TARGET%
    goto :worker_fail
)
echo [OK] Local directory deleted completely.
echo.

rem ------------------------------------------------------------
rem 2. Clone a completely fresh main checkout.
rem ------------------------------------------------------------
echo [2/3] Cloning a fresh copy of main...
git clone --branch "%BRANCH%" --single-branch "%REPO_URL%" "%TARGET%"
if errorlevel 1 (
    echo.
    echo [ERROR] Fresh clone failed.
    echo [INFO] The old local repository was already deleted.
    echo [INFO] Fix the network/Git problem and run this BAT again.
    if exist "%TARGET%" rd /s /q "%TARGET%" >nul 2>&1
    goto :worker_fail
)
echo [OK] Fresh clone completed.
echo.

rem ------------------------------------------------------------
rem 3. Verify branch, commit identity, and clean working tree.
rem ------------------------------------------------------------
echo [3/3] Verifying fresh checkout...
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

rem Delete this temporary worker after cmd.exe exits.
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

rem Hard stops against catastrophic path mistakes.
if /I "!CHECK_TARGET!"=="%SystemDrive%\" (
    echo [ERROR] Refusing to delete a drive root.
    exit /b 1
)
if /I "!CHECK_TARGET!"=="%USERPROFILE%" (
    echo [ERROR] Refusing to delete the user profile.
    exit /b 1
)
if /I "!CHECK_TARGET!"=="%USERPROFILE%\Desktop" (
    echo [ERROR] Refusing to delete the Desktop directory.
    exit /b 1
)

rem A normal standalone reset targets a folder literally named DryCycle.
rem If the checkout has another folder name, only accept it when its
rem origin remote proves that it is this DryCycle repository.
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
