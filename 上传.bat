@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

echo.
echo ===== 上传当前修改到 origin/main =====
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
    echo 为避免把内容推到错误分支，上传已停止。
    goto :fail
)

echo ===== 先同步远程 main =====
git pull --rebase --autostash origin main
if errorlevel 1 (
    echo.
    echo [错误] 同步远程 main 失败，请先解决 Git 冲突。
    goto :fail
)

git add -A

echo.
echo ===== 将要提交/上传的状态 =====
git status --short
echo.

choice /C YN /N /M "确认继续上传到 main？[Y/N]: "
if errorlevel 2 (
    echo 已取消上传。
    pause
    exit /b 0
)

git diff --cached --quiet
if errorlevel 1 (
    set "COMMIT_MSG="
    set /p "COMMIT_MSG=请输入提交说明（直接回车使用 Update DryCycle）: "
    if not defined COMMIT_MSG set "COMMIT_MSG=Update DryCycle"
    git commit -m "%COMMIT_MSG%"
    if errorlevel 1 goto :fail
) else (
    echo [信息] 没有新的工作区修改需要提交；将尝试推送已有本地提交。
)

git push origin main
if errorlevel 1 (
    echo.
    echo [错误] 推送失败。远程可能刚刚发生变化；重新运行本脚本即可再次同步后推送。
    goto :fail
)

echo.
echo ===== 上传完成 =====
git status -sb
echo.
pause
exit /b 0

:fail
echo.
echo ===== 上传失败 =====
echo Git 没有执行强制重置；请查看上方错误信息。
echo.
pause
exit /b 1
