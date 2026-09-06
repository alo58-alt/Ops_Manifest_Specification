#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [switch]$PrepareOnly,
    [ValidateRange(1, 120)][int]$BuildTimeoutMinutes = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-CompanyOpsTool {
    param([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory,
        [int]$TimeoutSeconds = 120, [switch]$ShowOutput)
    $start = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment['GIT_TERMINAL_PROMPT'] = '0'
    $start.Environment['GCM_INTERACTIVE'] = 'never'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $output = [Text.StringBuilder]::new()
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $started = $false
    try {
        if (-not $process.Start()) { throw "无法启动：$FilePath" }
        $started = $true
        $stdout = $process.StandardOutput.ReadLineAsync()
        $stderr = $process.StandardError.ReadLineAsync()
        while ($null -ne $stdout -or $null -ne $stderr -or -not $process.HasExited) {
            if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }
                throw "命令超时（$TimeoutSeconds 秒），已结束本次构建/检查进程树：$FilePath"
            }
            foreach ($streamName in @('stdout', 'stderr')) {
                $pending = Get-Variable -Name $streamName -ValueOnly
                if ($null -eq $pending -or -not $pending.IsCompleted) { continue }
                $line = $pending.GetAwaiter().GetResult()
                if ($null -eq $line) { Set-Variable -Name $streamName -Value $null; continue }
                if ($ShowOutput) { Write-Host $line }
                [void]$output.AppendLine($line)
                if ($output.Length -gt 262144) { [void]$output.Remove(0, $output.Length - 262144) }
                $reader = if ($streamName -eq 'stdout') { $process.StandardOutput } else { $process.StandardError }
                Set-Variable -Name $streamName -Value $reader.ReadLineAsync()
            }
            if (-not $process.HasExited -or $null -ne $stdout -or $null -ne $stderr) {
                [Threading.Thread]::Sleep(20)
            }
        }
        $process.WaitForExit()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output.ToString().Trim() }
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
}

function Invoke-CompanyOpsCheckedTool {
    param([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory,
        [int]$TimeoutSeconds = 120, [switch]$ShowOutput)
    $result = Invoke-CompanyOpsTool @PSBoundParameters
    if ($result.ExitCode -ne 0) { throw "命令失败（退出码 $($result.ExitCode)）：$($result.Output)" }
    return $result.Output
}

function Get-CompanyOpsBuildTools {
    $result = @{}
    foreach ($name in @('git', 'pwsh', 'dotnet', 'node', 'npm')) {
        $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $command) { throw "服务器缺少 $name；请先完成源码构建环境准备。更新器不安装系统依赖。" }
        $result[$name] = $command.Source
    }
    return $result
}

function Get-CompanyOpsHostInstallation {
    $images = @()
    foreach ($name in @('CompanyOps.Agent', 'CompanyOps.Console')) {
        $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $name
        $images += if (Test-Path -LiteralPath $key) {
            Get-ItemPropertyValue -LiteralPath $key -Name ImagePath
        } else { $null }
    }
    if ($null -eq $images[0] -and $null -eq $images[1]) { return $null }
    if ($null -eq $images[0] -or $null -eq $images[1]) { throw 'CompanyOps 服务安装不完整，请先排障。' }
    $roots = @()
    for ($i = 0; $i -lt 2; $i++) {
        $value = [Environment]::ExpandEnvironmentVariables([string]$images[$i]).Trim()
        $exe = if ($value.StartsWith('"')) { ($value -split '"')[1] } else { ($value -split '\s+')[0] }
        $expected = if ($i -eq 0) { 'CompanyOps.Agent.exe' } else { 'CompanyOps.Console.exe' }
        if ([IO.Path]::GetFileName($exe) -ne $expected -or -not [IO.Path]::IsPathFullyQualified($exe)) {
            throw 'CompanyOps 服务入口不符合标准结构，拒绝更新。'
        }
        $roots += [IO.Directory]::GetParent([IO.Path]::GetDirectoryName($exe)).FullName
    }
    if ($roots[0] -ne $roots[1]) { throw 'CompanyOps Agent 与 Console 不属于同一安装目录。' }
    return $roots[0]
}

function Test-CompanyOpsPathOverlap {
    param([string]$Left, [string]$Right)
    $leftRoot = [IO.Path]::GetFullPath($Left).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $rightRoot = [IO.Path]::GetFullPath($Right).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return $leftRoot.StartsWith($rightRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $rightRoot.StartsWith($leftRoot, [StringComparison]::OrdinalIgnoreCase)
}

function Get-CompanyOpsInstalledRevision {
    param([string]$InstallRoot)
    if ([string]::IsNullOrWhiteSpace($InstallRoot)) { return $null }
    $assembly = Join-Path $InstallRoot 'Agent\CompanyOps.Agent.dll'
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { return $null }
    $version = (Get-Item -LiteralPath $assembly).VersionInfo.ProductVersion
    if ($version -match '\+([a-f0-9]{40})$') { return $Matches[1] }
    return $null
}

function Assert-CompanyOpsBuildDirectories {
    param([string]$RepositoryRoot)
    foreach ($relative in @('artifacts', 'artifacts\publish', 'artifacts\setup', 'output')) {
        $path = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $relative))
        if (-not $path.StartsWith($RepositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw '构建输出目录逃逸源码仓库，拒绝构建。'
        }
        if (Test-Path -LiteralPath $path) {
            $item = Get-Item -LiteralPath $path -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "构建输出位置不是普通目录：$path"
            }
        }
    }
}

