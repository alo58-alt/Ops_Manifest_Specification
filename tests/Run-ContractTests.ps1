[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$validator = Join-Path $repoRoot 'tools\Test-OpsManifest.ps1'
$onboardingValidator = Join-Path $repoRoot 'tools\Test-ProjectOnboarding.ps1'
$releaseBuilder = Join-Path $repoRoot 'tools\New-ProjectRelease.ps1'
$schemaRoot = Join-Path $repoRoot 'spec\v1\schemas'
$validRoot = Join-Path $repoRoot 'examples\valid'
$invalidRoot = Join-Path $repoRoot 'examples\invalid'
$onboardingTemplates = Join-Path $repoRoot 'templates\project-onboarding'
$validOnboardingProject = Join-Path $repoRoot 'tests\fixtures\project-onboarding\l1-valid'
$incompleteOnboardingTemplate = Join-Path $repoRoot 'templates\project-onboarding\windows-service'
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source

$passed = 0
$failed = 0

function Invoke-TestCase {
    param(
        [string]$Name,
        [scriptblock]$Body
    )

    try {
        & $Body
        $script:passed++
        Write-Host "[PASS] $Name" -ForegroundColor Green
    } catch {
        $script:failed++
        Write-Host "[FAIL] $Name" -ForegroundColor Red
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    }
}

Invoke-TestCase -Name '五个 Schema 文件均为有效 JSON' -Body {
    $schemaFiles = @(Get-ChildItem -LiteralPath $schemaRoot -File -Filter '*.schema.json')
    if ($schemaFiles.Count -ne 5) {
        throw "预期 5 个 Schema，实际为 $($schemaFiles.Count)"
    }
    foreach ($schemaFile in $schemaFiles) {
        $null = Get-Content -LiteralPath $schemaFile.FullName -Raw -Encoding utf8 |
            ConvertFrom-Json
    }
}

$validFiles = @(Get-ChildItem -LiteralPath $validRoot -File -Filter '*.json' | Sort-Object Name)
foreach ($file in $validFiles) {
    Invoke-TestCase -Name "有效示例通过：$($file.Name)" -Body {
        $output = @(& $pwsh -NoProfile -File $validator $file.FullName 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw ($output -join [Environment]::NewLine)
        }
    }
}

$invalidFiles = @(Get-ChildItem -LiteralPath $invalidRoot -File -Filter '*.json' | Sort-Object Name)
foreach ($file in $invalidFiles) {
    Invoke-TestCase -Name "无效示例失败关闭：$($file.Name)" -Body {
        $output = @(& $pwsh -NoProfile -File $validator $file.FullName 2>&1)
        if ($LASTEXITCODE -eq 0) {
            throw "校验器错误地接受了无效示例：$($file.FullName)"
        }
    }
}

Invoke-TestCase -Name '批量校验全部有效示例' -Body {
    $output = @(& $pwsh -NoProfile -File $validator $validRoot 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw ($output -join [Environment]::NewLine)
    }
}

Invoke-TestCase -Name 'L1 项目接入材料通过' -Body {
    $output = @(
        & $pwsh -NoProfile -File $onboardingValidator `
            -ProjectRoot $validOnboardingProject `
            -Level L1 2>&1
    )
    if ($LASTEXITCODE -ne 0) {
        throw ($output -join [Environment]::NewLine)
    }
}

Invoke-TestCase -Name '六种项目接入模板均通过 ProjectManifest 契约' -Body {
    $templateManifests = @(
        Get-ChildItem -LiteralPath $onboardingTemplates `
            -Filter 'project-manifest.json' `
            -File `
            -Recurse
    )
    if ($templateManifests.Count -ne 6) {
        throw "预期 6 种项目接入模板，实际为 $($templateManifests.Count)"
    }

    foreach ($templateManifest in $templateManifests) {
        $output = @(& $pwsh -NoProfile -File $validator $templateManifest.FullName 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw ($output -join [Environment]::NewLine)
        }
    }
}

Invoke-TestCase -Name 'L3 发布材料哈希与 ZIP 载荷通过' -Body {
    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CompanyOps-Onboarding-" + [guid]::NewGuid().ToString('N'))
    try {
        $artifactRoot = Join-Path $testRoot 'artifacts'
        $payloadRoot = Join-Path $testRoot 'payload'
        $payloadApiRoot = Join-Path $payloadRoot 'api'
        New-Item -ItemType Directory -Force -Path $artifactRoot, $payloadApiRoot | Out-Null

        $payloadFile = Join-Path $payloadApiRoot 'Onboarding.Api.exe'
        Set-Content -LiteralPath $payloadFile -Value 'contract fixture payload' -Encoding utf8
        $artifactPath = Join-Path $artifactRoot 'onboarding-fixture-1.0.0.zip'
        Compress-Archive -Path (Join-Path $payloadRoot '*') -DestinationPath $artifactPath

        $projectManifestPath = Join-Path $validOnboardingProject 'ops\project-manifest.json'
        $projectHash = (Get-FileHash -LiteralPath $projectManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $artifactHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $artifactSize = (Get-Item -LiteralPath $artifactPath).Length
        $releaseManifestPath = Join-Path $testRoot 'release-manifest.json'
        $releaseManifest = [ordered]@{
            '$schema' = 'https://raw.githubusercontent.com/alo58-alt/Ops_Manifest_Specification/main/spec/v1/schemas/release-manifest.schema.json'
            apiVersion = 'ops.company/v1'
            manifestKind = 'ReleaseManifest'
            metadata = [ordered]@{
                projectId = 'onboarding-fixture'
                version = '1.0.0'
                releaseId = 'onboarding-fixture-1.0.0-test'
                builtAt = '2026-08-12T00:00:00Z'
                sourceRevision = '0123456789abcdef0123456789abcdef01234567'
            }
            target = [ordered]@{
                os = 'windows'
                architecture = 'x64'
                minAgentVersion = '0.1.0'
            }
            projectManifestSha256 = $projectHash
            artifacts = @(
                [ordered]@{
                    id = 'api-package'
                    fileName = 'onboarding-fixture-1.0.0.zip'
                    mediaType = 'application/zip'
                    sha256 = $artifactHash
                    sizeBytes = $artifactSize
                }
            )
            componentPayloads = @(
                [ordered]@{
                    componentId = 'api'
                    entrypoint = 'api-main'
                    artifactId = 'api-package'
                    path = 'api/Onboarding.Api.exe'
                    workingDirectory = 'api'
                }
            )
        }
        $releaseManifest |
            ConvertTo-Json -Depth 10 |
            Set-Content -LiteralPath $releaseManifestPath -Encoding utf8

        $output = @(
            & $pwsh -NoProfile -File $onboardingValidator `
                -ProjectRoot $validOnboardingProject `
                -Level L3 `
                -ReleaseManifestPath $releaseManifestPath `
                -ArtifactDirectory $artifactRoot 2>&1
        )
        if ($LASTEXITCODE -ne 0) {
            throw ($output -join [Environment]::NewLine)
        }
    }
    finally {
        if (Test-Path -LiteralPath $testRoot) {
            $resolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
            $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
            if (-not $resolvedTestRoot.StartsWith($resolvedTempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "拒绝清理临时目录以外的路径：$resolvedTestRoot"
            }
            Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
        }
    }
}

Invoke-TestCase -Name '通用发布工具由项目配方生成 ReleaseManifest ZIP 和 SHA-256' -Body {
    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CompanyOps-ReleaseBuilder-" + [guid]::NewGuid().ToString('N'))
    try {
        $payloadRoot = Join-Path $testRoot 'payload'
        $payloadApiRoot = Join-Path $payloadRoot 'api'
        $outputRoot = Join-Path $testRoot 'output'
        New-Item -ItemType Directory -Force -Path $payloadApiRoot | Out-Null
        Set-Content -LiteralPath (Join-Path $payloadApiRoot 'Onboarding.Api.exe') -Value 'fixture' -Encoding utf8
        $recipePath = Join-Path $testRoot 'release-recipe.json'
        [ordered]@{
            apiVersion = 'ops.company/v1'
            recipeKind = 'ReleaseRecipe'
            artifact = [ordered]@{
                id = 'application-package'
                fileName = '${PROJECT_ID}-${VERSION}-win-x64.zip'
            }
            target = [ordered]@{
                architecture = 'x64'
                minAgentVersion = '0.1.0'
            }
            componentPayloads = @([ordered]@{
                componentId = 'api'
                entrypoint = 'api-main'
                path = 'api/Onboarding.Api.exe'
                workingDirectory = 'api'
                arguments = @('--data-dir', '${ROOT_DATA}')
            })
        } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $recipePath -Encoding utf8

        $output = @(
            & $pwsh -NoProfile -File $releaseBuilder `
                -ProjectManifestPath (Join-Path $validOnboardingProject 'ops\project-manifest.json') `
                -RecipePath $recipePath `
                -PayloadDirectory $payloadRoot `
                -OutputDirectory $outputRoot `
                -Version '1.2.3' `
                -ReleaseId 'onboarding-fixture-1.2.3-test' `
                -SourceRevision '0123456789abcdef0123456789abcdef01234567' 2>&1
        )
        if ($LASTEXITCODE -ne 0) {
            throw ($output -join [Environment]::NewLine)
        }
        $releasePath = Join-Path $outputRoot 'release-manifest.json'
        $artifactPath = Join-Path $outputRoot 'onboarding-fixture-1.2.3-win-x64.zip'
        if (-not (Test-Path -LiteralPath $releasePath -PathType Leaf) -or
            -not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
            throw '通用发布工具没有生成预期文件'
        }
        $release = Get-Content -LiteralPath $releasePath -Raw -Encoding utf8 | ConvertFrom-Json
        $actualHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($release.artifacts[0].sha256 -ne $actualHash -or
            [long]$release.artifacts[0].sizeBytes -ne (Get-Item -LiteralPath $artifactPath).Length) {
            throw 'ReleaseManifest 的 ZIP 哈希或大小不匹配'
        }
    }
    finally {
        if (Test-Path -LiteralPath $testRoot) {
            $resolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
            $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
            if (-not $resolvedTestRoot.StartsWith($resolvedTempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "拒绝清理临时目录以外的路径：$resolvedTestRoot"
            }
            Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
        }
    }
}

Invoke-TestCase -Name '未填写的项目接入模板失败关闭' -Body {
    $output = @(
        & $pwsh -NoProfile -File $onboardingValidator `
            -ProjectRoot $incompleteOnboardingTemplate `
            -Level L1 2>&1
    )
    if ($LASTEXITCODE -eq 0) {
        throw '项目接入校验器错误地接受了仍含占位值的模板'
    }
    if (($output -join [Environment]::NewLine) -notmatch '占位值') {
        throw '项目接入校验器拒绝了模板，但没有报告占位值原因'
    }
}

Write-Host ""
Write-Host "契约测试完成：$passed 通过，$failed 失败，共 $($passed + $failed) 项。"

if ($failed -gt 0) {
    exit 1
}

exit 0
