#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][string]$FeedRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$selectedPackage = $PackageRoot
$selectedFeed = $FeedRoot
. (Join-Path $PSScriptRoot 'Update-CompanyOps.ps1')

function Write-CompanyOpsAtomicFile {
    param([string]$Path, [string]$Text, [Text.Encoding]$Encoding = [Text.UTF8Encoding]::new($false))
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary, $Text, $Encoding)
    try {
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, $null) }
        else { [IO.File]::Move($temporary, $Path) }
    } finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
}

function Publish-CompanyOpsPrebuiltPackage {
    param([string]$PackageRoot, [string]$FeedRoot)
    $source = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\', '/')
    $feed = [IO.Path]::GetFullPath($FeedRoot).TrimEnd('\', '/')
    if (($source + '\').StartsWith($feed + '\', [StringComparison]::OrdinalIgnoreCase) -or
        ($feed + '\').StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '源码包与服务器发布目录不能重叠。' }
    Assert-CompanyOpsOrdinaryPath $source
    $items = @(Get-ChildItem -LiteralPath $source -Recurse -Force)
    if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw '源安装包包含链接。' }
    foreach ($required in @('Setup\CompanyOps-Setup.exe', 'Setup\CompanyOps-Setup.dll',
        'Payload\Agent\CompanyOps.Agent.dll', 'Payload.sha256.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $source $required) -PathType Leaf)) { throw "安装包缺少 $required" }
    }
    $agentVersion = (Get-Item -LiteralPath (Join-Path $source 'Payload\Agent\CompanyOps.Agent.dll')).VersionInfo.ProductVersion
    if ($agentVersion -notmatch '\+([a-f0-9]{40})$') { throw '安装包未携带完整源码提交版本。' }
    $revision = $Matches[1]
    $files = @($items | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($source.Length + 1).Replace('\', '/');
            sizeBytes = $_.Length; sha256 = Get-CompanyOpsPackageHash $_.FullName }
    })
    $record = [ordered]@{ version = 1; revision = $revision; files = $files } | ConvertTo-Json -Depth 5
    # Verify every existing ancestor before creating a destination on the explicitly selected share.
    $ancestor = $feed
    while (-not (Test-Path -LiteralPath $ancestor)) { $ancestor = [IO.Directory]::GetParent($ancestor).FullName }
    Assert-CompanyOpsOrdinaryPath $ancestor
    [IO.Directory]::CreateDirectory($feed) | Out-Null
    Assert-CompanyOpsOrdinaryPath $feed
    $lease = [IO.File]::Open((Join-Path $feed '.publish.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $staging = Join-Path $feed ('.upload-' + [guid]::NewGuid().ToString('N'))
    try {
        [IO.Directory]::CreateDirectory($staging) | Out-Null
        $stagedPackage = Join-Path $staging $revision
        [IO.Directory]::CreateDirectory($stagedPackage) | Out-Null
        foreach ($entry in $files) {
            $target = Join-Path $stagedPackage $entry.path
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.File]::Copy((Join-Path $source $entry.path), $target, $false)
        }
        [IO.File]::WriteAllText((Join-Path $stagedPackage 'package-record.json'), $record, [Text.UTF8Encoding]::new($false))
        $pointer = [ordered]@{ version = 1; revision = $revision;
            recordSha256 = Get-CompanyOpsPackageHash (Join-Path $stagedPackage 'package-record.json') } | ConvertTo-Json
        [IO.File]::WriteAllText((Join-Path $staging 'latest.json'), $pointer, [Text.UTF8Encoding]::new($false))
        $verified = Read-CompanyOpsPrebuiltPackage $staging
        $destination = Join-Path $feed $revision
        if (Test-Path -LiteralPath $destination) {
            throw "该版本目录已存在，不覆盖不可变安装包：$destination"
        }
        if (-not $destination.StartsWith($feed + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not $stagedPackage.StartsWith($staging + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '目录移动越界。' }
        [IO.Directory]::Move($stagedPackage, $destination)
        $toolDirectory = Join-Path $feed 'tools'
        if (Test-Path -LiteralPath $toolDirectory) { Assert-CompanyOpsOrdinaryPath $toolDirectory }
        [IO.Directory]::CreateDirectory($toolDirectory) | Out-Null
        foreach ($existing in @((Join-Path $feed 'latest.json'), (Join-Path $feed '更新CompanyOps.cmd'),
            (Join-Path $toolDirectory 'Update-CompanyOps.ps1'))) {
            if (Test-Path -LiteralPath $existing) { Assert-CompanyOpsOrdinaryPath $existing }
        }
        $updater = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Update-CompanyOps.ps1'))
        Write-CompanyOpsAtomicFile (Join-Path $toolDirectory 'Update-CompanyOps.ps1') $updater ([Text.UTF8Encoding]::new($true))
        $launcher = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\更新CompanyOps.cmd'))
        Write-CompanyOpsAtomicFile (Join-Path $feed '更新CompanyOps.cmd') $launcher ([Text.Encoding]::ASCII)
        # Publish the selector only after transferred bytes and the fixed updater are ready.
        Write-CompanyOpsAtomicFile (Join-Path $feed 'latest.json') $pointer
        $final = Read-CompanyOpsPrebuiltPackage $feed
        return [pscustomobject]@{ Revision = $final.Revision; FilesVerified = $final.FileCount;
            PackageRoot = $final.PackageRoot; UpdateEntry = (Join-Path $feed '更新CompanyOps.cmd'); InstallerStarted = $false }
    } finally {
        $lease.Dispose()
        $resolvedStaging = [IO.Path]::GetFullPath($staging)
        if (-not $resolvedStaging.StartsWith($feed + '\.upload-', [StringComparison]::OrdinalIgnoreCase)) { throw '拒绝清理发布目录外的路径。' }
        if (Test-Path -LiteralPath $resolvedStaging) {
            Assert-CompanyOpsOrdinaryPath $resolvedStaging
            Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') { Publish-CompanyOpsPrebuiltPackage $selectedPackage $selectedFeed | Format-List }
