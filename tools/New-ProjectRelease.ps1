[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ProjectManifestPath,

    [Parameter(Mandatory)]
    [string]$RecipePath,

    [Parameter(Mandatory)]
    [string]$PayloadDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}$')]
    [string]$ReleaseId,

    [ValidatePattern('^[a-fA-F0-9]{7,64}$')]
    [string]$SourceRevision
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-RequiredFile([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label 不存在：$Path"
    }
    return [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
}

function Resolve-RequiredDirectory([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label 不存在：$Path"
    }
    return [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
}

function Resolve-PayloadPath([string]$Root, [string]$RelativePath, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [System.IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "$Label 必须是 PayloadDirectory 内的安全相对路径：$RelativePath"
    }
    $resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $resolved = [System.IO.Path]::GetFullPath((Join-Path $Root ($RelativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)))
    if (-not $resolved.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label 发生路径逃逸：$RelativePath"
    }
    return $resolved
}

$projectManifestPath = Resolve-RequiredFile $ProjectManifestPath 'ProjectManifest'
$recipePath = Resolve-RequiredFile $RecipePath '发布配方'
$payloadDirectory = Resolve-RequiredDirectory $PayloadDirectory '载荷目录'
$outputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$projectManifest = Get-Content -LiteralPath $projectManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
$recipe = Get-Content -LiteralPath $recipePath -Raw -Encoding utf8 | ConvertFrom-Json

if ($projectManifest.apiVersion -ne 'ops.company/v1' -or $projectManifest.manifestKind -ne 'ProjectManifest') {
    throw 'ProjectManifest 契约无效'
}
$projectId = [string]$projectManifest.metadata.id
if ([string]::IsNullOrWhiteSpace($projectId) -or $projectId -notmatch '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$') {
    throw 'ProjectManifest metadata.id 无效'
}
if ([string]$recipe.apiVersion -ne 'ops.company/v1' -or [string]$recipe.recipeKind -ne 'ReleaseRecipe') {
    throw '发布配方必须声明 apiVersion=ops.company/v1 和 recipeKind=ReleaseRecipe'
}

$artifactId = [string]$recipe.artifact.id
$fileNameTemplate = [string]$recipe.artifact.fileName
if ($artifactId -notmatch '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$') {
    throw '发布配方 artifact.id 无效'
}
$artifactFileName = $fileNameTemplate.Replace('${PROJECT_ID}', $projectId).Replace('${VERSION}', $Version)
if ([string]::IsNullOrWhiteSpace($artifactFileName) -or
    $artifactFileName.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
    -not $artifactFileName.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw '发布配方生成的 artifact.fileName 必须是安全 ZIP 文件名'
}

$manifestComponentIds = @($projectManifest.components | ForEach-Object { [string]$_.id })
$manifestEntrypoints = @{}
foreach ($component in @($projectManifest.components)) {
    $manifestEntrypoints[[string]$component.id] = [string]$component.entrypoint
}
$seenComponents = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$componentPayloads = @()
foreach ($payload in @($recipe.componentPayloads)) {
    $componentId = [string]$payload.componentId
    $entrypoint = [string]$payload.entrypoint
    if ($componentId -notin $manifestComponentIds -or $manifestEntrypoints[$componentId] -ne $entrypoint) {
        throw "发布配方组件或入口与 ProjectManifest 不一致：$componentId / $entrypoint"
    }
    if (-not $seenComponents.Add($componentId)) {
        throw "发布配方组件重复：$componentId"
    }
    $entrypointPath = Resolve-PayloadPath $payloadDirectory ([string]$payload.path) "组件 $componentId 入口"
    if (-not (Test-Path -LiteralPath $entrypointPath -PathType Leaf)) {
        throw "组件 $componentId 入口文件不存在：$entrypointPath"
    }
    $hasWorkingDirectory = $payload.PSObject.Properties.Name -contains 'workingDirectory'
    $workingDirectoryValue = if ($hasWorkingDirectory) { [string]$payload.workingDirectory } else { $null }
    if (-not [string]::IsNullOrWhiteSpace($workingDirectoryValue)) {
        $workingDirectory = Resolve-PayloadPath $payloadDirectory $workingDirectoryValue "组件 $componentId 工作目录"
        if (-not (Test-Path -LiteralPath $workingDirectory -PathType Container)) {
            throw "组件 $componentId 工作目录不存在：$workingDirectory"
        }
    }
    $argumentValues = if ($payload.PSObject.Properties.Name -contains 'arguments') {
        @($payload.arguments | ForEach-Object { [string]$_ })
    } else {
        @()
    }
    $componentPayload = [ordered]@{
        componentId = $componentId
        entrypoint = $entrypoint
        artifactId = $artifactId
        path = [string]$payload.path
        arguments = $argumentValues
    }
    if (-not [string]::IsNullOrWhiteSpace($workingDirectoryValue)) {
        $componentPayload['workingDirectory'] = $workingDirectoryValue
    }
    $componentPayloads += $componentPayload
}
if ($componentPayloads.Count -ne $manifestComponentIds.Count) {
    throw '发布配方必须为 ProjectManifest 的每个组件提供且只提供一个载荷'
}

if (Test-Path -LiteralPath $outputDirectory) {
    if (@(Get-ChildItem -LiteralPath $outputDirectory -Force).Count -gt 0) {
        throw "输出目录必须为空以保护不可变版本：$outputDirectory"
    }
} else {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

$artifactPath = Join-Path $outputDirectory $artifactFileName
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $payloadDirectory,
    $artifactPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
    throw "ZIP 制品生成失败：$artifactPath"
}
$artifact = Get-Item -LiteralPath $artifactPath
$artifactHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
$projectHash = (Get-FileHash -LiteralPath $projectManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()

$metadata = [ordered]@{
    projectId = $projectId
    version = $Version
    releaseId = $ReleaseId
    builtAt = [DateTimeOffset]::UtcNow.ToString('o')
}
if (-not [string]::IsNullOrWhiteSpace($SourceRevision)) {
    $metadata['sourceRevision'] = $SourceRevision.ToLowerInvariant()
}
$releaseManifest = [ordered]@{
    '$schema' = 'https://raw.githubusercontent.com/alo58-alt/Ops_Manifest_Specification/main/spec/v1/schemas/release-manifest.schema.json'
    apiVersion = 'ops.company/v1'
    manifestKind = 'ReleaseManifest'
    metadata = $metadata
    target = [ordered]@{
        os = 'windows'
        architecture = [string]$recipe.target.architecture
        minAgentVersion = [string]$recipe.target.minAgentVersion
    }
    projectManifestSha256 = $projectHash
    artifacts = @([ordered]@{
        id = $artifactId
        fileName = $artifactFileName
        mediaType = 'application/zip'
        sha256 = $artifactHash
        sizeBytes = $artifact.Length
    })
    componentPayloads = $componentPayloads
}
$releaseManifestPath = Join-Path $outputDirectory 'release-manifest.json'
$json = $releaseManifest | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText(
    $releaseManifestPath,
    $json + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

$validator = Join-Path $PSScriptRoot 'Test-OpsManifest.ps1'
& $validator $releaseManifestPath
if ($LASTEXITCODE -ne 0) {
    throw "生成后的 ReleaseManifest 校验失败：$releaseManifestPath"
}

[pscustomobject]@{
    ProjectId = $projectId
    Version = $Version
    ReleaseId = $ReleaseId
    ReleaseManifestPath = $releaseManifestPath
    ArtifactPath = $artifactPath
    ArtifactSha256 = $artifactHash
    ArtifactSizeBytes = $artifact.Length
}