function Assert-CompanyOpsUpdatePackage {
    param([string]$PackageRoot, [string]$Revision, [hashtable]$BuildTools)
    $setup = Join-Path $PackageRoot 'Setup\CompanyOps-Setup.exe'
    $assembly = Join-Path $PackageRoot 'Setup\CompanyOps-Setup.dll'
    $agentAssembly = Join-Path $PackageRoot 'Payload\Agent\CompanyOps.Agent.dll'
    foreach ($file in @($setup, $assembly, $agentAssembly, (Join-Path $PackageRoot 'Payload.sha256.json'))) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "构建未产生完整升级文件：$file" }
    }
    $version = (Get-Item -LiteralPath $agentAssembly).VersionInfo.ProductVersion
    if (-not $version.EndsWith('+' + $Revision, [StringComparison]::OrdinalIgnoreCase)) {
        throw '构建产物源码版本与本次目标提交不一致，拒绝启动升级器。'
    }
    Invoke-CompanyOpsCheckedTool -FilePath $BuildTools.dotnet -Arguments @($assembly, '--verify-payload', $PackageRoot) -WorkingDirectory $PackageRoot -TimeoutSeconds 120 -ShowOutput | Out-Null
    return $setup
}

function Start-CompanyOpsUpgrade {
    param([string]$SetupPath)
    # This is the user-selected interactive upgrade. Setup requests UAC and
    # requires another explicit click before it changes existing services.
    $process = Start-Process -FilePath $SetupPath -ArgumentList '--upgrade-only' -WorkingDirectory (Split-Path -Parent $SetupPath) -PassThru
    try {
        if (-not $process.WaitForExit(3600000)) {
            throw '升级窗口仍未关闭，结果尚未确定。窗口会继续运行，请在其中完成或取消；不要重复更新。'
        }
        if ($process.ExitCode -ne 0) { throw "升级未完成（退出码 $($process.ExitCode)）；请查看安装器提示。" }
    } finally { $process.Dispose() }
}

