@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
if not exist "%SCRIPT_DIR%Show-HookStatus.ps1" (
  echo Missing Show-HookStatus.ps1 next to this BAT.
  pause
  exit /b 2
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Show-HookStatus.ps1"
pause
exit /b %ERRORLEVEL%
