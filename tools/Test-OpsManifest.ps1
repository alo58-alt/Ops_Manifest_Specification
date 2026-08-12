[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]]$Path,

    [switch]$Recurse,

    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$schemaRoot = Join-Path $repoRoot 'spec\v1\schemas'
$schemaByKind = @{
    ProjectManifest    = Join-Path $schemaRoot 'project-manifest.schema.json'
    ReleaseManifest    = Join-Path $schemaRoot 'release-manifest.schema.json'
    EnvironmentBinding = Join-Path $schemaRoot 'environment-binding.schema.json'
    InstalledState     = Join-Path $schemaRoot 'installed-state.schema.json'
    PortRegistry       = Join-Path $schemaRoot 'port-registry.schema.json'
}

function Test-HasProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object]$InputObject,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    return $InputObject.PSObject.Properties.Name -contains $Name
}

function Add-DuplicateErrors {
    param(
        [object[]]$Items,
        [string]$PropertyName,
        [string]$Label,
        [System.Collections.Generic.List[string]]$Errors
    )

    $values = @(
        foreach ($item in $Items) {
            if (Test-HasProperty -InputObject $item -Name $PropertyName) {
                [string]$item.$PropertyName
            }
        }
    )

    foreach ($group in @($values | Group-Object | Where-Object Count -GT 1)) {
        $Errors.Add("$Label 重复：$($group.Name)")
    }
}

function Add-PortConflictErrors {
    param(
        [object[]]$Ports,
        [System.Collections.Generic.List[string]]$Errors,
        [string]$Label
    )

    for ($leftIndex = 0; $leftIndex -lt $Ports.Count; $leftIndex++) {
        $left = $Ports[$leftIndex]
        for ($rightIndex = $leftIndex + 1; $rightIndex -lt $Ports.Count; $rightIndex++) {
            $right = $Ports[$rightIndex]
            if ($left.protocol -ne $right.protocol -or $left.port -ne $right.port) {
                continue
            }

            $leftIsIpv6 = ([string]$left.address).Contains(':')
            $rightIsIpv6 = ([string]$right.address).Contains(':')
            if ($leftIsIpv6 -ne $rightIsIpv6) {
                continue
            }

            $wildcard = if ($leftIsIpv6) { '::' } else { '0.0.0.0' }
            if ($left.address -eq $right.address -or $left.address -eq $wildcard -or $right.address -eq $wildcard) {
                $Errors.Add(
                    "$Label 冲突：$($left.protocol) $($left.address):$($left.port) 与 $($right.address):$($right.port)"
                )
            }
        }
    }
}

