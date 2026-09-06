@echo off
setlocal EnableExtensions EnableDelayedExpansion
title DryCycle Safe Upload - ASCII V3

rem ============================================================
rem DryCycle Safe Upload
rem Build: SAFE_ASCII_V3_2026-09-06
rem Pure ASCII, no BOM, Windows CRLF.
rem Never uses reset --hard, clean -fd, checkout ., restore ., or force push.
rem ============================================================

cls
echo ============================================================
echo DryCycle Safe Upload
echo Build: SAFE_ASCII_V3_2026-09-06
echo ============================================================
echo.

cd /d "%~dp0"

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo [ERROR] This BAT must be placed inside the DryCycle Git repository.
    goto :fail
)

for /f "delims=" %%R in ('git rev-parse --show-toplevel') do set "REPO=%%R"
cd /d "%REPO%"

for /f "delims=" %%B in ('git symbolic-ref --quiet --short HEAD 2^>nul') do set "BRANCH=%%B"
if not defined BRANCH (
    echo [ERROR] Could not determine the current branch.
    echo Detached HEAD is not supported.
    goto :fail
)

git remote get-url origin >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Remote "origin" does not exist.
    goto :fail
)

for /f "delims=" %%U in ('git remote get-url origin') do set "REMOTE_URL=%%U"

echo Repository : %REPO%
echo Branch     : %BRANCH%
echo Remote     : %REMOTE_URL%
echo.

rem ------------------------------------------------------------
rem Existing conflict guard
rem ------------------------------------------------------------
git diff --name-only --diff-filter=U | findstr . >nul 2>&1
if not errorlevel 1 (
    echo [ERROR] Existing unresolved merge conflicts:
    git diff --name-only --diff-filter=U
    echo.
    echo Resolve them before running this uploader.
    goto :fail
)

rem ------------------------------------------------------------
rem 1. Show local changes
rem ------------------------------------------------------------
call :section "1. LOCAL UNCOMMITTED CHANGES"

git status --short > "%TEMP%\drycycle_upload_local.txt"
for %%F in ("%TEMP%\drycycle_upload_local.txt") do set "LOCAL_SIZE=%%~zF"

if "%LOCAL_SIZE%"=="0" (
    echo No uncommitted local changes.
    set "HAS_LOCAL=0"
) else (
    type "%TEMP%\drycycle_upload_local.txt"
    set "HAS_LOCAL=1"
)
echo.

rem ------------------------------------------------------------
rem 2. Fetch remote safely
rem ------------------------------------------------------------
call :section "2. FETCH REMOTE"

echo Running: git fetch origin
git fetch origin
if errorlevel 1 (
    echo [ERROR] Fetch failed.
    echo Local working files were not reset or deleted.
    goto :fail
)
echo [OK] Fetch complete.
echo.

git show-ref --verify --quiet "refs/remotes/origin/%BRANCH%"
if errorlevel 1 (
    set "REMOTE_EXISTS=0"
    echo Remote branch origin/%BRANCH% does not exist yet.
) else (
    set "REMOTE_EXISTS=1"
)
echo.

rem ------------------------------------------------------------
rem 3. Show remote-only commits
rem ------------------------------------------------------------
if "%REMOTE_EXISTS%"=="1" (
    call :section "3. REMOTE CHANGES NOT YET IN LOCAL"

    git log --oneline HEAD.."origin/%BRANCH%" > "%TEMP%\drycycle_upload_remote.txt"
    for %%F in ("%TEMP%\drycycle_upload_remote.txt") do set "REMOTE_SIZE=%%~zF"

    if "!REMOTE_SIZE!"=="0" (
        echo No remote-only commits.
    ) else (
        echo Remote-only commits:
        type "%TEMP%\drycycle_upload_remote.txt"
        echo.
        echo Files changed by remote-only commits:
        git diff --name-status HEAD.."origin/%BRANCH%"
    )
    echo.
)

