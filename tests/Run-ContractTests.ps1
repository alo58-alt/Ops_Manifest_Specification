[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$validator = Join-Path $repoRoot 'tools\Test-OpsManifest.ps1'
$schemaRoot = Join-Path $repoRoot 'spec\v1\schemas'
$validRoot = Join-Path $repoRoot 'examples\valid'
$invalidRoot = Join-Path $repoRoot 'examples\invalid'
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
            ConvertFrom-Json -Depth 100
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

Write-Host ""
Write-Host "契约测试完成：$passed 通过，$failed 失败，共 $($passed + $failed) 项。"

if ($failed -gt 0) {
    exit 1
}

exit 0
