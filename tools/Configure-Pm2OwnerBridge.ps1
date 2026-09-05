[CmdletBinding()]
param(
    [string]$ExpectedOwnerSid = '',
    [string]$ExpectedOwnerAccount = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $unelevatedOwnerSid = $identity.User.Value
    $unelevatedOwnerAccount = $identity.Name
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath),
        '-ExpectedOwnerSid', $unelevatedOwnerSid,
        '-ExpectedOwnerAccount', ('"{0}"' -f $unelevatedOwnerAccount)
    )
    $process = Start-Process `
        -FilePath 'powershell.exe' `
        -Verb RunAs `
        -Wait `
        -PassThru `
        -ArgumentList $arguments
    exit $process.ExitCode
}

$ownerSid = $identity.User.Value
$ownerAccount = $identity.Name
if ((-not [string]::IsNullOrWhiteSpace($ExpectedOwnerSid) -and
     -not [string]::Equals($ExpectedOwnerSid, $ownerSid, [StringComparison]::OrdinalIgnoreCase)) -or
    (-not [string]::IsNullOrWhiteSpace($ExpectedOwnerAccount) -and
     -not [string]::Equals($ExpectedOwnerAccount, $ownerAccount, [StringComparison]::OrdinalIgnoreCase))) {
    throw '提升权限后变成了另一个 Windows 账号。请让真实 PM2 owner 使用自己的管理员权限运行，不能用其他管理员凭据代替。'
}
$bridgeRoot = Split-Path -Parent $PSCommandPath
$installRoot = Split-Path -Parent $bridgeRoot
$bridgeExecutable = Join-Path $bridgeRoot 'CompanyOps.Pm2Bridge.exe'
$bridgeConfigPath = Join-Path $bridgeRoot 'appsettings.json'
$agentConfigPath = Join-Path $installRoot 'Agent\appsettings.json'

foreach ($requiredFile in @($bridgeExecutable, $agentConfigPath)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "CompanyOps 安装不完整，缺少：$requiredFile"
    }
}

$nodeCommand = Get-Command node.exe -CommandType Application -ErrorAction Stop |
    Select-Object -First 1
$pm2Command = Get-Command pm2.cmd -CommandType Application -ErrorAction Stop |
    Select-Object -First 1
$nodeExecutable = $nodeCommand.Source
$pm2CliCandidates = @(
    (Join-Path (Split-Path -Parent $pm2Command.Source) 'node_modules\pm2\bin\pm2')
)
$npmCommand = Get-Command npm.cmd -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -ne $npmCommand) {
    $npmRoot = (& $npmCommand.Source root --global 2>$null | Select-Object -Last 1)
    if (-not [string]::IsNullOrWhiteSpace($npmRoot)) {
        $pm2CliCandidates += Join-Path $npmRoot.Trim() 'pm2\bin\pm2'
    }
}
$pm2CliPath = $pm2CliCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($pm2CliPath)) {
    throw '没有找到当前 Windows 用户安装的 PM2 JavaScript CLI。请先确认该账号执行 pm2 list 正常。'
}

# 只验证当前用户能访问自己的 daemon；完整 jlist 不写磁盘、不打印到屏幕。
$pm2Json = (& $nodeExecutable $pm2CliPath jlist 2>$null) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($pm2Json)) {
    throw '当前 Windows 用户无法读取自己的 PM2 daemon。请先确认该账号执行 pm2 list 正常。'
}
try {
    $null = $pm2Json | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw 'PM2 jlist 没有返回有效 JSON，未修改 CompanyOps 配置。'
}
finally {
    $pm2Json = $null
}

$agentSettings = Get-Content -Raw -LiteralPath $agentConfigPath |
    ConvertFrom-Json -ErrorAction Stop
$manifestDirectory = [Environment]::ExpandEnvironmentVariables(
    [string]$agentSettings.Ops.ManifestDirectory)
$snapshotDirectory = [Environment]::ExpandEnvironmentVariables(
    [string]$agentSettings.Ops.Pm2SnapshotDirectory)
if (-not [IO.Path]::IsPathRooted($manifestDirectory) -or
    -not [IO.Path]::IsPathRooted($snapshotDirectory)) {
    throw 'Agent 配置中缺少绝对 ManifestDirectory 或 Pm2SnapshotDirectory。'
}

$pipeName = "CompanyOps.Pm2Bridge.$ownerSid.v1"
$taskName = "CompanyOps-Pm2Bridge-$ownerSid"
$taskPath = '\CompanyOps\'
$discoveryFileName = "CompanyOps.Pm2Bridge.$ownerSid.discovery.json"
$discoveryPath = Join-Path $snapshotDirectory $discoveryFileName

New-Item -ItemType Directory -Force -Path $snapshotDirectory | Out-Null
& icacls.exe $bridgeRoot /grant:r "*$($ownerSid):(OI)(CI)RX" /T /C | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw '无法授予 PM2 owner 读取 Bridge 程序的权限。'
}
& icacls.exe $snapshotDirectory /grant:r "*$($ownerSid):(OI)(CI)M" /T /C | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw '无法授予 PM2 owner 写入缩减快照的权限。'
}

$settings = [ordered]@{
    Pm2Bridge = [ordered]@{
        PipeName = $pipeName
        OwnerSid = $ownerSid
        ManifestDirectory = [IO.Path]::GetFullPath($manifestDirectory)
        SnapshotDirectory = [IO.Path]::GetFullPath($snapshotDirectory)
        NodeExecutablePath = [IO.Path]::GetFullPath($nodeExecutable)
        Pm2CliPath = [IO.Path]::GetFullPath($pm2CliPath)
        SnapshotIntervalSeconds = 10
    }
}
$temporaryConfig = "$bridgeConfigPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $settings | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $temporaryConfig -Encoding UTF8
    Move-Item -LiteralPath $temporaryConfig -Destination $bridgeConfigPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryConfig) {
        Remove-Item -LiteralPath $temporaryConfig -Force -ErrorAction SilentlyContinue
    }
}

$existingTask = Get-ScheduledTask `
    -TaskName $taskName `
    -TaskPath $taskPath `
    -ErrorAction SilentlyContinue
