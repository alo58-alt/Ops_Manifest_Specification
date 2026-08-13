@echo off
setlocal
chcp 65001 >nul
title 生成 CompanyOps 安装包

where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo [失败] 构建电脑缺少 PowerShell 7 ^(pwsh.exe^)。
  echo 请安装 PowerShell 7 后重新双击本文件。
  pause
  exit /b 1
)

pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build-CompanyOpsSetup.ps1"
if errorlevel 1 (
  echo.
  echo [失败] 没有生成安装包，请查看上方第一条红色错误。
  pause
  exit /b 1
)

echo.
echo [完成] 安装包位于：%~dp0output\CompanyOps-Offline-win-x64.zip
start "" explorer.exe /select,"%~dp0output\CompanyOps-Offline-win-x64.zip"
pause
exit /b 0
