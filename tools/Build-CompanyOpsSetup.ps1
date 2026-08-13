[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $repositoryRoot 'OpsManifest.slnx'
$nugetConfig = Join-Path $repositoryRoot 'NuGet.config'
$publishRoot = Join-Path $repositoryRoot 'artifacts\publish'
$setupRoot = Join-Path $repositoryRoot 'artifacts\setup'
$packageRoot = Join-Path $setupRoot 'package\CompanyOps-Offline'
$payloadRoot = Join-Path $packageRoot 'Payload'
$payloadManifest = Join-Path $packageRoot 'Payload.sha256.json'
$setupPublishRoot = Join-Path $packageRoot 'Setup'
$setupIntermediateRoot = Join-Path $setupRoot 'obj\'
# Keep this relative so each project (the test and its project reference) gets
# a distinct intermediate directory. An absolute shared path causes generated
# assembly attributes from both projects to collide.
$setupTestIntermediateRoot = 'obj.setup-upgrade-tests\'
$outputRoot = Join-Path $repositoryRoot 'output'
$packageOutput = Join-Path $outputRoot 'CompanyOps-Offline-win-x64.zip'
$hashOutput = Join-Path $outputRoot 'CompanyOps-Offline-win-x64.sha256.txt'

function Invoke-Checked {
    param(
        [scriptblock]$Command,
        [string]$Description
    )

    Write-Host "[$Description]" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description 失败，退出码 $LASTEXITCODE"
    }
}

function Assert-Command {
    param([string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "构建电脑缺少 $Name。请安装项目要求的开发工具后重试。"
    }
}

Assert-Command 'dotnet'
Assert-Command 'node'
Assert-Command 'npm'

Push-Location $repositoryRoot
try {
    Invoke-Checked {
        dotnet restore $solution --configfile $nugetConfig -r win-x64
    } '还原 .NET 依赖'

    if (-not $SkipTests) {
        Invoke-Checked {
            dotnet test '.\tests\Ops.Agent.Tests\Ops.Agent.Tests.csproj' `
                -c $Configuration `
                --no-restore
            if ($LASTEXITCODE -ne 0) { return }
            dotnet test '.\tests\Ops.Console.Tests\Ops.Console.Tests.csproj' `
                -c $Configuration `
                --no-restore
            if ($LASTEXITCODE -ne 0) { return }
            dotnet restore '.\tests\Ops.Setup.Tests\Ops.Setup.Tests.csproj' `
                --configfile $nugetConfig `
                -r win-x64 `
                -p:BaseIntermediateOutputPath=$setupTestIntermediateRoot
            if ($LASTEXITCODE -ne 0) { return }
            dotnet test '.\tests\Ops.Setup.Tests\Ops.Setup.Tests.csproj' `
                -c $Configuration `
                --no-restore `
                -p:BaseIntermediateOutputPath=$setupTestIntermediateRoot
        } '运行 .NET 自动测试'

        Invoke-Checked {
            $pwsh = (Get-Process -Id $PID).Path
            $contractTests = Join-Path $repositoryRoot 'tests\Run-ContractTests.ps1'
            $contractProcess = Start-Process `
                -FilePath $pwsh `
                -ArgumentList @('-NoProfile', '-File', $contractTests) `
                -NoNewWindow `
                -Wait `
                -PassThru
            if ($contractProcess.ExitCode -ne 0) {
                throw "运维声明契约测试失败，退出码 $($contractProcess.ExitCode)"
            }
            $global:LASTEXITCODE = 0
        } '运行运维声明契约测试'
    }

    Invoke-Checked {
        & (Join-Path $repositoryRoot 'tools\Publish-OpsPlatform.ps1') `
            -OutputDirectory $publishRoot `
            -Configuration $Configuration `
            -SelfContained
    } '生成 CompanyOps 程序文件'

    foreach ($component in @('Agent', 'Console', 'Cli', 'Pm2Bridge', 'SessionAgent')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $component) -PathType Container)) {
            throw "发布结果不完整，缺少 $component。"
        }
    }

    if (Test-Path -LiteralPath $setupRoot) {
        Remove-Item -LiteralPath $setupRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
    foreach ($component in @('Agent', 'Console', 'Cli', 'Pm2Bridge', 'SessionAgent')) {
        Copy-Item `
            -LiteralPath (Join-Path $publishRoot $component) `
            -Destination (Join-Path $payloadRoot $component) `
            -Recurse
    }

    $payloadFiles = @(
        Get-ChildItem -LiteralPath $payloadRoot -File -Recurse |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    Path = [System.IO.Path]::GetRelativePath($payloadRoot, $_.FullName).Replace('\', '/')
                    Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                }
            }
    )
    [ordered]@{ Version = 1; Files = $payloadFiles } |
        ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $payloadManifest -Encoding utf8

    Invoke-Checked {
        dotnet publish '.\src\Ops.Setup\Ops.Setup.csproj' `
            -c $Configuration `
            -r win-x64 `
            --self-contained true `
            -p:BaseIntermediateOutputPath=$setupIntermediateRoot `
            -o $setupPublishRoot
    } '生成透明离线安装程序'

    $publishedSetup = Join-Path $setupPublishRoot 'CompanyOps-Setup.exe'
    if (-not (Test-Path -LiteralPath $publishedSetup -PathType Leaf)) {
        throw '没有生成 CompanyOps-Setup.exe。'
    }

    $setupAssembly = Join-Path $setupPublishRoot 'CompanyOps-Setup.dll'
    Invoke-Checked {
        dotnet $setupAssembly --verify-payload $packageRoot
    } '验证安装包逐文件哈希'

    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    foreach ($obsoleteOutput in @(
        (Join-Path $outputRoot 'CompanyOps-Setup.exe'),
        (Join-Path $outputRoot 'CompanyOps-Setup.sha256.txt')
    )) {
        if (Test-Path -LiteralPath $obsoleteOutput) {
            Remove-Item -LiteralPath $obsoleteOutput -Force
        }
    }
    if (Test-Path -LiteralPath $packageOutput) {
        Remove-Item -LiteralPath $packageOutput -Force
    }
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $packageOutput -CompressionLevel Optimal
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $packageOutput
    "{0}  {1}" -f $hash.Hash, (Split-Path -Leaf $packageOutput) |
        Set-Content -LiteralPath $hashOutput -Encoding ascii

    Write-Host ''
    Write-Host '透明离线安装包生成成功。' -ForegroundColor Green
    Write-Host "把 ZIP 复制到服务器、完整解压后双击 Setup\CompanyOps-Setup.exe：$packageOutput" -ForegroundColor Green
    Write-Host "SHA-256 已保存：$hashOutput"
}
finally {
    Pop-Location
}
