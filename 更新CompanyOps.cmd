@echo off
setlocal DisableDelayedExpansion
chcp 65001 >nul
title CompanyOps Update
where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo [ERROR] PowerShell 7 is required. Install it and reopen this window.
  pause
  exit /b 1
)
pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Update-CompanyOps.ps1"
set "updateExit=%errorlevel%"
echo.
if not "%updateExit%"=="0" echo [ERROR] Update did not complete. See the error above. Exit code: %updateExit%.
pause
exit /b %updateExit%
