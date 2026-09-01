@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

echo.
echo ===== 下载最新 origin/main =====
echo 当前目录: %CD%
echo.

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo [错误] 当前目录不是 Git 仓库。
    goto :fail
)

for /f "delims=" %%B in ('git branch --show-current') do set "BRANCH=%%B"
if /I not "%BRANCH%"=="main" (
    echo [错误] 当前分支是 "%BRANCH%"，不是 main。
    echo 请先切回 main，或者使用“重置.bat”直接恢复到最新 main。
    goto :fail
)

git fetch origin main
if errorlevel 1 goto :fail

git merge --ff-only origin/main
if errorlevel 1 (
    echo.
    echo [错误] 无法安全快进到最新 main。
    echo 本地修改可能与远程文件冲突；如果你确定不要本地修改，请使用“重置.bat”。
    goto :fail
)

echo.
echo ===== 下载完成 =====
git status -sb
echo.
pause
exit /b 0

:fail
echo.
echo ===== 下载失败，未强制覆盖本地文件 =====
echo.
pause
exit /b 1
