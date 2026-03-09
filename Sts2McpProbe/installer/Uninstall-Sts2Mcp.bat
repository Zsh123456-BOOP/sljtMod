@echo off
setlocal

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-Sts2Mcp.ps1" %*
if errorlevel 1 (
    echo.
    echo [Sts2Mcp] Uninstall failed.
    pause
    exit /b 1
)

echo.
echo [Sts2Mcp] Uninstall success.
pause
