[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
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

$clientDirectory = Join-Path $repositoryRoot 'src\Ops.Console\ClientApp'
Push-Location $clientDirectory
try {
    Invoke-Checked { npm ci --ignore-scripts } '前端依赖还原'
    Invoke-Checked { npm run build } '前端构建'
}
finally {
    Pop-Location
}

$projects = [ordered]@{
    Agent = 'src\Ops.Agent\Ops.Agent.csproj'
    Console = 'src\Ops.Console\Ops.Console.csproj'
    Pm2Bridge = 'src\Ops.Pm2Bridge\Ops.Pm2Bridge.csproj'
    Cli = 'src\Ops.Cli\Ops.Cli.csproj'
}

foreach ($entry in $projects.GetEnumerator()) {
    $destination = Join-Path $resolvedOutput $entry.Key
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Push-Location $repositoryRoot
    try {
        Invoke-Checked {
            dotnet publish $entry.Value -c $Configuration --no-restore -o $destination
        } "发布 $($entry.Key)"
    }
    finally {
        Pop-Location
    }
}

Write-Host "发布完成：$resolvedOutput"
Write-Host '仅生成文件，未注册服务、任务、端口或防火墙规则。'
