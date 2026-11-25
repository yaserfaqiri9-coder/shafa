@echo off
setlocal
rem Rebuilds damaged AlShifa database files from the current data. Nothing is deleted: the old files stay in C:\AlShifa\data.damaged-*.
rem Optional: drag an SQL export (mysqldump) onto this file to rebuild from it when the server can no longer export by itself.
net session >nul 2>&1
if %errorlevel% neq 0 (
  set "ALSHIFA_REBUILD_LAUNCHER=%~f0"
  set "ALSHIFA_REBUILD_ARG=%~1"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:ALSHIFA_REBUILD_LAUNCHER -ArgumentList ('\"' + $env:ALSHIFA_REBUILD_ARG + '\"') -Verb RunAs"
  exit /b
)
cd /d "%~dp0"
if "%~1"=="" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0database-repair\rebuild-database.ps1"
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0database-repair\rebuild-database.ps1" -SqlFile "%~1"
)
echo.
pause
