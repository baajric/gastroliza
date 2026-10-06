@echo off
rem Uklanja most Gastroliza (trazi administratorska prava).
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ukloni.ps1"
echo.
pause