function Test-ProjectManifestSemantics {
    param(
        [object]$Document,
        [System.Collections.Generic.List[string]]$Errors
    )

    $components = @($Document.components)
    Add-DuplicateErrors -Items $components -PropertyName 'id' -Label '组件 ID' -Errors $Errors

    $componentIds = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
    foreach ($component in $components) {
        [void]$componentIds.Add([string]$component.id)
    }

    $ports = if (Test-HasProperty -InputObject $Document -Name 'ports') { @($Document.ports) } else { @() }
    Add-DuplicateErrors -Items $ports -PropertyName 'id' -Label '端口请求 ID' -Errors $Errors

    $portById = @{}
    foreach ($port in $ports) {
        if (-not $portById.ContainsKey([string]$port.id)) {
            $portById[[string]$port.id] = $port
        }
        if (-not $componentIds.Contains([string]$port.componentId)) {
            $Errors.Add("端口请求 $($port.id) 引用了不存在的组件 $($port.componentId)")
        }
    }

    $configuration = if (Test-HasProperty -InputObject $Document -Name 'configuration') {
        @($Document.configuration)
    } else {
        @()
    }
    Add-DuplicateErrors -Items $configuration -PropertyName 'key' -Label '配置键' -Errors $Errors

    $dataDirectories = if (Test-HasProperty -InputObject $Document -Name 'dataDirectories') {
        @($Document.dataDirectories)
    } else {
        @()
    }
    Add-DuplicateErrors -Items $dataDirectories -PropertyName 'id' -Label '数据目录 ID' -Errors $Errors
    foreach ($dataDirectory in $dataDirectories) {
        if (-not $componentIds.Contains([string]$dataDirectory.componentId)) {
            $Errors.Add("数据目录 $($dataDirectory.id) 引用了不存在的组件 $($dataDirectory.componentId)")
        }
    }

    $pm2Names = @(
        $components |
            Where-Object kind -EQ 'pm2Legacy' |
            ForEach-Object { [string]$_.pm2.name }
    )
    foreach ($group in @($pm2Names | Group-Object | Where-Object Count -GT 1)) {
        $Errors.Add("PM2 精确名称重复：$($group.Name)")
    }

    foreach ($component in $components) {
        foreach ($dependency in @($component.dependsOn)) {
            if (-not $componentIds.Contains([string]$dependency)) {
                $Errors.Add("组件 $($component.id) 依赖不存在的组件 $dependency")
            } elseif ($dependency -eq $component.id) {
                $Errors.Add("组件 $($component.id) 不得依赖自身")
            }
        }

        foreach ($probe in @($component.health)) {
            if (Test-HasProperty -InputObject $probe -Name 'portRef') {
                $portRef = [string]$probe.portRef
                if (-not $portById.ContainsKey($portRef)) {
                    $Errors.Add("组件 $($component.id) 的健康探针引用不存在的端口 $portRef")
                } elseif ($portById[$portRef].componentId -ne $component.id) {
                    $Errors.Add("组件 $($component.id) 的健康探针引用了其他组件的端口 $portRef")
                }
            }
        }

        if ($component.kind -eq 'pm2Legacy') {
            $cwd = ([string]$component.pm2.cwd).Replace('\', '/').TrimEnd('/')
            $script = ([string]$component.pm2.script).Replace('\', '/')
            if (-not $script.StartsWith("$cwd/", [System.StringComparison]::OrdinalIgnoreCase)) {
                $Errors.Add("PM2 组件 $($component.id) 的 script 必须位于其 cwd 内")
            }
        }
    }

    if ($components.Count -eq $componentIds.Count) {
        $indegree = @{}
        $dependents = @{}
        foreach ($componentId in $componentIds) {
            $indegree[$componentId] = 0
            $dependents[$componentId] = [System.Collections.Generic.List[string]]::new()
        }

        foreach ($component in $components) {
            foreach ($dependency in @($component.dependsOn)) {
                if ($componentIds.Contains([string]$dependency) -and $dependency -ne $component.id) {
                    $indegree[[string]$component.id]++
                    $dependents[[string]$dependency].Add([string]$component.id)
                }
            }
        }

        $queue = [System.Collections.Generic.Queue[string]]::new()
        foreach ($componentId in $componentIds) {
            if ($indegree[$componentId] -eq 0) {
                $queue.Enqueue($componentId)
            }
        }

        $visited = 0
        while ($queue.Count -gt 0) {
            $current = $queue.Dequeue()
            $visited++
            foreach ($dependent in $dependents[$current]) {
                $indegree[$dependent]--
                if ($indegree[$dependent] -eq 0) {
                    $queue.Enqueue($dependent)
                }
            }
        }

        if ($visited -ne $componentIds.Count) {
            $Errors.Add('组件依赖包含循环，无法形成安全启动顺序')
        }
    }
}

function Test-ReleaseManifestSemantics {
    param(
        [object]$Document,
        [System.Collections.Generic.List[string]]$Errors
    )

    $artifacts = @($Document.artifacts)
    Add-DuplicateErrors -Items $artifacts -PropertyName 'id' -Label '制品 ID' -Errors $Errors
    Add-DuplicateErrors -Items $artifacts -PropertyName 'fileName' -Label '制品文件名' -Errors $Errors

    $artifactIds = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
    foreach ($artifact in $artifacts) {
        [void]$artifactIds.Add([string]$artifact.id)
    }

    $payloads = @($Document.componentPayloads)
    Add-DuplicateErrors -Items $payloads -PropertyName 'componentId' -Label '组件载荷 componentId' -Errors $Errors
    $payloadKeys = @($payloads | ForEach-Object { "$($_.componentId)/$($_.entrypoint)" })
    foreach ($group in @($payloadKeys | Group-Object | Where-Object Count -GT 1)) {
        $Errors.Add("组件入口载荷重复：$($group.Name)")
    }

    foreach ($payload in $payloads) {
        if (-not $artifactIds.Contains([string]$payload.artifactId)) {
            $Errors.Add(
                "组件 $($payload.componentId) 的入口 $($payload.entrypoint) 引用了不存在的制品 $($payload.artifactId)"
            )
        }
    }
}

function Test-EnvironmentBindingSemantics {
    param(
        [object]$Document,
        [System.Collections.Generic.List[string]]$Errors
    )

    $components = @($Document.componentBindings)
    Add-DuplicateErrors -Items $components -PropertyName 'componentId' -Label '组件绑定' -Errors $Errors
    Add-DuplicateErrors -Items $components -PropertyName 'nativeName' -Label 'Windows 原生资源名' -Errors $Errors

    $ports = @($Document.portBindings)
    Add-DuplicateErrors -Items $ports -PropertyName 'portId' -Label '端口绑定 ID' -Errors $Errors
    Add-PortConflictErrors -Ports $ports -Errors $Errors -Label '环境端口绑定'

    $settings = @($Document.settings)
    Add-DuplicateErrors -Items $settings -PropertyName 'key' -Label '环境配置键' -Errors $Errors
    foreach ($setting in $settings) {
        $hasValue = Test-HasProperty -InputObject $setting -Name 'value'
        $hasSecretRef = Test-HasProperty -InputObject $setting -Name 'secretRef'
        if ($hasValue -eq $hasSecretRef) {
            $Errors.Add("环境配置 $($setting.key) 必须且只能包含 value 或 secretRef 之一")
        }
    }
}

function Test-InstalledStateSemantics {
    param(
        [object]$Document,
        [System.Collections.Generic.List[string]]$Errors
    )

    Add-DuplicateErrors -Items @($Document.components) -PropertyName 'componentId' -Label '安装组件状态' -Errors $Errors

    $operation = $Document.lastOperation
    $hasCompletedAt = Test-HasProperty -InputObject $operation -Name 'completedAt'
    if ($operation.status -eq 'running' -and $hasCompletedAt) {
        $Errors.Add('运行中的操作不得包含 completedAt')
    }
    if ($operation.status -ne 'running' -and -not $hasCompletedAt) {
        $Errors.Add("已结束的操作状态 $($operation.status) 必须包含 completedAt")
    }
}

function Test-PortRegistrySemantics {
    param(
        [object]$Document,
        [System.Collections.Generic.List[string]]$Errors
    )

    $reservations = @($Document.reservations)
    Add-PortConflictErrors -Ports $reservations -Errors $Errors -Label '主机端口登记'

    $ownershipKeys = @(
        $reservations |
            ForEach-Object {
                "$($_.projectId)/$($_.environment)/$($_.componentId)/$($_.portId)"
            }
    )
    foreach ($group in @($ownershipKeys | Group-Object | Where-Object Count -GT 1)) {
        $Errors.Add("同一端口需求存在多条活动登记：$($group.Name)")
    }
}

function Get-ManifestFiles {
    param(
        [string[]]$InputPaths,
        [bool]$Recursive
    )

    $files = [System.Collections.Generic.Dictionary[string, System.IO.FileInfo]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )

    foreach ($inputPath in $InputPaths) {
        foreach ($resolvedPath in @(Resolve-Path -Path $inputPath -ErrorAction Stop)) {
            $item = Get-Item -LiteralPath $resolvedPath.Path -Force
            if ($item.PSIsContainer) {
                $children = if ($Recursive) {
                    Get-ChildItem -LiteralPath $item.FullName -File -Filter '*.json' -Recurse
                } else {
                    Get-ChildItem -LiteralPath $item.FullName -File -Filter '*.json'
                }
                foreach ($child in $children) {
                    $files[$child.FullName] = $child
                }
            } elseif ($item.Extension -ieq '.json') {
                $files[$item.FullName] = $item
            } else {
                throw "不支持的输入文件类型：$($item.FullName)"
            }
        }
    }

    return @($files.Values | Sort-Object FullName)
}

$files = @(Get-ManifestFiles -InputPaths $Path -Recursive $Recurse.IsPresent)
if ($files.Count -eq 0) {
    Write-Error '没有找到待校验的 JSON 文件。'
    exit 2
}

$failedCount = 0
foreach ($file in $files) {
    $errors = [System.Collections.Generic.List[string]]::new()
    $raw = $null
    $document = $null

    try {
        $raw = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
        $document = $raw | ConvertFrom-Json
    } catch {
        $errors.Add("JSON 解析失败：$($_.Exception.Message)")
    }

    $kind = $null
    if ($null -ne $document) {
        if (-not (Test-HasProperty -InputObject $document -Name 'manifestKind')) {
            $errors.Add('缺少 manifestKind，无法选择权威 Schema')
        } else {
            $kind = [string]$document.manifestKind
            if (-not $schemaByKind.ContainsKey($kind)) {
                $errors.Add("不支持的 manifestKind：$kind")
            }
        }
    }

    if ($errors.Count -eq 0) {
        try {
            $schemaValid = Test-Json -Json $raw -SchemaFile $schemaByKind[$kind] -ErrorAction Stop
            if (-not $schemaValid) {
                $errors.Add("未通过 $kind 的 JSON Schema 校验")
            }
        } catch {
            $errors.Add("Schema 校验失败：$($_.Exception.Message)")
        }
    }

    if ($errors.Count -eq 0) {
        switch ($kind) {
            'ProjectManifest' {
                Test-ProjectManifestSemantics -Document $document -Errors $errors
            }
            'ReleaseManifest' {
                Test-ReleaseManifestSemantics -Document $document -Errors $errors
            }
            'EnvironmentBinding' {
                Test-EnvironmentBindingSemantics -Document $document -Errors $errors
            }
            'InstalledState' {
                Test-InstalledStateSemantics -Document $document -Errors $errors
            }
            'PortRegistry' {
                Test-PortRegistrySemantics -Document $document -Errors $errors
            }
        }
    }

    if ($errors.Count -eq 0) {
        if (-not $Quiet) {
            Write-Host "[PASS] $($file.FullName) ($kind)" -ForegroundColor Green
        }
    } else {
        $failedCount++
        Write-Host "[FAIL] $($file.FullName)" -ForegroundColor Red
        foreach ($validationError in $errors) {
            Write-Host "  - $validationError" -ForegroundColor Red
        }
    }
}

if (-not $Quiet) {
    Write-Host ""
    Write-Host "校验完成：$($files.Count - $failedCount) 通过，$failedCount 失败，共 $($files.Count) 个文件。"
}

if ($failedCount -gt 0) {
    exit 1
}

exit 0
