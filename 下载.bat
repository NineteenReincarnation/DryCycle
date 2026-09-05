@echo off
chcp 65001 >nul
setlocal

cd /d "C:\Users\Float\Desktop\DryCycle"

echo.
echo ===== 下载最新 origin/main =====
echo 当前目录: %CD%
echo.

git fetch origin main
if errorlevel 1 (
    echo.
    echo [错误] 无法下载 origin/main。
    pause
    exit /b 1
)

git diff --quiet
if errorlevel 1 (
    echo.
    echo [错误] 检测到本地已修改文件。
    echo 如需放弃本地修改，请使用“重置.bat”。
    pause
    exit /b 1
)

git diff --cached --quiet
if errorlevel 1 (
    echo.
    echo [错误] 检测到本地已暂存修改。
    echo 如需放弃本地修改，请使用“重置.bat”。
    pause
    exit /b 1
)

git reset --hard origin/main
if errorlevel 1 (
    echo.
    echo [错误] 无法同步到 origin/main。
    pause
    exit /b 1
)

echo.
echo ===== 下载完成 =====
git log -1 --oneline
echo.
pause