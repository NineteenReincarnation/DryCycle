@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

echo.
echo ===== 完全重置当前文件夹为最新 origin/main =====
echo 当前目录: %CD%
echo.
echo [严重警告]
echo 此操作会把当前仓库工作区彻底恢复成远程 main：
echo   - 删除所有已跟踪文件的本地修改；
echo   - 删除所有未跟踪文件和文件夹；
echo   - 删除所有被 .gitignore 忽略的文件和文件夹；
echo   - 丢弃尚未推送的本地提交；
echo   - 最终工作区内容以最新 origin/main 为准。
echo.
echo 只有 .git 仓库元数据会保留，以便继续使用 Git。
echo.

choice /C YN /N /M "确定彻底重置？[Y/N]: "
if errorlevel 2 (
    echo 已取消重置。
    pause
    exit /b 0
)

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo [错误] 当前目录不是 Git 仓库。
    goto :fail
)

echo.
echo ===== 获取最新远程 main =====
git fetch origin main
if errorlevel 1 (
    echo [错误] 无法获取 origin/main。为避免清空后没有目标版本，本次停止。
    goto :fail
)

echo.
echo ===== 强制恢复到 origin/main =====
rem 先切换/重建本地 main，使其准确指向远程 main。
git checkout -B main origin/main
if errorlevel 1 (
    rem 本地修改可能阻止 checkout，先丢弃已跟踪修改后再试一次。
    git reset --hard
    if errorlevel 1 goto :fail
    git checkout -B main origin/main
    if errorlevel 1 goto :fail
)

git reset --hard origin/main
if errorlevel 1 goto :fail

echo.
echo ===== 删除全部本地额外内容 =====
rem -x: 连 .gitignore 忽略内容也删除
rem -f -f: 连嵌套 Git 工作树等普通 clean 会保护的未跟踪目录也清理
git clean -ffdx
if errorlevel 1 goto :fail

rem 再次恢复一次，确保最终所有受 Git 管理的文件完全等于远程 main。
git reset --hard origin/main
if errorlevel 1 goto :fail

echo.
echo ===== 重置完成 =====
echo 当前文件夹工作区现在就是最新 origin/main。
git status -sb
git rev-parse HEAD
echo.
pause
exit /b 0

:fail
echo.
echo ===== 重置失败 =====
echo 请查看上方 Git 错误信息。
echo.
pause
exit /b 1
