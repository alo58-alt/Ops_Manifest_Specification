@echo off
setlocal
chcp 65001 >nul
title 更新 CompanyOps
where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo [失败] 服务器缺少 PowerShell 7。请先完成源码构建环境准备。
  pause
  exit /b 1
)
pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Update-CompanyOps.ps1"
set "updateExit=%errorlevel%"
echo.
if not "%updateExit%"=="0" echo [未完成] 请查看上面的具体原因。不要手工覆盖程序目录。
pause
exit /b %updateExit%
