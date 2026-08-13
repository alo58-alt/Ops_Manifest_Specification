[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$PublishRoot,
    [Parameter(Mandatory)]
    [string]$InstallRoot,
    [Parameter(Mandatory)]
    [string]$DataRoot,
    [string]$HostId = $env:COMPUTERNAME,
    [string]$SessionOwner = [Security.Principal.WindowsIdentity]::GetCurrent().Name,
    [string[]]$Operators = @(),
    [switch]$Apply,
    [switch]$StartServices,
    [switch]$StartSessionAgent
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $PSScriptRoot '..\artifacts\publish'
}

function Test-LocalAbsolutePath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not [System.IO.Path]::IsPathRooted($Path)) {
        return $false
    }

    $root = [System.IO.Path]::GetPathRoot($Path)
    return $root -match '^[A-Za-z]:\\$'
}

foreach ($selectedPath in @($InstallRoot, $DataRoot)) {
    if (-not (Test-LocalAbsolutePath $selectedPath)) {
        throw "CompanyOps 程序和数据目录必须使用本机磁盘绝对路径，不能使用相对路径或 UNC：$selectedPath"
    }
}

$publish = [System.IO.Path]::GetFullPath($PublishRoot)
$install = [System.IO.Path]::GetFullPath($InstallRoot)
$data = [System.IO.Path]::GetFullPath($DataRoot)

