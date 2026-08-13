[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectRoot,

    [ValidateSet('L1', 'L2', 'L3')]
    [string]$Level = 'L1',

    [string]$ReleaseManifestPath,

    [string]$ArtifactDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$specRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestValidator = Join-Path $PSScriptRoot 'Test-OpsManifest.ps1'
$failures = [System.Collections.Generic.List[string]]::new()
$passes = [System.Collections.Generic.List[string]]::new()

function Add-Failure {
    param([string]$Message)
    $script:failures.Add($Message)
}

function Add-Pass {
    param([string]$Message)
    $script:passes.Add($Message)
}

function Test-Placeholder {
    param([string]$Value)

    return $Value -match '(?i)(change[-_ ]?me|sample-system|demo-api|example-project|请替换|待填写|TODO)'
}

function Get-JsonStrings {
    param([object]$Value)

    if ($null -eq $Value) {
        return
    }

    if ($Value -is [string]) {
        $Value
        return
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [System.Management.Automation.PSObject]) {
        foreach ($item in $Value) {
            Get-JsonStrings -Value $item
        }
        return
    }

    foreach ($property in $Value.PSObject.Properties) {
        Get-JsonStrings -Value $property.Value
    }
}

function Invoke-ManifestValidation {
    param([string]$Path)

    $pwshPath = (Get-Process -Id $PID).Path
    $validationOutput = @(
        & $pwshPath -NoProfile -File $manifestValidator -Path $Path -Quiet 2>&1
    )
    if ($LASTEXITCODE -ne 0) {
        Add-Failure "Manifest 契约校验失败：$Path`n$($validationOutput -join [Environment]::NewLine)"
        return $false
    }

    Add-Pass "Manifest 契约校验通过：$Path"
    return $true
}

function Test-ZipSafetyAndPayloads {
    param(
        [string]$ZipPath,
        [object[]]$Payloads
    )

    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
        try {
            $entryNames = [System.Collections.Generic.HashSet[string]]::new(
                [System.StringComparer]::OrdinalIgnoreCase
            )
            foreach ($entry in $archive.Entries) {
                $name = $entry.FullName.Replace('\', '/')
                $segments = @($name.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries))
                if ([string]::IsNullOrWhiteSpace($name) -or
                    $name.StartsWith('/') -or
                    $name -match '^[A-Za-z]:' -or
                    $segments -contains '..') {
                    Add-Failure "ZIP 包含不安全路径：$ZipPath -> $name"
                    continue
                }

                if (-not $entryNames.Add($name)) {
                    Add-Failure "ZIP 包含大小写不敏感的重复路径：$ZipPath -> $name"
                }
            }

            foreach ($payload in $Payloads) {
                $payloadPath = ([string]$payload.path).Replace('\', '/').Trim('/')
                $hasPayload = $entryNames.Contains($payloadPath) -or
                    @($entryNames | Where-Object { $_.StartsWith("$payloadPath/", [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
                if (-not $hasPayload) {
                    Add-Failure "组件 $($payload.componentId) 的载荷路径不在 ZIP 中：$payloadPath"
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    catch {
        Add-Failure "无法安全读取 ZIP：$ZipPath；$($_.Exception.Message)"
    }
}

try {
    if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) {
        throw "项目根目录不存在：$ProjectRoot"
    }
    $resolvedProjectRoot = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ProjectRoot).Path)
}
catch {
    Write-Host "[FAIL] $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

$opsDirectory = Join-Path $resolvedProjectRoot 'ops'
$projectManifestPath = Join-Path $opsDirectory 'project-manifest.json'
$opsReadmePath = Join-Path $opsDirectory 'README.md'

if (-not (Test-Path -LiteralPath $opsDirectory -PathType Container)) {
    Add-Failure "缺少项目运维目录：$opsDirectory"
}

if (-not (Test-Path -LiteralPath $projectManifestPath -PathType Leaf)) {
    Add-Failure "缺少：$projectManifestPath"
}

if (-not (Test-Path -LiteralPath $opsReadmePath -PathType Leaf)) {
    Add-Failure "缺少：$opsReadmePath"
}

$projectManifest = $null
if (Test-Path -LiteralPath $projectManifestPath -PathType Leaf) {
    if (Invoke-ManifestValidation -Path $projectManifestPath) {
        try {
            $projectManifest = Get-Content -LiteralPath $projectManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
        }
        catch {
            Add-Failure "ProjectManifest 无法解析：$($_.Exception.Message)"
        }
    }
}

if ($null -ne $projectManifest) {
    $allStrings = @(Get-JsonStrings -Value $projectManifest)
    foreach ($value in $allStrings) {
        if (Test-Placeholder -Value $value) {
            Add-Failure "ProjectManifest 仍包含占位值：$value"
        }
        if ($value -match '^[A-Za-z]:\\' -or $value -match '^\\\\') {
            Add-Failure "ProjectManifest 不得包含服务器绝对路径：$value"
        }
    }

    foreach ($component in @($projectManifest.components)) {
        if (@($component.health).Count -eq 0) {
            Add-Failure "组件 $($component.id) 没有健康探针；L1 接入要求每个组件至少一个探针"
        }
    }

    if ($failures.Count -eq 0) {
        Add-Pass 'ProjectManifest 无占位符、服务器绝对路径或无探针组件'
    }
}

if (Test-Path -LiteralPath $opsReadmePath -PathType Leaf) {
    $readme = Get-Content -LiteralPath $opsReadmePath -Raw -Encoding utf8
    if ($readme.Length -lt 300) {
        Add-Failure 'ops\README.md 内容过短，无法说明运行、健康、数据和接入事实'
    }

    foreach ($keyword in @('部署', '组件', '健康', '端口', '数据', '日志', '配置', 'Secret', '验证', '接入')) {
        if ($readme -notmatch [regex]::Escape($keyword)) {
            Add-Failure "ops\README.md 缺少主题：$keyword"
        }
    }

    if ($readme -match '(?im)(?:^|[\s`''"])[A-Za-z]:\\(?:[^<>:"|?*\r\n]+)') {
        Add-Failure 'ops\README.md 包含具体盘符绝对路径；请改写成项目相对路径或 <ProjectRoot> 占位符'
    }

    if ($readme -match '(?i)(password|passwd|token|secret|cookie)\s*[:=]\s*[^<\s][^\r\n]*') {
        Add-Failure 'ops\README.md 疑似包含 Secret 明文赋值；只允许写配置键或 secretRef 说明'
    }
}

if (Test-Path -LiteralPath $opsDirectory -PathType Container) {
    $forbiddenKinds = @('EnvironmentBinding', 'InstalledState', 'PortRegistry')
    foreach ($jsonFile in @(Get-ChildItem -LiteralPath $opsDirectory -Filter '*.json' -File -Recurse)) {
        try {
            $document = Get-Content -LiteralPath $jsonFile.FullName -Raw -Encoding utf8 | ConvertFrom-Json
            if (($document.PSObject.Properties.Name -contains 'manifestKind') -and
                $document.manifestKind -in $forbiddenKinds) {
                Add-Failure "项目仓库不得包含主机状态 $($document.manifestKind)：$($jsonFile.FullName)"
            }
        }
        catch {
            Add-Failure "ops 目录包含无法解析的 JSON：$($jsonFile.FullName)"
        }
    }
}

if ($Level -eq 'L3') {
    $gitSource = if ($null -ne $projectManifest.update -and
        $projectManifest.update.PSObject.Properties.Name -contains 'source') {
        $projectManifest.update.source
    }
    else {
        $null
    }
    $usesGitFastForward = $null -ne $gitSource -and $gitSource.kind -eq 'gitFastForward'
    if ($usesGitFastForward) {
        if ($projectManifest.update.rollbackOnFailure -ne $true) {
            Add-Failure 'L3 Git 更新要求 update.rollbackOnFailure=true'
        }
        if ([string]$gitSource.remoteUrl -notmatch '^https://[^@/]+/.+$') {
            Add-Failure 'L3 Git 更新 remoteUrl 必须是无凭据 HTTPS URL'
        }
        if ($failures.Count -eq 0) {
            Add-Pass 'L3 Git 快进来源、分支和失败回滚声明完整'
        }
    }

    if (-not $usesGitFastForward) {
    if ([string]::IsNullOrWhiteSpace($ReleaseManifestPath) -or
        -not (Test-Path -LiteralPath $ReleaseManifestPath -PathType Leaf)) {
        Add-Failure 'L3 制品发布必须提供存在的 -ReleaseManifestPath'
    }
    if ([string]::IsNullOrWhiteSpace($ArtifactDirectory) -or
        -not (Test-Path -LiteralPath $ArtifactDirectory -PathType Container)) {
        Add-Failure 'L3 制品发布必须提供存在的 -ArtifactDirectory'
    }

    if ((Test-Path -LiteralPath $ReleaseManifestPath -PathType Leaf) -and
        (Test-Path -LiteralPath $ArtifactDirectory -PathType Container) -and
        (Invoke-ManifestValidation -Path $ReleaseManifestPath)) {
        try {
            $release = Get-Content -LiteralPath $ReleaseManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
            if ($null -ne $projectManifest -and $release.metadata.projectId -ne $projectManifest.metadata.id) {
                Add-Failure "ReleaseManifest projectId 与 ProjectManifest 不一致：$($release.metadata.projectId) / $($projectManifest.metadata.id)"
            }

            $projectHash = (Get-FileHash -LiteralPath $projectManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($release.projectManifestSha256 -cne $projectHash) {
                Add-Failure "projectManifestSha256 不正确；预期 $projectHash，实际 $($release.projectManifestSha256)"
            }

            $artifactsById = @{}
            foreach ($artifact in @($release.artifacts)) {
                $artifactsById[[string]$artifact.id] = $artifact
                $artifactPath = Join-Path ([System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ArtifactDirectory).Path)) ([string]$artifact.fileName)
                if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
                    Add-Failure "制品不存在：$artifactPath"
                    continue
                }

                $file = Get-Item -LiteralPath $artifactPath
                if ($file.Length -ne [long]$artifact.sizeBytes) {
                    Add-Failure "制品大小不符：$artifactPath；预期 $($artifact.sizeBytes)，实际 $($file.Length)"
                }
                $hash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($hash -cne [string]$artifact.sha256) {
                    Add-Failure "制品 SHA-256 不符：$artifactPath"
                }
            }

            if ($null -ne $projectManifest) {
                $componentsById = @{}
                foreach ($component in @($projectManifest.components)) {
                    $componentsById[[string]$component.id] = $component
                }

                foreach ($payload in @($release.componentPayloads)) {
                    if (-not $componentsById.ContainsKey([string]$payload.componentId)) {
                        Add-Failure "ReleaseManifest 引用了不存在的组件：$($payload.componentId)"
                        continue
                    }
                    if ($componentsById[[string]$payload.componentId].entrypoint -ne $payload.entrypoint) {
                        Add-Failure "组件 $($payload.componentId) 的 entrypoint 与 ProjectManifest 不一致"
                    }
                    if ($artifactsById.ContainsKey([string]$payload.artifactId)) {
                        $artifactPath = Join-Path $ArtifactDirectory ([string]$artifactsById[[string]$payload.artifactId].fileName)
                        if (Test-Path -LiteralPath $artifactPath -PathType Leaf) {
                            Test-ZipSafetyAndPayloads -ZipPath $artifactPath -Payloads @($payload)
                        }
                    }
                }
            }
        }
        catch {
            Add-Failure "L3 交叉校验失败：$($_.Exception.Message)"
        }
    }
    }
}

if ($Level -eq 'L2' -and $failures.Count -eq 0) {
    Add-Pass 'L2 项目材料通过；服务唯一归属与控制仍须在目标服务器由 CompanyOps 验证'
}

foreach ($pass in $passes) {
    Write-Host "[PASS] $pass" -ForegroundColor Green
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Host "[FAIL] $failure" -ForegroundColor Red
    }
    Write-Host ''
    Write-Host "项目接入材料验收失败：$($failures.Count) 项不合格。" -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host "项目接入材料验收通过：$Level。" -ForegroundColor Green
Write-Host '注意：该结果不等于服务器资源、外部服务或真实业务 UAT 已通过。'
exit 0
