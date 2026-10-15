@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
if not exist "%SCRIPT_DIR%Start-MuMuHook.ps1" (
  echo Missing Start-MuMuHook.ps1 next to this BAT.
  echo Copy the complete my-hook-tool repository instead of this BAT alone.
  pause
  exit /b 2
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Start-MuMuHook.ps1" -ProfileId HSR-4.5-MUMU -ObserveTransfers -ReuseMumu
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" (
  echo.
  echo HSR 4.5 reuse-session failed with exit code %EXIT_CODE%.
  pause
)
exit /b %EXIT_CODE%