if ($null -ne $existingTask) {
    $unexpectedAction = @($existingTask.Actions).Where({
        -not [string]::Equals(
            [IO.Path]::GetFullPath($_.Execute.Trim('"')),
            [IO.Path]::GetFullPath($bridgeExecutable),
            [StringComparison]::OrdinalIgnoreCase)
    })
    if ($existingTask.Principal.UserId -notin @($ownerAccount, $ownerSid) -or
        $unexpectedAction.Count -gt 0) {
        throw "同名计划任务已存在但归属或入口不同，拒绝覆盖：$taskPath$taskName"
    }
}

$action = New-ScheduledTaskAction `
    -Execute $bridgeExecutable `
    -WorkingDirectory $bridgeRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $ownerAccount
$taskPrincipal = New-ScheduledTaskPrincipal `
    -UserId $ownerAccount `
    -LogonType Interactive `
    -RunLevel Limited
Register-ScheduledTask `
    -TaskName $taskName `
    -TaskPath $taskPath `
    -Action $action `
    -Trigger $trigger `
    -Principal $taskPrincipal `
    -Description 'CompanyOps PM2 owner bridge，登录时启动' `
    -Force | Out-Null
Start-ScheduledTask -TaskName $taskName -TaskPath $taskPath

$deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
do {
    Start-Sleep -Milliseconds 500
    if (Test-Path -LiteralPath $discoveryPath -PathType Leaf) {
        try {
            $snapshot = Get-Content -Raw -LiteralPath $discoveryPath |
                ConvertFrom-Json -ErrorAction Stop
            if ($snapshot.protocolVersion -eq 'ops-pm2-snapshot/v1' -and
                $snapshot.ownerSid -eq $ownerSid -and
                $snapshot.controlPipeName -eq $pipeName -and
                ([DateTimeOffset]::UtcNow - [DateTimeOffset]$snapshot.capturedAt).TotalSeconds -le 30) {
                Write-Host ''
                Write-Host 'PM2 主机接管配置完成。' -ForegroundColor Green
                Write-Host "Owner：$ownerAccount"
                Write-Host "发现快照：$discoveryPath"
                Write-Host '现在回到 CompanyOps，选择项目目录并点击“检查项目”。'
                exit 0
            }
        }
        catch {
            # Bridge may be replacing the file atomically; keep waiting within the deadline.
        }
    }
} while ([DateTimeOffset]::UtcNow -lt $deadline)

throw 'PM2 Bridge 已配置，但 20 秒内没有生成有效发现快照。请检查计划任务历史和 Bridge 日志。'