foreach ($selectedPath in @($install, $data)) {
    if ($selectedPath.TrimEnd('\') -eq [System.IO.Path]::GetPathRoot($selectedPath).TrimEnd('\')) {
        throw "不能把磁盘根目录作为 CompanyOps 目录：$selectedPath"
    }
    $selectedRoot = [System.IO.Path]::GetPathRoot($selectedPath)
    if (-not (Test-Path -LiteralPath $selectedRoot -PathType Container)) {
        throw "CompanyOps 目标磁盘不存在：$selectedRoot"
    }
}

$installPrefix = $install.TrimEnd('\') + '\'
$dataPrefix = $data.TrimEnd('\') + '\'
if ($install -eq $data -or
    $installPrefix.StartsWith($dataPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
    $dataPrefix.StartsWith($installPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'CompanyOps 程序目录和数据目录必须相互独立，不能相同或互相嵌套。'
}

if (-not $Apply) {
    Write-Host '安全预览：未传入 -Apply，不会复制文件或注册服务。'
    Write-Host "发布源：$publish"
    Write-Host "程序目录：$install"
    Write-Host "数据目录：$data"
    Write-Host '将注册 CompanyOps.Agent(LocalSystem) 与 CompanyOps.Console(NetworkService)，默认不启动。'
    Write-Host "将为当前交互用户注册登录任务：$SessionOwner（受限权限，默认不立即启动）。"
    return
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '安装需要在提升的 PowerShell 中执行。'
}

foreach ($component in @('Agent', 'Console', 'Pm2Bridge', 'SessionAgent', 'Cli')) {
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

try {
    $sessionOwnerAccount = [Security.Principal.NTAccount]::new($SessionOwner)
    $sessionOwnerSid = $sessionOwnerAccount.Translate([Security.Principal.SecurityIdentifier]).Value
}
catch {
    throw "无法解析交互会话用户：$SessionOwner"
}

$ownerKeyBytes = [Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes($sessionOwnerSid.ToUpperInvariant()))
$ownerKey = ([Convert]::ToHexString($ownerKeyBytes[0..7])).ToLowerInvariant()
$sessionTaskName = "CompanyOps.SessionAgent.$ownerKey"
$existingSessionTask = Get-ScheduledTask -TaskPath '\CompanyOps\' -TaskName $sessionTaskName -ErrorAction SilentlyContinue
if ($null -ne $existingSessionTask) {
    throw "交互会话任务已存在，拒绝覆盖：\CompanyOps\$sessionTaskName"
}

if (Test-Path -LiteralPath $install) {
    throw "安装目录已存在，拒绝覆盖：$install"
}

if (-not $PSCmdlet.ShouldProcess($install, '复制 CompanyOps 发布文件并注册 Windows Services')) {
    return
}

New-Item -ItemType Directory -Path $install | Out-Null
foreach ($component in @('Agent', 'Console', 'Pm2Bridge', 'SessionAgent', 'Cli')) {
    Copy-Item -LiteralPath (Join-Path $publish $component) -Destination (Join-Path $install $component) -Recurse
}

$manifestDirectory = Join-Path $data 'manifests'
$agentStateDirectory = Join-Path $data 'Agent'
$snapshotDirectory = Join-Path $agentStateDirectory 'pm2-snapshots'
$interactiveSnapshotDirectory = Join-Path $agentStateDirectory 'interactive-snapshots'
New-Item -ItemType Directory -Force -Path $manifestDirectory, $agentStateDirectory, $snapshotDirectory, $interactiveSnapshotDirectory | Out-Null
& icacls.exe $manifestDirectory /grant ('*{0}:(OI)(CI)RX' -f $sessionOwnerSid) /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw '无法授予 SessionAgent 读取运维声明的权限。' }
& icacls.exe $interactiveSnapshotDirectory /grant ('*{0}:(OI)(CI)M' -f $sessionOwnerSid) /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw '无法授予 SessionAgent 写入交互快照的权限。' }

$agentSettings = @{
    Ops = @{
        HostId = $HostId
        ManifestDirectory = $manifestDirectory
        StateDirectory = $agentStateDirectory
        Pm2SnapshotDirectory = $snapshotDirectory
        InteractiveSnapshotDirectory = $interactiveSnapshotDirectory
        PipeName = 'CompanyOps.Agent.v1'
        InventoryIntervalSeconds = 30
        EnableMutations = $false
        EnableExistingServiceOperations = $true
        EnableInteractiveSessionOperations = $true
        EnableExistingGitUpdates = $true
        GitExecutablePath = ''
        AllowedProjectInstallRoots = @()
        AllowedClientSids = @('S-1-5-20')
    }
    Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.Hosting.Lifetime' = 'Information' } }
}
$agentSettings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $install 'Agent\appsettings.json') -Encoding utf8

$sessionSettings = @{
    SessionAgent = @{
        ManifestDirectory = $manifestDirectory
        SnapshotDirectory = $interactiveSnapshotDirectory
        PipeName = "CompanyOps.SessionAgent.$ownerKey"
        SnapshotIntervalSeconds = 10
    }
    Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.Hosting.Lifetime' = 'Information' } }
}
$sessionSettings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $install 'SessionAgent\appsettings.json') -Encoding utf8

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

$sessionAgentRoot = Join-Path $install 'SessionAgent'
$sessionAgentExe = Join-Path $sessionAgentRoot 'CompanyOps.SessionAgent.exe'
$sessionAction = New-ScheduledTaskAction -Execute $sessionAgentExe -WorkingDirectory $sessionAgentRoot
$sessionTrigger = New-ScheduledTaskTrigger -AtLogOn -User $SessionOwner
$sessionPrincipal = New-ScheduledTaskPrincipal `
    -UserId $SessionOwner `
    -LogonType Interactive `
    -RunLevel Limited
$sessionSettingsSet = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew
Register-ScheduledTask `
    -TaskName $sessionTaskName `
    -TaskPath '\CompanyOps\' `
    -Action $sessionAction `
    -Trigger $sessionTrigger `
    -Principal $sessionPrincipal `
    -Settings $sessionSettingsSet `
    -Description 'CompanyOps 当前登录用户交互程序宿主；不执行脚本或任意命令。' | Out-Null

Write-Host '安装完成。Agent mutations 仍为 false，服务默认未启动。'
Write-Host "请先检查配置和 ACL：$install 与 $data"
Write-Host "交互会话任务：\CompanyOps\$sessionTaskName，用户 SID：$sessionOwnerSid"
if ($StartServices) {
    Start-Service -Name 'CompanyOps.Agent'
    Start-Service -Name 'CompanyOps.Console'
    Write-Host '已按显式 -StartServices 授权启动两个服务。'
}
if ($StartSessionAgent) {
    if (-not $StartServices) {
        Write-Warning 'SessionAgent 将单独启动；CompanyOps.Agent/Console 仍保持停止。'
    }
    Start-ScheduledTask -TaskPath '\CompanyOps\' -TaskName $sessionTaskName
    Write-Host '已按显式 -StartSessionAgent 授权启动当前用户 SessionAgent。'
}
