@echo off
setlocal
rem Rebuilds the AlShifa database from the verified pre-update backup and installs the new version, in one step.
net session >nul 2>&1
if %errorlevel% neq 0 (
  set "ALSHIFA_RECOVER_LAUNCHER=%~f0"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:ALSHIFA_RECOVER_LAUNCHER -Verb RunAs"
  exit /b
)
cd /d "%~dp0"
set "LOG=C:\AlShifa\logs\recover-and-update-%RANDOM%.log"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& '%~dp0recover-and-update.ps1' *>&1 | Tee-Object -FilePath '%LOG%'"
echo.
echo Log: %LOG%
pause
