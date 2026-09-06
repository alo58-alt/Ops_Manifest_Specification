@echo off
setlocal DisableDelayedExpansion
chcp 65001 >nul
title CompanyOps Package Build

where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo [ERROR] PowerShell 7 ^(pwsh.exe^) is required.
  echo Install PowerShell 7 and reopen this window.
  pause
  exit /b 1
)

pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build-CompanyOpsSetup.ps1"
if errorlevel 1 (
  echo.
  echo [ERROR] Package build failed. See the first error above.
  pause
  exit /b 1
)

echo.
echo [DONE] Package: "%~dp0output\CompanyOps-Offline-win-x64.zip"
start "" explorer.exe /select,"%~dp0output\CompanyOps-Offline-win-x64.zip"
pause
exit /b 0
