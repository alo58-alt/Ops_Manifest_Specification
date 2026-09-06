#requires -Version 5.1
[CmdletBinding()]
param([string]$FeedRoot, [switch]$CheckOnly, [switch]$Unattended, [switch]$Apply,
    [string]$FromRevision, [string]$InstallRoot, [string]$DataRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-CompanyOpsOrdinaryPath {
    param([string]$Path)
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "不允许链接路径：$Path" }
        $item = if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }
    }
}

function Get-CompanyOpsPackageHash {
    param([string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Read-CompanyOpsPrebuiltPackage {
    param([Parameter(Mandatory)][string]$FeedRoot)
    $root = [IO.Path]::GetFullPath($FeedRoot).TrimEnd('\', '/')
    Assert-CompanyOpsOrdinaryPath $root
    $pointerPath = Join-Path $root 'latest.json'
    Assert-CompanyOpsOrdinaryPath $pointerPath
    $pointerHash = Get-CompanyOpsPackageHash $pointerPath
    $pointer = Get-Content -LiteralPath $pointerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($pointer.version -ne 1 -or $pointer.revision -cnotmatch '^[a-f0-9]{40}$' -or
        $pointer.recordSha256 -cnotmatch '^[a-f0-9]{64}$') { throw '发布索引格式无效。' }
    # A revision is the only accepted directory name; the index cannot select an executable or command.
    $package = Join-Path $root $pointer.revision
    Assert-CompanyOpsOrdinaryPath $package
    $recordPath = Join-Path $package 'package-record.json'
    Assert-CompanyOpsOrdinaryPath $recordPath
    if ((Get-CompanyOpsPackageHash $recordPath) -ne $pointer.recordSha256) { throw '安装包记录哈希不匹配。' }
    $record = Get-Content -LiteralPath $recordPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($record.version -ne 1 -or $record.revision -ne $pointer.revision -or
        @($record.files).Count -lt 4 -or @($record.files).Count -gt 20000) { throw '安装包记录不完整。' }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $record.files) {
        $relative = [string]$entry.path
        if ($relative -match '(^/|\\|:|(^|/)\.{1,2}(/|$)|//|[. ](/|$))' -or
            [string]::IsNullOrWhiteSpace($relative) -or $relative -eq 'package-record.json' -or
            $entry.sha256 -cnotmatch '^[a-f0-9]{64}$' -or -not $expected.Add($relative)) {
            throw "安装包包含无效或重复路径：$relative"
        }
        $file = [IO.Path]::GetFullPath((Join-Path $package $relative))
        if (-not $file.StartsWith($package + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '安装包路径越界。' }
        Assert-CompanyOpsOrdinaryPath $file
        $item = Get-Item -LiteralPath $file -Force
        if ($item.PSIsContainer -or $item.Length -ne $entry.sizeBytes -or
            (Get-CompanyOpsPackageHash $file) -ne $entry.sha256) { throw "安装包文件校验失败：$relative" }
    }
    foreach ($required in @('Setup/CompanyOps-Setup.exe', 'Setup/CompanyOps-Setup.dll',
        'Payload/Agent/CompanyOps.Agent.dll', 'Payload.sha256.json')) {
        if (-not $expected.Contains($required)) { throw "安装包缺少固定入口：$required" }
    }
    foreach ($item in Get-ChildItem -LiteralPath $package -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '安装包中不允许链接。' }
        if (-not $item.PSIsContainer) {
            $relative = $item.FullName.Substring($package.Length + 1).Replace('\', '/')
            if ($relative -ne 'package-record.json' -and -not $expected.Contains($relative)) { throw "安装包含未登记文件：$relative" }
        }
    }
    if ((Get-CompanyOpsPackageHash $pointerPath) -ne $pointerHash) { throw '校验期间发布索引发生变化，请重新检查。' }
    return [pscustomobject]@{ Revision = $pointer.revision; PackageRoot = $package;
        SetupPath = (Join-Path $package 'Setup\CompanyOps-Setup.exe'); PointerHash = $pointerHash; FileCount = $expected.Count }
}

function Start-CompanyOpsPrebuiltUpgrade {
    param([string]$SetupPath)
    # The selected installer is interactive and requests its own Windows permission prompt.
    $process = Start-Process -FilePath $SetupPath -ArgumentList '--upgrade-only' -WorkingDirectory (Split-Path -Parent $SetupPath) -PassThru
    try {
        if (-not $process.WaitForExit(3600000)) { throw '升级窗口尚未关闭，请在现有窗口完成或取消；不要重复启动。' }
        if ($process.ExitCode -ne 0) { throw "安装器未完成升级，退出码：$($process.ExitCode)" }
    } finally { $process.Dispose() }
}

function Invoke-CompanyOpsPrebuiltUpdate {
    param([Parameter(Mandatory)][string]$FeedRoot, [switch]$CheckOnly, [switch]$Unattended, [switch]$Apply,
        [string]$FromRevision, [string]$InstallRoot, [string]$DataRoot)
    if ($Apply -and (-not $Unattended -or $CheckOnly)) { throw 'Apply 只能用于显式选择的 Unattended 升级。' }
    $package = Read-CompanyOpsPrebuiltPackage $FeedRoot
    Write-Host "[校验通过] $($package.Revision)，$($package.FileCount) 个预编译文件。"
    if ($CheckOnly) { return $package }
    if ($Unattended) {
        if ($FromRevision -cnotmatch '^[a-f0-9]{40}$') { throw '必须指定 FromRevision 完整版本。' }
        foreach ($path in @($InstallRoot, $DataRoot)) {
            if ($path -notmatch '^[A-Za-z]:\\' -or $path -match '["\r\n]') { throw '必须指定本机程序及数据目录。' }
        }
        $logDirectory = Join-Path ([IO.Path]::GetFullPath($FeedRoot)) 'upgrade-logs'
        if (Test-Path -LiteralPath $logDirectory) { Assert-CompanyOpsOrdinaryPath $logDirectory }
        [IO.Directory]::CreateDirectory($logDirectory) | Out-Null
        $operation = [guid]::NewGuid().ToString('N')
        $stdout = Join-Path $logDirectory ($operation + '.result.json')
        $stderr = Join-Path $logDirectory ($operation + '.log')
        $arguments = '--upgrade-unattended --from-revision ' + $FromRevision + ' --to-revision ' + $package.Revision +
            ' --install-root "' + $InstallRoot.TrimEnd('\') + '" --data-root "' + $DataRoot.TrimEnd('\') + '"'
        if ($Apply) { $arguments += ' --apply' }
        $process = Start-Process -FilePath $package.SetupPath -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $package.SetupPath) `
            -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
        try {
            if (-not $process.WaitForExit(900000)) { throw "升级进程尚未结束；不要重复执行，请查询 $stderr" }
            $process.Refresh()
            $exitCode = $process.ExitCode
            $details = [IO.File]::ReadAllText($stderr)
            if ($details) { Write-Host $details }
            if ($exitCode -ne 0) { throw "命令行升级失败，退出码 $exitCode；日志：$stderr" }
            $result = [IO.File]::ReadAllText($stdout) | ConvertFrom-Json
            if ($result.Outcome -notin @('Planned', 'Upgraded', 'AlreadyCurrent')) { throw '安装器未返回明确结果。' }
            return $result
        } finally { $process.Dispose() }
    }
    Start-CompanyOpsPrebuiltUpgrade $package.SetupPath
    Write-Host '[完成] CompanyOps 安装器已确认升级成功。'
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        if ([string]::IsNullOrWhiteSpace($FeedRoot)) {
            $candidate = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
            if (Test-Path -LiteralPath (Join-Path $candidate 'latest.json')) { $FeedRoot = $candidate }
            else {
                Add-Type -AssemblyName System.Windows.Forms
                $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
                try {
                    $dialog.Description = '选择开发端已发布的 CompanyOps 安装包目录（包含 latest.json）'
                    if ($dialog.ShowDialog() -ne 'OK') { exit 2 }
                    $FeedRoot = $dialog.SelectedPath
                } finally { $dialog.Dispose() }
            }
        }
        Invoke-CompanyOpsPrebuiltUpdate -FeedRoot $FeedRoot -CheckOnly:$CheckOnly -Unattended:$Unattended -Apply:$Apply `
            -FromRevision $FromRevision -InstallRoot $InstallRoot -DataRoot $DataRoot | Format-List
        exit 0
    } catch { Write-Host "[失败] $($_.Exception.Message)" -ForegroundColor Red; exit 1 }
}
