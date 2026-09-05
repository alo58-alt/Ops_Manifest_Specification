@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Configure-Pm2OwnerBridge.ps1"
set "CompanyOpsExitCode=%ERRORLEVEL%"
echo.
if not "%CompanyOpsExitCode%"=="0" echo PM2 主机接管配置失败，错误码 %CompanyOpsExitCode%。
pause
exit /b %CompanyOpsExitCode%
