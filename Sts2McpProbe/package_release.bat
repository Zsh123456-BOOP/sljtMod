@echo off
setlocal

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0package_release.ps1" %*
if errorlevel 1 (
    echo.
    echo [Sts2Mcp] 打包失败，请查看上面的错误信息。
    pause
    exit /b 1
)

echo.
echo [Sts2Mcp] 打包成功。
pause