rem ------------------------------------------------------------
rem 4. Commit local changes first
rem ------------------------------------------------------------
if "%HAS_LOCAL%"=="1" (
    call :section "4. COMMIT LOCAL CHANGES"

    echo These local changes will be committed:
    type "%TEMP%\drycycle_upload_local.txt"
    echo.

    choice /C YN /N /M "Commit these local changes? [Y/N]: "
    if errorlevel 2 goto :cancel

    git add -A
    if errorlevel 1 (
        echo [ERROR] git add -A failed.
        goto :fail
    )

    echo.
    echo Staged files:
    git diff --cached --name-status
    echo.

    for /f %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd_HH-mm-ss"') do set "STAMP=%%T"
    set "MSG=sync: local changes !STAMP!"

    git commit -m "!MSG!"
    if errorlevel 1 (
        echo [ERROR] git commit failed.
        goto :fail
    )

    echo [OK] Local changes committed.
    echo.
)

rem ------------------------------------------------------------
rem 5. Merge remote into local
rem ------------------------------------------------------------
if "%REMOTE_EXISTS%"=="1" (
    call :section "5. MERGE REMOTE AND LOCAL"

    for /f "tokens=1,2" %%A in ('git rev-list --left-right --count HEAD..."origin/%BRANCH%"') do (
        set "AHEAD=%%A"
        set "BEHIND=%%B"
    )

    echo Local ahead  : !AHEAD!
    echo Remote ahead : !BEHIND!
    echo.

    if not "!BEHIND!"=="0" (
        echo Merging origin/%BRANCH% into local %BRANCH%...
        echo Non-conflicting changes will merge automatically.
        echo Conflicts will STOP the script before any push.
        echo.

        git merge --no-edit "origin/%BRANCH%"
        if errorlevel 1 (
            git diff --name-only --diff-filter=U | findstr . >nul 2>&1
            if not errorlevel 1 (
                echo.
                call :section "MERGE CONFLICT"
                echo Conflict files:
                git diff --name-only --diff-filter=U
                echo.
                echo No push was performed.
                echo Both local and remote commits are still preserved.
                echo Resolve conflicts, then:
                echo   git add -A
                echo   git commit
                echo Then run this BAT again.
                goto :fail
            )

            echo [ERROR] Merge failed for a non-conflict reason.
            echo Run: git status
            goto :fail
        )

        echo [OK] Merge complete.
    ) else (
        echo [OK] Local already contains all remote commits.
    )
    echo.
)

rem ------------------------------------------------------------
rem 6. Show EXACT push payload
rem ------------------------------------------------------------
call :section "6. EXACT CONTENT TO PUSH"

if "%REMOTE_EXISTS%"=="1" (
    for /f %%N in ('git rev-list --count "origin/%BRANCH%..HEAD"') do set "PUSH_COUNT=%%N"

    if "!PUSH_COUNT!"=="0" (
        echo Nothing to push.
        echo Local and origin/%BRANCH% are synchronized.
        goto :done
    )

    echo Commits to push:
    git log --oneline "origin/%BRANCH%..HEAD"
    echo.
    echo Final file changes relative to origin/%BRANCH%:
    git diff --name-status "origin/%BRANCH%..HEAD"
) else (
    echo First push of branch %BRANCH%.
    echo.
    echo Recent commits:
    git log --oneline --decorate -n 20
)

echo.
choice /C YN /N /M "Push exactly the content shown above? [Y/N]: "
if errorlevel 2 goto :cancel

rem ------------------------------------------------------------
rem 7. Push
rem ------------------------------------------------------------
call :section "7. PUSH"

if "%REMOTE_EXISTS%"=="1" (
    git push origin "%BRANCH%"
) else (
    git push -u origin "%BRANCH%"
)

if errorlevel 1 (
    echo.
    echo [ERROR] Push failed.
    echo Local commits remain intact.
    goto :fail
)

echo.
echo ============================================================
echo UPLOAD COMPLETE
echo ============================================================
git status -sb
goto :done


:section
echo ------------------------------------------------------------
echo %~1
echo ------------------------------------------------------------
exit /b

:cancel
echo.
echo Cancelled.
echo No push was performed.
goto :done

:done
echo.
echo Press any key to close...
pause >nul
exit /b 0

:fail
echo.
echo Operation stopped safely.
echo No hard reset, clean, restore, checkout overwrite, or force push was used.
echo.
echo Press any key to close...
pause >nul
exit /b 1
