#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$UpdaterPath)
$ErrorActionPreference = 'Stop'
. $UpdaterPath
$realRunner = (Get-Item Function:\Invoke-CompanyOpsTool).ScriptBlock
$pwshPath = (Get-Process -Id $PID).Path
$testParent = Join-Path ([IO.Path]::GetTempPath()) 'CompanyOps.SourceUpdate.Tests'
$testRoot = Join-Path $testParent ([guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
function Assert-UpdateTest([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
try {
    $real = & $realRunner -FilePath $pwshPath -Arguments @('-NoProfile', '-Command', '[Console]::WriteLine("output"); [Console]::Error.WriteLine("error"); exit 7') -WorkingDirectory $testRoot -TimeoutSeconds 15
    Assert-UpdateTest ($real.ExitCode -eq 7 -and $real.Output.Contains('output') -and $real.Output.Contains('error')) '进程输出或退出码没有被保留'
    $timedOut = $false
    try { & $realRunner -FilePath $pwshPath -Arguments @('-NoProfile', '-Command', 'Start-Sleep -Seconds 30') -WorkingDirectory $testRoot -TimeoutSeconds 1 | Out-Null }
    catch { $timedOut = $_.Exception.Message.Contains('超时') }
    Assert-UpdateTest $timedOut '超时没有中止本次测试子进程'

    function Get-CompanyOpsBuildTools { return @{ git = 'git'; pwsh = 'pwsh'; dotnet = 'dotnet'; node = 'node'; npm = 'npm' } }
    function Get-CompanyOpsHostInstallation {
        if ($script:scenario -eq 'missing-installation') { return $null }
        if ($script:scenario -eq 'overlapping-installation') { return $script:fixture }
        return (Join-Path $testRoot 'existing-installation')
    }
    function Get-CompanyOpsInstalledRevision {
        param($InstallRoot)
        if ($script:scenario -eq 'already-current') { return $script:target }
        if ($script:scenario -eq 'older-remote') { return ('4' * 40) }
        return $null
    }
    function Assert-CompanyOpsUpdatePackage {
        param($PackageRoot, $Revision, $BuildTools)
        $script:validated = $true
        if ($script:scenario -eq 'bad-package') { throw '包校验失败' }
        Assert-UpdateTest ($Revision -eq $script:target) '校验未绑定目标提交'
        return (Join-Path $PackageRoot 'Setup\CompanyOps-Setup.exe')
    }
    function Start-CompanyOpsUpgrade {
        param($SetupPath)
        Assert-UpdateTest $script:validated '校验前启动了安装器'
        $script:launched = $true
        if ($script:scenario -eq 'installer-cancelled') { throw '升级已取消' }
    }
    function Invoke-CompanyOpsTool {
        param($FilePath, $Arguments, $WorkingDirectory, $TimeoutSeconds = 120, [switch]$ShowOutput)
        $result = ''; $exitCode = 0
        if ($FilePath -eq 'pwsh') {
            $script:built = $true
            Assert-UpdateTest ($Arguments[-1] -eq (Join-Path $script:fixture 'tools\Build-CompanyOpsSetup.ps1')) '调用了非固定构建入口'
            if ($script:scenario -eq 'build-failed') { $exitCode = 9; $result = '构建失败' }
            if ($script:scenario -eq 'head-changed-after-build') { $script:head = '3' * 40 }
        } else {
            switch ($Arguments[0]) {
                'rev-parse' {
                    $result = switch ($Arguments[1]) {
                        '--show-toplevel' { $script:fixture }
                        '--git-path' { '.git\companyops-update.lock' }
                        'HEAD' { $script:head }
                        'FETCH_HEAD' { $script:target }
                    }
                }
                'remote' { $result = if ($script:scenario -eq 'wrong-remote') { 'https://example.com/wrong.git' } else { 'https://github.com/alo58-alt/Ops_Manifest_Specification.git' } }
                'branch' { $result = if ($script:scenario -eq 'wrong-branch') { 'feature' } else { 'main' } }
                'status' {
                    if ($script:scenario -eq 'dirty' -or
                        ($script:scenario -eq 'changed-before-merge' -and $script:fetched) -or
                        ($script:scenario -eq 'dirty-after-build' -and $script:built)) { $result = ' M keep.txt' }
                }
                'fetch' { $script:fetched = $true; if ($script:scenario -eq 'fetch-failed') { $exitCode = 1; $result = '远端不可用' } }
                'merge-base' { if ($script:scenario -eq 'diverged' -or ($script:scenario -eq 'older-remote' -and $Arguments[2] -eq ('4' * 40))) { $exitCode = 1 } }
                'merge' { $script:merged = $true; $script:head = $script:target }
                default { throw "非预期 Git 调用：$Arguments" }
            }
        }
        return [pscustomobject]@{ ExitCode = $exitCode; Output = $result }
    }
    $scenarios = @('success', 'check-only', 'prepare-only', 'missing-installation', 'overlapping-installation',
        'wrong-remote', 'wrong-branch', 'dirty', 'fetch-failed', 'diverged', 'changed-before-merge',
        'build-failed', 'dirty-after-build', 'head-changed-after-build', 'bad-package', 'installer-cancelled', 'concurrent', 'already-current', 'older-remote')
    foreach ($script:scenario in $scenarios) {
        $script:fixture = Join-Path $testRoot $script:scenario
        [IO.Directory]::CreateDirectory((Join-Path $script:fixture '.git')) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $script:fixture 'tools')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $script:fixture 'tools\Build-CompanyOpsSetup.ps1'), '# inert fixture')
        $script:head = '1' * 40; $script:target = '2' * 40
        $script:fetched = $false; $script:merged = $false; $script:built = $false; $script:validated = $false; $script:launched = $false
        $failed = $false; $held = $null; $result = $null
        try {
            if ($script:scenario -eq 'concurrent') {
                $held = [IO.File]::Open((Join-Path $script:fixture '.git\companyops-update.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
            }
            $result = Invoke-CompanyOpsSourceUpdate -RepositoryRoot $script:fixture -CheckOnly:($script:scenario -eq 'check-only') -PrepareOnly:($script:scenario -eq 'prepare-only')
        } catch { $failed = $true }
        finally { if ($null -ne $held) { $held.Dispose() } }
        $expectedSuccess = $script:scenario -in @('success', 'check-only', 'prepare-only', 'already-current')
        Assert-UpdateTest ($failed -ne $expectedSuccess) "场景结果错误：$script:scenario"
        Assert-UpdateTest ($script:launched -eq ($script:scenario -in @('success', 'installer-cancelled'))) "安装器门禁错误：$script:scenario"
        if ($script:scenario -eq 'check-only') {
            Assert-UpdateTest (-not $script:merged -and -not $script:built -and $result.Outcome -eq 'Checked') '检查模式修改了源码或启动了构建'
        }
        if ($script:scenario -eq 'prepare-only') { Assert-UpdateTest ($script:validated -and $result.Outcome -eq 'Prepared') '准备模式未完成制品校验' }
        if ($script:scenario -eq 'already-current') { Assert-UpdateTest (-not $script:built -and $result.Outcome -eq 'AlreadyCurrent') '相同已安装版本仍触发构建或服务操作' }
        if ($script:scenario -in @('wrong-remote', 'wrong-branch', 'dirty', 'missing-installation', 'overlapping-installation', 'concurrent')) {
            Assert-UpdateTest (-not $script:fetched -and -not $script:built) "预检失败后仍获取或构建：$script:scenario"
        }
        Write-Host "[PASS] $script:scenario"
    }
    Write-Host 'COMPANYOPS-SOURCE-UPDATE-TESTS-PASSED:19'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($testParent) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '拒绝清理测试根目录外的路径' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
