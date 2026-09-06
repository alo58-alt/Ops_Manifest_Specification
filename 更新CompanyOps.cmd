@echo off
setlocal DisableDelayedExpansion
chcp 65001 >nul
title CompanyOps Update
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Update-CompanyOps.ps1"
set "updateExit=%errorlevel%"
echo.
if not "%updateExit%"=="0" echo [ERROR] Update did not complete. See the error above. Exit code: %updateExit%.
pause
exit /b %updateExit%
