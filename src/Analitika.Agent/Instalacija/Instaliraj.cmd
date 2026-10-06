@echo off
rem Pokrece instalaciju mosta Gastroliza sa administratorskim pravima.
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0instaliraj.ps1"
echo.
pause
