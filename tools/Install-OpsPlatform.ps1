[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$PublishRoot = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'CompanyOps'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'CompanyOps'),
    [string]$HostId = $env:COMPUTERNAME,
    [string[]]$Operators = @(),
    [switch]$Apply,
    [switch]$StartServices
)

$ErrorActionPreference = 'Stop'
$publish = [System.IO.Path]::GetFullPath($PublishRoot)
$install = [System.IO.Path]::GetFullPath($InstallRoot)
$data = [System.IO.Path]::GetFullPath($DataRoot)

if (-not $Apply) {
    Write-Host '安全预览：未传入 -Apply，不会复制文件或注册服务。'
    Write-Host "发布源：$publish"
    Write-Host "程序目录：$install"
    Write-Host "数据目录：$data"
    Write-Host '将注册 CompanyOps.Agent(LocalSystem) 与 CompanyOps.Console(NetworkService)，默认不启动。'
    return
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '安装需要在提升的 PowerShell 中执行。'
}

foreach ($component in @('Agent', 'Console', 'Pm2Bridge', 'Cli')) {
    $source = Join-Path $publish $component
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "缺少发布目录：$source"
    }
}

foreach ($serviceName in @('CompanyOps.Agent', 'CompanyOps.Console')) {
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        throw "服务 $serviceName 已存在。本脚本只负责首次安装，拒绝覆盖；升级必须走独立版本化流程。"
    }
}

if (Test-Path -LiteralPath $install) {
    throw "安装目录已存在，拒绝覆盖：$install"
}

if (-not $PSCmdlet.ShouldProcess($install, '复制 CompanyOps 发布文件并注册 Windows Services')) {
    return
}

New-Item -ItemType Directory -Path $install | Out-Null
foreach ($component in @('Agent', 'Console', 'Pm2Bridge', 'Cli')) {
    Copy-Item -LiteralPath (Join-Path $publish $component) -Destination (Join-Path $install $component) -Recurse
}

$manifestDirectory = Join-Path $data 'manifests'
$agentStateDirectory = Join-Path $data 'Agent'
$snapshotDirectory = Join-Path $agentStateDirectory 'pm2-snapshots'
New-Item -ItemType Directory -Force -Path $manifestDirectory, $agentStateDirectory, $snapshotDirectory | Out-Null

$agentSettings = @{
    Ops = @{
        HostId = $HostId
        ManifestDirectory = $manifestDirectory
        StateDirectory = $agentStateDirectory
        Pm2SnapshotDirectory = $snapshotDirectory
        PipeName = 'CompanyOps.Agent.v1'
        InventoryIntervalSeconds = 30
        EnableMutations = $false
        AllowedClientSids = @('S-1-5-20')
    }
    Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.Hosting.Lifetime' = 'Information' } }
}
$agentSettings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $install 'Agent\appsettings.json') -Encoding utf8

$consoleSettings = @{
    Urls = 'http://127.0.0.1:19310'
    Console = @{
        PipeName = 'CompanyOps.Agent.v1'
        Operators = $Operators
        Administrators = @()
        AllowLocalAdministrators = $true
    }
    Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning' } }
}
$consoleSettings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $install 'Console\appsettings.json') -Encoding utf8

$agentExe = Join-Path $install 'Agent\CompanyOps.Agent.exe'
$consoleExe = Join-Path $install 'Console\CompanyOps.Console.exe'
New-Service -Name 'CompanyOps.Agent' -BinaryPathName ('"{0}"' -f $agentExe) -DisplayName 'CompanyOps Agent' -Description 'Windows 多项目统一运维 Agent' -StartupType Automatic | Out-Null
New-Service -Name 'CompanyOps.Console' -BinaryPathName ('"{0}"' -f $consoleExe) -DisplayName 'CompanyOps Console' -Description '本机 CompanyOps 管理 Console' -StartupType Automatic | Out-Null

& sc.exe config 'CompanyOps.Console' obj= 'NT AUTHORITY\NetworkService' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw '配置 CompanyOps.Console 的 NetworkService 身份失败。服务已创建但未启动，请人工修复或删除。'
}

Write-Host '安装完成。Agent mutations 仍为 false，服务默认未启动。'
Write-Host "请先检查配置和 ACL：$install 与 $data"
if ($StartServices) {
    Start-Service -Name 'CompanyOps.Agent'
    Start-Service -Name 'CompanyOps.Console'
    Write-Host '已按显式 -StartServices 授权启动两个服务。'
}