function Invoke-CompanyOpsSourceUpdate {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepositoryRoot, [switch]$CheckOnly, [switch]$PrepareOnly,
        [ValidateRange(1, 120)][int]$BuildTimeoutMinutes = 30)
    if ($CheckOnly -and $PrepareOnly) { throw 'CheckOnly 与 PrepareOnly 不能同时使用。' }
    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    $buildTools = Get-CompanyOpsBuildTools
    $top = Invoke-CompanyOpsCheckedTool $buildTools.git @('rev-parse', '--show-toplevel') $root
    if ([IO.Path]::GetFullPath($top).TrimEnd('\', '/') -ne $root) { throw '必须从 Ops 仓库根目录执行更新。' }
    $remote = Invoke-CompanyOpsCheckedTool $buildTools.git @('remote', 'get-url', 'origin') $root
    if ($remote -ne 'https://github.com/alo58-alt/Ops_Manifest_Specification.git') { throw 'origin 不是声明的 CompanyOps 官方仓库，拒绝更新。' }
    $branch = Invoke-CompanyOpsCheckedTool $buildTools.git @('branch', '--show-current') $root
    if ($branch -ne 'main') { throw 'Ops 更新要求 main 分支；不会自动切换分支。' }
    $installation = Get-CompanyOpsHostInstallation
    if ($null -ne $installation -and (Test-CompanyOpsPathOverlap $root $installation)) {
        throw '源码目录与正在使用的 CompanyOps 安装目录重叠，拒绝在运行目录构建。'
    }
    if (-not $CheckOnly -and -not $PrepareOnly -and $null -eq $installation) {
        throw '本机没有现有 CompanyOps 安装；更新入口不会执行首次安装。'
    }
    $lockPath = Invoke-CompanyOpsCheckedTool $buildTools.git @('rev-parse', '--git-path', 'companyops-update.lock') $root
    if (-not [IO.Path]::IsPathFullyQualified($lockPath)) { $lockPath = Join-Path $root $lockPath }
    try { $lease = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch [IO.IOException] { throw '此仓库已有 CompanyOps 更新正在执行，请查看原更新窗口。' }
    try {
        $dirty = Invoke-CompanyOpsCheckedTool $buildTools.git @('status', '--porcelain', '--untracked-files=all') $root
        if (-not [string]::IsNullOrWhiteSpace($dirty)) { throw '源码工作树有本地改动，拒绝更新；不会 stash、reset 或清理文件。' }
        $before = Invoke-CompanyOpsCheckedTool $buildTools.git @('rev-parse', 'HEAD') $root
        Write-Host '[检查] 正在获取官方 main 分支…'
        Invoke-CompanyOpsCheckedTool $buildTools.git @('fetch', '--no-tags', 'origin', 'refs/heads/main') $root | Out-Null
        $target = Invoke-CompanyOpsCheckedTool $buildTools.git @('rev-parse', 'FETCH_HEAD') $root
        if ($target -notmatch '^[a-f0-9]{40}$') { throw '远端目标提交格式无效。' }
        $ancestor = Invoke-CompanyOpsTool $buildTools.git @('merge-base', '--is-ancestor', $before, $target) $root
        if ($ancestor.ExitCode -ne 0) { throw '源码分支不能快进到远端 main，拒绝更新。' }
        $installedRevision = Get-CompanyOpsInstalledRevision $installation
        if ($null -ne $installedRevision -and $installedRevision -ne $target) {
            $upgradeDirection = Invoke-CompanyOpsTool $buildTools.git @('merge-base', '--is-ancestor', $installedRevision, $target) $root
            if ($upgradeDirection.ExitCode -ne 0) { throw '远端目标不是已安装版本的后续提交，拒绝自动降级或未知版本切换。' }
        }
        Write-Host "[目标] $before -> $target"
        if ($CheckOnly) { return [pscustomobject]@{ Outcome = 'Checked'; SourceRevision = $target; InstallerStarted = $false } }
        $dirty = Invoke-CompanyOpsCheckedTool $buildTools.git @('status', '--porcelain', '--untracked-files=all') $root
        $current = Invoke-CompanyOpsCheckedTool $buildTools.git @('rev-parse', 'HEAD') $root
        if ($current -ne $before -or -not [string]::IsNullOrWhiteSpace($dirty)) { throw '检查期间源码发生变化，请重新运行更新。' }
        Invoke-CompanyOpsCheckedTool $buildTools.git @('merge', '--ff-only', '--no-edit', $target) $root -ShowOutput | Out-Null
        if (-not $PrepareOnly -and $installedRevision -eq $target) {
            Write-Host '[完成] 已安装当前版本，无需重新构建或重启服务。'
            return [pscustomobject]@{ Outcome = 'AlreadyCurrent'; SourceRevision = $target; InstallerStarted = $false }
        }
        Write-Host '[构建] 正在服务器生成并验证新版，现有服务继续运行…'
        Assert-CompanyOpsBuildDirectories $root
        $buildScript = Join-Path $root 'tools\Build-CompanyOpsSetup.ps1'
        if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) { throw '目标提交缺少固定的平台构建入口。' }
        Invoke-CompanyOpsCheckedTool -FilePath $buildTools.pwsh -Arguments @('-NoProfile', '-File', $buildScript) -WorkingDirectory $root -TimeoutSeconds ($BuildTimeoutMinutes * 60) -ShowOutput | Out-Null
        $after = Invoke-CompanyOpsCheckedTool $buildTools.git @('rev-parse', 'HEAD') $root
        $dirty = Invoke-CompanyOpsCheckedTool $buildTools.git @('status', '--porcelain', '--untracked-files=all') $root
        if ($after -ne $target -or -not [string]::IsNullOrWhiteSpace($dirty)) { throw '构建期间源码发生变化，拒绝升级现有服务。' }
        $packageRoot = Join-Path $root 'artifacts\setup\package\CompanyOps-Offline'
        $setupPath = Assert-CompanyOpsUpdatePackage $packageRoot $target $buildTools
        if ($PrepareOnly) { return [pscustomobject]@{ Outcome = 'Prepared'; SourceRevision = $target; InstallerStarted = $false; PackageRoot = $packageRoot } }
        Write-Host '[升级] 已完成构建和校验。请在安装器中核对原目录与授权，点击“升级并启动”。'
        Start-CompanyOpsUpgrade $setupPath
        Write-Host '[完成] CompanyOps 升级成功。'
        return [pscustomobject]@{ Outcome = 'Upgraded'; SourceRevision = $target; InstallerStarted = $true }
    } finally { $lease.Dispose() }
}

if ($MyInvocation.InvocationName -ne '.') {
    $updateRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $logDirectory = Join-Path $updateRoot 'artifacts\platform-updates'
    [IO.Directory]::CreateDirectory($logDirectory) | Out-Null
    $logPath = Join-Path $logDirectory ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID + '.log')
    Start-Transcript -LiteralPath $logPath | Out-Null
    try {
        Invoke-CompanyOpsSourceUpdate -RepositoryRoot $updateRoot -CheckOnly:$CheckOnly -PrepareOnly:$PrepareOnly -BuildTimeoutMinutes $BuildTimeoutMinutes | Format-List
        exit 0
    } catch {
        Write-Host "[失败] $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "日志：$logPath"
        exit 1
    } finally { Stop-Transcript | Out-Null }
}
