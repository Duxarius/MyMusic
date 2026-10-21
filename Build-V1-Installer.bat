@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-V1-Installer.ps1"
if errorlevel 1 (
  echo.
  echo Installer build failed.
  pause
  exit /b 1
)
echo.
echo MyMusic v1 installer created successfully.
pause
