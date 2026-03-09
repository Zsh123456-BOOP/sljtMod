@echo off
setlocal

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Sts2Mcp.ps1" %*
if errorlevel 1 (
    echo.
    echo [Sts2Mcp] Install failed.
    pause
    exit /b 1
)

echo.
echo [Sts2Mcp] Install success.
pause
