[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')) + [System.IO.Path]::DirectorySeparatorChar
if (-not $resolvedOutput.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw '输出目录必须位于本仓库 artifacts 下，避免覆盖运行目录。'
}

function Invoke-Checked {
    param([scriptblock]$Command, [string]$Description)
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description 失败，退出码 $LASTEXITCODE"
    }
}

$clientSource = Join-Path $repositoryRoot 'src\Ops.Console\ClientApp'
$frontendBuildRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'CompanyOps.FrontendBuild.' + [Guid]::NewGuid().ToString('N'))
$clientDirectory = Join-Path $frontendBuildRoot 'ClientApp'
$builtWebRoot = Join-Path $frontendBuildRoot 'wwwroot'

New-Item -ItemType Directory -Path $clientDirectory | Out-Null
foreach ($fileName in @('package.json', 'package-lock.json', 'tsconfig.json', 'vite.config.ts', 'index.html')) {
    Copy-Item -LiteralPath (Join-Path $clientSource $fileName) -Destination $clientDirectory
}
foreach ($directoryName in @('src', 'public')) {
    $source = Join-Path $clientSource $directoryName
    if (Test-Path -LiteralPath $source -PathType Container) {
        Copy-Item -LiteralPath $source -Destination $clientDirectory -Recurse
    }
}

Push-Location $clientDirectory
try {
    Invoke-Checked { npm ci --ignore-scripts } '前端依赖还原'
    Invoke-Checked { npm run build } '前端构建'
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath (Join-Path $builtWebRoot 'index.html') -PathType Leaf)) {
    throw '前端构建未生成隔离的 wwwroot。'
}

$projects = [ordered]@{
    Agent = 'src\Ops.Agent\Ops.Agent.csproj'
    Console = 'src\Ops.Console\Ops.Console.csproj'
    Pm2Bridge = 'src\Ops.Pm2Bridge\Ops.Pm2Bridge.csproj'
    SessionAgent = 'src\Ops.SessionAgent\Ops.SessionAgent.csproj'
    Cli = 'src\Ops.Cli\Ops.Cli.csproj'
}

foreach ($entry in $projects.GetEnumerator()) {
    $destination = Join-Path $resolvedOutput $entry.Key
    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Push-Location $repositoryRoot
    try {
        Invoke-Checked {
            $arguments = @(
                'publish',
                $entry.Value,
                '-c', $Configuration,
                '--no-restore',
                '-o', $destination
            )
            if ($SelfContained) {
                $arguments += @('-r', 'win-x64', '--self-contained', 'true')
            }
            & dotnet @arguments
        } "发布 $($entry.Key)"
    }
    finally {
        Pop-Location
    }
}

$consoleWebRoot = Join-Path $resolvedOutput 'Console\wwwroot'
if (Test-Path -LiteralPath $consoleWebRoot) {
    Remove-Item -LiteralPath $consoleWebRoot -Recurse -Force
}
Copy-Item -LiteralPath $builtWebRoot -Destination $consoleWebRoot -Recurse

try {
    Remove-Item -LiteralPath $frontendBuildRoot -Recurse -Force -ErrorAction Stop
}
catch {
    Write-Warning "隔离前端构建临时目录将在系统清理时移除：$frontendBuildRoot"
}

Write-Host "发布完成：$resolvedOutput"
Write-Host '仅生成文件，未注册服务、任务、端口或防火墙规则。'
