@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Start-HSR-4.4-MuMuHook.ps1"
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" (
  echo.
  echo Hook session failed with exit code %EXIT_CODE%.
  pause
)
exit /b %EXIT_CODE%
