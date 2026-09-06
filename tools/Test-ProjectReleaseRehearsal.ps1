#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectManifestPath,
    [Parameter(Mandatory)][string]$ReleaseManifestPath,
    [string]$BaselineReleaseManifestPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$specificationRoot = Split-Path -Parent $PSScriptRoot

function Resolve-InputFile([string]$Value) {
    $item = Get-Item -LiteralPath $Value -ErrorAction Stop
    if ($item.PSIsContainer) { throw "需要文件路径：$Value" }
    return $item.FullName
}

$projectPath = Resolve-InputFile $ProjectManifestPath
$releasePath = Resolve-InputFile $ReleaseManifestPath
$baselinePath = if ($BaselineReleaseManifestPath) { Resolve-InputFile $BaselineReleaseManifestPath } else { $null }
$pathsToValidate = @($projectPath, $releasePath)
if ($baselinePath) { $pathsToValidate += $baselinePath }
& (Join-Path $PSScriptRoot 'Test-OpsManifest.ps1') $pathsToValidate
if ($LASTEXITCODE -ne 0) { throw '输入声明未通过契约校验' }

$runId = [Guid]::NewGuid().ToString('N')
$outputRoot = if ($OutputDirectory) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    Join-Path $specificationRoot "artifacts/release-rehearsals/$runId"
}
if (Test-Path -LiteralPath $outputRoot) { throw "演练输出目录必须不存在，拒绝覆盖：$outputRoot" }
$outputParent = Split-Path -Parent $outputRoot
[IO.Directory]::CreateDirectory($outputParent) | Out-Null
$requestPath = Join-Path $outputParent "$runId.request.tmp"
$request = [ordered]@{
    projectManifestPath = $projectPath
    releaseManifestPath = $releasePath
    baselineReleaseManifestPath = $baselinePath
    outputDirectory = $outputRoot
}
$request | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $requestPath -Encoding utf8
$previousInput = [Environment]::GetEnvironmentVariable('COMPANYOPS_RELEASE_REHEARSAL_INPUT', 'Process')
try {
    $env:COMPANYOPS_RELEASE_REHEARSAL_INPUT = $requestPath
    Write-Host '隔离发布事务演练：真实 ZIP / 部署引擎 / SQLite，假服务适配器与假健康探针。'
    Write-Host '不会启动业务进程、注册服务、连接 Agent 或操作本机业务端口。'
    $testProject = Join-Path $specificationRoot 'tests/Ops.Agent.Tests/Ops.Agent.Tests.csproj'
    & dotnet test $testProject -c Release --no-restore `
        --filter 'FullyQualifiedName~ProjectReleaseRehearsalTests' `
        --logger 'console;verbosity=minimal' `
        --logger 'trx;LogFileName=rehearsal.trx' `
        --results-directory (Join-Path $outputParent "$runId-test-results") `
        --blame-hang-timeout 5m
    $testExitCode = $LASTEXITCODE
    $reportPath = Join-Path $outputRoot 'rehearsal-result.json'
    if ($testExitCode -ne 0) { throw "演练失败（退出码 $testExitCode）；已生成的报告位于 $reportPath" }
    if (!(Test-Path -LiteralPath $reportPath)) { throw '测试未生成演练报告，不能判定通过' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.succeeded -ne $true -or $report.realHostOperations -ne $false) { throw '演练报告未通过结果门禁' }
    Write-Host "隔离演练通过：$reportPath"
    if ($report.derivedBaselineUsesCandidatePayload) {
        Write-Host '基线由同一候选载荷衍生，仅证明部署事务；不代表两个业务版本或数据库迁移验收。'
    }
} finally {
    [Environment]::SetEnvironmentVariable('COMPANYOPS_RELEASE_REHEARSAL_INPUT', $previousInput, 'Process')
    if (Test-Path -LiteralPath $requestPath) { Remove-Item -LiteralPath $requestPath }
}
