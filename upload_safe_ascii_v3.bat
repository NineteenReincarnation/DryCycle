@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Universal Safe Git Upload

rem ============================================================
rem Universal Safe Git Upload
rem
rem Goals:
rem   - Works on the current branch. Nothing is hardcoded to main.
rem   - Commits every tracked/untracked NON-IGNORED local change.
rem   - Never uses reset --hard, git clean, checkout overwrite, or force push.
rem   - Fetches and merges remote work before pushing.
rem   - Shows the exact push payload and asks before push.
rem   - Warns when ignored local files exist: Git will not upload them.
rem
rem Ignored files are intentionally local. The matching safe reset script
rem never runs git clean, so ignored build caches/resources are preserved.
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
echo Universal Safe Git Upload
echo ============================================================
echo Repository : %REPO%
echo Branch     : %BRANCH%
echo Remote     : %REMOTE_URL%
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
rem 2. Show everything Git WILL consider for upload.
rem    Ignored files are reported separately and are never silently implied
rem    to have been uploaded.
rem ------------------------------------------------------------
call :section "2. LOCAL CHANGES"
git status --short --untracked-files=all > "%TEMP%\git_safe_upload_local.txt"
for %%F in ("%TEMP%\git_safe_upload_local.txt") do set "LOCAL_SIZE=%%~zF"

if "!LOCAL_SIZE!"=="0" (
    echo No uncommitted tracked/non-ignored changes.
    set "HAS_LOCAL=0"
) else (
    type "%TEMP%\git_safe_upload_local.txt"
    set "HAS_LOCAL=1"
)
echo.

git ls-files --others -i --exclude-standard > "%TEMP%\git_safe_upload_ignored.txt"
for %%F in ("%TEMP%\git_safe_upload_ignored.txt") do set "IGNORED_SIZE=%%~zF"
if not "!IGNORED_SIZE!"=="0" (
    echo [NOTICE] Ignored local files exist.
    echo They are NOT uploaded by this script because .gitignore explicitly marks them local.
    echo The safe reset script preserves them and never runs git clean.
    echo If one of these files must live in Git, remove/fix its ignore rule first, then rerun upload.
    echo Inspect them with: git status --ignored --short
    echo.
)

rem ------------------------------------------------------------
rem 3. Stage and commit all NON-IGNORED local work.
rem ------------------------------------------------------------
if "!HAS_LOCAL!"=="1" (
    call :section "3. COMMIT LOCAL CHANGES"
    echo git add -A will stage all tracked and non-ignored untracked changes shown above.
    echo.
    choice /C YN /N /M "Stage and commit these changes? [Y/N]: "
    if errorlevel 2 goto :cancel

    git add -A
    if errorlevel 1 (
        echo [ERROR] git add -A failed.
        goto :fail
    )

    git diff --cached --quiet
    if not errorlevel 1 (
        echo [INFO] Nothing was staged after git add -A.
    ) else (
        echo.
        echo Staged payload:
        git diff --cached --name-status
        echo.

        for /f %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd_HH-mm-ss"') do set "STAMP=%%T"
        set "MSG=sync: local changes !STAMP!"
        git commit -m "!MSG!"
        if errorlevel 1 (
            echo [ERROR] git commit failed. Staged files remain intact.
            goto :fail
        )
        echo [OK] Local changes committed.
    )
) else (
    call :section "3. COMMIT LOCAL CHANGES"
    echo Nothing new to commit.
)
echo.

rem ------------------------------------------------------------
rem 4. Merge remote work into local branch.
rem    No rebase/reset/force is used. Conflicts stop before push.
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
                echo Resolve them, git add -A, git commit, then run upload again.
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

:section
echo ------------------------------------------------------------
echo %~1
echo ------------------------------------------------------------
exit /b 0

:cancel
echo.
echo Cancelled. No destructive cleanup was performed.
goto :done

:done
del /q "%TEMP%\git_safe_upload_local.txt" >nul 2>&1
del /q "%TEMP%\git_safe_upload_ignored.txt" >nul 2>&1
echo.
echo Press any key to close...
pause >nul
exit /b 0

:fail
del /q "%TEMP%\git_safe_upload_local.txt" >nul 2>&1
del /q "%TEMP%\git_safe_upload_ignored.txt" >nul 2>&1
echo.
echo Operation stopped safely.
echo This uploader never runs git clean, reset --hard, checkout overwrite, or force push.
echo.
echo Press any key to close...
pause >nul
exit /b 1
