# CompanyOps Windows 完整操作手册（傻瓜式 MVP / 试点版）

> 普通首次安装不要从第 4 章逐条复制工程命令。请直接打开仓库根目录的 `三步安装CompanyOps.md`：构建电脑双击生成安装包、复制一个 EXE、服务器双击安装。后续章节只用于工程排障和高级接入。

> 适用仓库：`Ops_Manifest_Specification`
> 适用平台：Windows 10/11、Windows Server，x64
> 适用阶段：CompanyOps MVP 首次安装、只读盘点、项目声明接入和受控试点
> 默认安全模式：`Ops:EnableMutations=false`

## 0. 先看这一页：哪些能做，哪些暂时不能做

不要跳过本节。当前仓库是可运行的 MVP，不是已经封装完成的生产发布系统。

### 0.1 当前可以直接使用

| 能力 | 当前状态 | 普通运维是否可照本手册执行 |
|---|---|---|
| 构建 Agent、Console、CLI、PM2 Bridge | 已实现 | 可以 |
| 首次安装 Agent 和 Console Windows 服务 | 已实现 | 可以 |
| Console 本机 Windows 身份认证 | 已实现 | 可以 |
| 扫描 ProjectManifest、EnvironmentBinding、InstalledState | 已实现 | 可以 |
| 盘点 Windows Service、IIS、任务计划、监听端口 | 已实现 | 可以 |
| 只读查看项目、归属、健康和审计 | 已实现 | 可以 |
| Console 图形化检查更新 / 安全更新 / 回滚请求 | 已实现并通过前端生产构建 | Console 自动选择 Install 或 Update；完成 operator 浏览器与试点主机 UAT 后可以 |
| 校验 ReleaseManifest、ZIP 大小和 SHA-256 | 已实现 | 可以 |
| 安全解包到不可变 release、登记端口和版本指针 | 已实现 MVP | 只允许工程试点 |
| 精确启停已存在且已证明归属的原生组件 | 已实现 | 现场授权后可以 |
| 已存在 Windows Service 的真实入口切换、健康复核和失败恢复 | 已完成代码与假适配器验证 | 完成试点主机 UAT 后才可用于生产写入 |
| PM2 owner 快照与精确 `pm_id` 控制 | 已实现 | 只允许遗留项目试点 |

### 0.2 当前不能当作生产能力

| 尚未完成的能力 | 当前真实情况 | 临时处理原则 |
|---|---|---|
| 自动创建 Windows Service | 部署引擎不会执行 `New-Service` | 先由现有安装程序创建；CompanyOps 只纳管已存在资源 |
| 自动创建 IIS site / app pool | 部署引擎不会创建 IIS 资源 | 先按项目自己的受审安装流程创建 |
| 自动创建任务计划 | 部署引擎不会创建任务 | 先按项目自己的受审安装流程创建 |
| IIS / staticSite / 任务计划 / PM2 发布入口切换 | 尚无对应生产激活适配器；`Plan` 会失败关闭 | 继续使用项目受审更新流程，不得绕过 `Plan` |
| Secret Provider | 只支持 `secretRef` 契约，尚不解析真实 Secret | 不在 Manifest 写明文；仍由现有安全配置流程注入 |
| 防火墙、证书、HTTPS | 未实现 | 由主机现有基线或人工审批流程处理 |
| 数据库备份与迁移 | 未实现 | 继续使用项目专属备份、迁移和回退方案 |
| 断电 / Agent 进程崩溃后的自动事务恢复 | 受控异常会回滚，但尚无跨进程持久化激活日志 | 试点窗口先留存旧 ImagePath、pointer 和 InstalledState；异常重启后按审计与真实 SCM 状态人工恢复，不做无人值守发布 |
| 平台自身升级、卸载 | 安装脚本只支持首次安装 | 不得覆盖安装目录；等待版本化升级器 |
| 多主机集中控制 | 当前阶段明确不需要，不属于生产阻塞项 | 每台主机独立安装一个 Agent 和 Console |

**结论：** 普通运维当前可以安全完成“安装 CompanyOps、保持只读、接入声明、查看状态”。Windows Service（含 NSSM）与 interactiveApp 已共用真实入口切换代码闭环，但项目安装、更新、回滚和启停仍必须先在试点主机完成现场验收，不能只凭自动化测试直接照搬到生产。

---

## 1. 一分钟理解系统

每台 Windows 主机只部署一套 CompanyOps：

```text
Windows 主机
├─ CompanyOps.Agent       高权限 Windows Service，唯一执行者
├─ CompanyOps.Console     低权限 Windows Service，只监听 127.0.0.1:19310
├─ companyops.exe         本机诊断 CLI
├─ CompanyOps.Pm2Bridge   可选，仅遗留 PM2 项目需要
└─ 多个独立业务项目       不安装各自的运维管理器，只提交运维声明
```

项目不主动“注册”到 Agent，也不携带独立运维管理器。接入动作就是：

1. 开发人员提供 `ProjectManifest`；
2. CI 提供 `ReleaseManifest + ZIP + SHA-256`；
3. 运维人员为具体主机提供 `EnvironmentBinding`；
4. 把声明放进安装时选择的 `<DataRoot>\manifests`；
5. Agent 每 30 秒扫描一次并生成项目视图。

五类文件的责任边界：

| 文件 | 谁维护 | 放在哪里 | 内容 |
|---|---|---|---|
| ProjectManifest | 项目开发者 | 项目 Git 仓库和服务器 manifests 目录 | 项目需要什么 |
| ReleaseManifest | CI / 发布者 | 与 ZIP 放在同一发布目录 | 这个版本包含什么 |
| EnvironmentBinding | 授权运维者 | 服务器 manifests 目录 | 这台主机如何绑定端口、路径和原生资源 |
| InstalledState | CompanyOps Agent | 服务器 manifests 目录 | 实际安装状态和 generation |
| PortRegistry | CompanyOps Agent | Agent SQLite 状态库 | 主机端口归属 |

---

## 2. 永远不要做的事情

- 不要在 Manifest 中填写密码、Token、私钥、数据库连接串等明文 Secret。
- 不要手工修改 Agent 生成的 InstalledState。
- 不要直接覆盖安装时选择的 CompanyOps 程序目录。
- 不要在生产首日把 `EnableMutations` 改成 `true`。
- 不要因为进程存在、端口监听或 PM2 显示 `online` 就判定业务健康。
- 不要执行 `pm2 stop all`、`pm2 delete all` 或 `pm2 kill`。
- 不要设置、修改或依赖 `PM2_HOME` 来隔离项目。
- 不要结束未知端口的监听进程。
- 不要把文件版本回滚等同于数据库回滚。
- 不要把自动化测试通过等同于真实服务器、真实账号和真实业务 UAT 通过。

遇到归属冲突、同名多实例、generation 不一致、健康失败或快照过期时，正确动作是停止并查明原因，不是绕过校验。

---

## 3. 准备角色和三台“逻辑机器”

实际可以是两台机器，但职责要分开理解。

| 角色 | 用途 | 需要的权限 |
|---|---|---|
| 构建机 | 拉代码、测试、生成 CompanyOps 发布包 | 普通开发权限；已安装 SDK |
| 目标服务器 | 运行 Agent、Console 和业务项目 | 安装时需要本地管理员 |
| 管理员工作位 | 通过远程桌面登录目标服务器操作 Console | Windows 域账号或本地账号 |

建议先选一台非生产 Windows 试点服务器，并选择一个低风险项目。试点项目最好满足：

- 只有一个后端服务；
- 有 `GET /health`；
- 不执行不可逆数据库迁移；
- 可以人工安装和回滚；
- 停机不会影响关键业务。

---

## 4. 构建机准备

### 4.1 安装必要工具

构建机需要：

- Git；
- PowerShell 7，命令为 `pwsh`；
- .NET SDK `10.0.302` 或兼容的更新补丁；
- Node.js `20.19.0+`，或 `22.12.0+`；
- npm。

打开 PowerShell 7，逐条检查：

```powershell
git --version
pwsh --version
dotnet --version
node --version
npm --version
```

任何一条显示“找不到命令”，先安装对应工具，不要继续。

### 4.2 获取代码

新构建机执行：

```powershell
$WorkspaceRootInput = (Read-Host '请输入源码工作区路径；相对路径按当前目录解析').Trim()
if ([string]::IsNullOrWhiteSpace($WorkspaceRootInput)) {
    throw '源码工作区不能为空'
}
$WorkspaceRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkspaceRootInput)
$WorkspaceDriveRoot = [System.IO.Path]::GetPathRoot($WorkspaceRoot)
if ($WorkspaceDriveRoot -notmatch '^[A-Za-z]:\\$') {
    throw "源码工作区必须位于本机磁盘：$WorkspaceRoot"
}

New-Item -ItemType Directory -Force -Path $WorkspaceRoot | Out-Null
Set-Location $WorkspaceRoot

$GitUrlRewrites = @(git config --show-origin --get-regexp '^url\..*\.insteadof$' 2>$null)
$SuspiciousGitUrlRewrites = @(
    $GitUrlRewrites | Where-Object { $_ -match 'ghproxy|github.*mirror|mirror.*github' }
)
if ($SuspiciousGitUrlRewrites.Count -gt 0) {
    $SuspiciousGitUrlRewrites | ForEach-Object { Write-Warning $_ }
    throw '检测到 GitHub URL 被重写到镜像。请先按第 17 章“Git 被重写到失效镜像”处理，再克隆。'
}

$RepositoryRoot = Join-Path $WorkspaceRoot 'Ops_Manifest_Specification'
if (Test-Path -LiteralPath $RepositoryRoot) {
    throw "目标仓库目录已存在，拒绝覆盖：$RepositoryRoot。若它是已有仓库，请改用后面的已有仓库步骤。"
}

git clone https://github.com/alo58-alt/Ops_Manifest_Specification.git $RepositoryRoot
if ($LASTEXITCODE -ne 0) {
    throw 'git clone 失败。不要继续执行 Set-Location；先处理上面的 Git 原始错误。'
}

if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot '.git'))) {
    throw "克隆后仍找不到仓库：$RepositoryRoot"
}
Set-Location $RepositoryRoot
```

已有仓库执行：

```powershell
$RepositoryRootInput = (Read-Host '请输入现有 Ops_Manifest_Specification 仓库路径').Trim()
if ([string]::IsNullOrWhiteSpace($RepositoryRootInput)) {
    throw '仓库路径不能为空'
}
$RepositoryRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RepositoryRootInput)
if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot '.git'))) {
    throw "不是有效的 Git 仓库：$RepositoryRoot"
}

Set-Location $RepositoryRoot
git status --short --branch
git pull --ff-only
```

如果 `git status` 显示未提交修改，停止。不要 stash、reset 或覆盖不明改动。

### 4.3 还原依赖并测试

在仓库根目录逐条执行：

```powershell
dotnet restore .\OpsManifest.slnx --configfile .\NuGet.config
dotnet build .\OpsManifest.slnx -c Release --no-restore
dotnet test .\OpsManifest.slnx -c Release --no-build --no-restore
pwsh -NoProfile -File .\tests\Run-ContractTests.ps1
```

合格标准：

- `dotnet build`：0 error；
- .NET 测试：全部通过；
- 契约测试：全部通过；
- 没有继续执行安装或启动任何本机服务。

### 4.4 生成发布目录

```powershell
pwsh -NoProfile -File .\tools\Publish-OpsPlatform.ps1
```

成功后必须存在：

```text
artifacts\publish\Agent
artifacts\publish\Console
artifacts\publish\Cli
artifacts\publish\Pm2Bridge
```

此命令只生成文件，不注册服务、不修改端口、不启动 Agent。

### 4.5 打包并计算 SHA-256

先选择交付输出目录，再生成交付包：

```powershell
$RepositoryRoot = (git rev-parse --show-toplevel).Trim()
$DeliveryRootInput = (Read-Host '请输入交付输出目录；相对路径按当前目录解析').Trim()
if ([string]::IsNullOrWhiteSpace($DeliveryRootInput)) {
    throw '交付输出目录不能为空'
}
$DeliveryRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DeliveryRootInput)
if ([System.IO.Path]::GetPathRoot($DeliveryRoot) -notmatch '^[A-Za-z]:\\$') {
    throw "交付目录必须位于本机磁盘：$DeliveryRoot"
}

$PackageRoot = Join-Path $DeliveryRoot 'CompanyOps-Package'
$ZipPath = Join-Path $DeliveryRoot 'CompanyOps-Package.zip'

New-Item -ItemType Directory -Force -Path $DeliveryRoot | Out-Null
if (Test-Path -LiteralPath $PackageRoot) {
    throw "交付目录已存在，请人工确认后换一个新目录：$PackageRoot"
}

New-Item -ItemType Directory -Path $PackageRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'artifacts\publish') -Destination $PackageRoot -Recurse
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'tools\Install-OpsPlatform.ps1') -Destination $PackageRoot
Compress-Archive -Path (Join-Path $PackageRoot '*') -DestinationPath $ZipPath
Get-FileHash -Algorithm SHA256 -LiteralPath $ZipPath
```

记录屏幕上的 SHA-256。传到服务器后必须重新计算并比对。

> 如果 `CompanyOps-Package` 或 ZIP 已存在，不要直接覆盖。换一个带日期和版本的新目录，保留每次交付的来源和哈希。

---

## 5. 目标服务器首次安装 CompanyOps

### 5.1 服务器前置条件

目标服务器至少需要：

- 支持的 Windows x64；
- .NET 10 ASP.NET Core Runtime x64；
- 本地管理员账号；
- 能访问本机 `127.0.0.1`；
- 足够的程序盘、数据盘和发布盘空间。

按项目类型额外需要：

- IIS 项目：安装并按企业基线配置 IIS；
- 遗留 PM2 项目：由真实 PM2 owner 账号安装 Node.js 和 PM2；
- 普通 Windows Service 项目：不需要 PM2。

服务器不需要为了 CompanyOps Console 对外开放 19310 端口。

### 5.2 复制并校验交付包

先在当前管理员 PowerShell 会话中选择 CompanyOps 程序目录和数据目录。两个目录可以位于任意本地磁盘，但必须是不同、非嵌套、不是磁盘根的绝对路径：

```powershell
$InstallRootInput = (Read-Host '请输入 CompanyOps 程序目录；相对路径按当前目录解析').Trim()
$DataRootInput = (Read-Host '请输入 CompanyOps 数据目录；相对路径按当前目录解析').Trim()
if ([string]::IsNullOrWhiteSpace($InstallRootInput) -or [string]::IsNullOrWhiteSpace($DataRootInput)) {
    throw '程序目录和数据目录不能为空'
}
$InstallRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallRootInput)
$DataRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DataRootInput)

foreach ($SelectedPath in @($InstallRoot, $DataRoot)) {
    $SelectedRoot = [System.IO.Path]::GetPathRoot($SelectedPath)
    if ($SelectedRoot -notmatch '^[A-Za-z]:\\$') {
        throw "CompanyOps 程序和状态目录必须位于本机磁盘：$SelectedPath"
    }
    if ($SelectedPath.TrimEnd('\') -eq $SelectedRoot.TrimEnd('\')) {
        throw "不能把磁盘根目录作为 CompanyOps 目录：$SelectedPath"
    }
    if (-not (Test-Path -LiteralPath $SelectedRoot -PathType Container)) {
        throw "目标磁盘不存在：$SelectedPath"
    }
}

$InstallPrefix = $InstallRoot.TrimEnd('\') + '\'
$DataPrefix = $DataRoot.TrimEnd('\') + '\'
if ($InstallRoot -eq $DataRoot -or
    $InstallPrefix.StartsWith($DataPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
    $DataPrefix.StartsWith($InstallPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw '程序目录和数据目录必须相互独立，不能相同或互相嵌套'
}

[pscustomobject]@{
    InstallRoot = $InstallRoot
    DataRoot = $DataRoot
} | Format-List
```

记录这两个值，并在本章剩余步骤中保持同一个 PowerShell 窗口。如果窗口关闭，重新设置相同的 `$InstallRoot` 和 `$DataRoot` 后再继续。

将 ZIP 复制到服务器的受控暂存目录。在服务器管理员 PowerShell 中输入实际 ZIP 路径并计算哈希：

```powershell
$PackageZip = (Read-Host '请输入服务器上 CompanyOps-Package.zip 的绝对路径').Trim()
if (-not (Test-Path -LiteralPath $PackageZip -PathType Leaf)) {
    throw "找不到交付 ZIP：$PackageZip"
}

Get-FileHash -Algorithm SHA256 -LiteralPath $PackageZip
```

只有哈希与构建机记录完全一致，才能继续。不同就停止并重新传输。

解压到一个全新目录：

```powershell
$StageRootInput = (Read-Host '请输入全新的解压目标目录；相对路径按当前目录解析').Trim()
if ([string]::IsNullOrWhiteSpace($StageRootInput)) {
    throw '解压目标目录不能为空'
}
$StageRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($StageRootInput)
if ([System.IO.Path]::GetPathRoot($StageRoot) -notmatch '^[A-Za-z]:\\$') {
    throw "解压目标必须位于本机磁盘：$StageRoot"
}

if (Test-Path -LiteralPath $StageRoot) {
    throw "目标目录已存在，请人工确认并换用全新目录：$StageRoot"
}

Expand-Archive -LiteralPath $PackageZip -DestinationPath $StageRoot
Get-ChildItem -LiteralPath $StageRoot
```

应看到：

```text
publish
Install-OpsPlatform.ps1
```

### 5.3 安装前检查

```powershell
Get-Service -Name CompanyOps.Agent,CompanyOps.Console -ErrorAction SilentlyContinue
Test-Path -LiteralPath $InstallRoot
Test-Path -LiteralPath $DataRoot
whoami
$env:COMPUTERNAME
```

判断：

- 如果已经存在 `CompanyOps.Agent` 或 `CompanyOps.Console`，停止；这是升级或修复，不是首次安装。
- 如果所选 `$InstallRoot` 已存在，停止；不要删除或覆盖。
- 如果所选 `$DataRoot` 已存在，先确认是否为以前的状态数据。
- 记录 `whoami` 输出，它就是 Console operator 候选账号。
- 记录 `$env:COMPUTERNAME`，它就是默认 HostId。

### 5.4 只预览，不安装

```powershell
$StageRoot = (Read-Host '请输入刚才解压的 CompanyOps-Package 目录绝对路径').Trim()
$HostId = $env:COMPUTERNAME
$OperatorAccount = (whoami).Trim()

& (Join-Path $StageRoot 'Install-OpsPlatform.ps1') `
    -PublishRoot (Join-Path $StageRoot 'publish') `
    -InstallRoot $InstallRoot `
    -DataRoot $DataRoot `
    -HostId $HostId `
    -Operators $OperatorAccount
```

必须看到“安全预览：未传入 `-Apply`”。这一步不复制文件、不注册服务。

核对屏幕内容：

- 发布源必须是刚才解压的 `publish`；
- 程序目录应为你选择的 `$InstallRoot`；
- 数据目录应为你选择的 `$DataRoot`；
- 注册两个服务；
- 默认不启动。

### 5.5 正式首次安装，但不要自动启动

确认 PowerShell 窗口标题包含“管理员”，然后执行：

```powershell
$StageRoot = (Read-Host '请再次输入已核对的 CompanyOps-Package 目录绝对路径').Trim()
$HostId = $env:COMPUTERNAME
$OperatorAccount = (whoami).Trim()

& (Join-Path $StageRoot 'Install-OpsPlatform.ps1') `
    -PublishRoot (Join-Path $StageRoot 'publish') `
    -InstallRoot $InstallRoot `
    -DataRoot $DataRoot `
    -HostId $HostId `
    -Operators $OperatorAccount `
    -Apply `
    -Confirm
```

出现确认提示时，再次核对目标路径后确认。不要加 `-StartServices`。

成功标准：

- 显示“安装完成”；
- 显示 mutations 仍为 false；
- 两个服务已注册但尚未启动。

### 5.6 安装后检查配置

```powershell
Get-Service -Name CompanyOps.Agent,CompanyOps.Console | Format-Table Name,Status,StartType

Get-Content -Raw -LiteralPath (Join-Path $InstallRoot 'Agent\appsettings.json')
Get-Content -Raw -LiteralPath (Join-Path $InstallRoot 'Console\appsettings.json')
```

Agent 必须满足：

```text
HostId                 = 当前服务器 HostId
ManifestDirectory      = <DataRoot>\manifests
StateDirectory         = <DataRoot>\Agent
Pm2SnapshotDirectory   = <DataRoot>\Agent\pm2-snapshots
PipeName               = CompanyOps.Agent.v1
EnableMutations        = false
AllowedProjectInstallRoots = 空数组；启用部署前必须填写受审的项目父目录
AllowedClientSids      包含 S-1-5-20
```

Console 必须满足：

```text
Urls                    = http://127.0.0.1:19310
PipeName                = CompanyOps.Agent.v1
Operators               包含指定 Windows 账号
AllowLocalAdministrators = true
```

查看目录 ACL，不要直接修改：

```powershell
Get-Acl -LiteralPath $InstallRoot | Format-List
Get-Acl -LiteralPath $DataRoot | Format-List
```

### 5.7 先启动 Agent

```powershell
Start-Service -Name CompanyOps.Agent
Start-Sleep -Seconds 3
Get-Service -Name CompanyOps.Agent
```

必须为 `Running`。然后以管理员身份运行 CLI：

```powershell
$CompanyOpsCli = Join-Path $InstallRoot 'Cli\companyops.exe'
& $CompanyOpsCli ping
```

> 实际发布文件名以 `<InstallRoot>\Cli` 中的 `.exe` 为准。如果上面的文件不存在，执行 `Get-ChildItem (Join-Path $InstallRoot 'Cli\*.exe')` 查找，不要猜路径。

`ping` 返回中必须看到：

```text
"mode": "read-only"
```

如果显示 `mutations-enabled`，立即停止 CompanyOps.Agent，检查配置为什么提前启用了写操作。

### 5.8 再启动 Console

```powershell
Start-Service -Name CompanyOps.Console
Start-Sleep -Seconds 3
Get-Service -Name CompanyOps.Console
Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort 19310 -ErrorAction SilentlyContinue
```

在目标服务器本机浏览器打开：

```text
http://127.0.0.1:19310
```

合格标准：

- 页面能打开；
- 显示当前 Windows 用户和角色；
- Agent 模式是 `read-only`；
- 页面没有暴露远程 shell 或任意命令输入框；
- 非 operator 用户只能查看，不能点击启停。

### 5.9 首次只读验收

在管理员 PowerShell 中逐条执行：

```powershell
$CompanyOpsCli = Join-Path $InstallRoot 'Cli\companyops.exe'
& $CompanyOpsCli catalog
& $CompanyOpsCli inventory
& $CompanyOpsCli projects
& $CompanyOpsCli audit
```

空项目主机的正常情况：

- `catalog` 没有声明；
- `projects` 为空；
- `inventory` 能列出 Windows Service、端口等盘点源；
- 未安装 IIS 时 IIS 盘点源可以是 `Unavailable`，但不应无解释地 `Failed`；
- `audit` 包含 Agent startup。

### 5.10 以后重新打开 PowerShell 时先恢复目录变量

后续章节使用 `$InstallRoot` 和 `$DataRoot`，它们必须等于首次安装时的选择。每次新开管理员 PowerShell，先执行：

```powershell
$InstallRootInput = (Read-Host '请输入首次安装时选择的 CompanyOps 程序目录').Trim()
$DataRootInput = (Read-Host '请输入首次安装时选择的 CompanyOps 数据目录').Trim()
if ([string]::IsNullOrWhiteSpace($InstallRootInput) -or [string]::IsNullOrWhiteSpace($DataRootInput)) {
    throw '程序目录和数据目录不能为空'
}
$InstallRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallRootInput)
$DataRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DataRootInput)
if ([System.IO.Path]::GetPathRoot($InstallRoot) -notmatch '^[A-Za-z]:\\$' -or
    [System.IO.Path]::GetPathRoot($DataRoot) -notmatch '^[A-Za-z]:\\$') {
    throw '程序目录和数据目录必须位于本机磁盘'
}
$CompanyOpsCli = Join-Path $InstallRoot 'Cli\companyops.exe'

if (-not (Test-Path -LiteralPath $CompanyOpsCli -PathType Leaf)) {
    throw "程序目录不正确，找不到 CLI：$CompanyOpsCli"
}
if (-not (Test-Path -LiteralPath (Join-Path $DataRoot 'manifests') -PathType Container)) {
    throw "数据目录不正确，找不到 manifests：$DataRoot"
}

[pscustomobject]@{
    InstallRoot = $InstallRoot
    DataRoot = $DataRoot
    CompanyOpsCli = $CompanyOpsCli
} | Format-List
```

不要从服务名称或文档示例猜目录；以首次安装记录和实际文件为准。

---

## 6. 新项目如何选择组件类型

| 项目情况 | 推荐 kind | 说明 |
|---|---|---|
| 后台 API、常驻 Worker、Python/.NET/Node 后端 | `windowsService` | 新项目默认首选，由 SCM 管理 |
| ASP.NET 或必须依赖 IIS 应用池 | `iisSite` | 需要现有 IIS 资源 |
| 已构建的 Vue/React 静态页面 | `staticSite` | 由 IIS 静态站点承载 |
| 每天/每小时执行的批处理 | `scheduledTask` | 由任务计划承载 |
| 已经在 PM2 中稳定运行的老项目 | `pm2Legacy` | 仅迁移期兼容，不作为新项目默认选择 |

一个项目可以包含多个组件，例如：

```text
api（windowsService）
├─ web（staticSite，依赖 api）
└─ cleanup（scheduledTask，依赖 api）
```

依赖关系只允许引用同项目组件，不能自依赖，不能形成循环。

---

## 7. 项目开发者编写 ProjectManifest

### 7.1 在项目仓库建立文件

建议结构：

```text
业务项目根目录
└─ ops
   └─ project-manifest.json
```

项目 ID 规则：

- 小写字母开头；
- 只用小写字母、数字和连字符；
- 创建后不要随意修改；
- 示例：`contract-review-system`。

### 7.2 最小 Windows Service 示例

复制下面内容后替换所有 `demo-api` 和显示名称：

```json
{
  "$schema": "https://raw.githubusercontent.com/alo58-alt/Ops_Manifest_Specification/main/spec/v1/schemas/project-manifest.schema.json",
  "apiVersion": "ops.company/v1",
  "manifestKind": "ProjectManifest",
  "metadata": {
    "id": "demo-api",
    "displayName": "示例 API",
    "description": "CompanyOps 试点项目",
    "owners": [
      "platform-team"
    ]
  },
  "components": [
    {
      "id": "api",
      "displayName": "API 服务",
      "kind": "windowsService",
      "entrypoint": "api-main",
      "dependsOn": [],
      "health": [
        {
          "kind": "http",
          "portRef": "api-http",
          "path": "/health",
          "expectedStatus": 200,
          "expectJson": {
            "status": "ok"
          },
          "timeoutSeconds": 2
        }
      ],
      "service": {
        "startMode": "delayed",
        "failureRestartLimit": 3
      }
    }
  ],
  "ports": [
    {
      "id": "api-http",
      "componentId": "api",
      "protocol": "tcp",
      "allocation": "dynamic",
      "preferredPort": 9201,
      "exposure": "loopback"
    }
  ],
  "configuration": [],
  "dataDirectories": [],
  "update": {
    "strategy": "stopStart",
    "rollbackOnFailure": true,
    "healthTimeoutSeconds": 60,
    "retainReleaseCount": 2
  }
}
```

注意：

- `entrypoint` 是逻辑 ID，稍后必须在 ReleaseManifest 中对应；
- `preferredPort` 是申请值，不代表已经占用；
- `exposure=loopback` 表示只允许本机访问；
- 健康接口最好返回 `{"status":"ok"}`；
- Secret 配置只声明 `type=secret`，不能写真实值。

### 7.3 在规范仓库中校验

```powershell
$SpecRepository = (Read-Host '请输入 Ops_Manifest_Specification 仓库绝对路径').Trim()
$ProjectManifest = (Read-Host '请输入业务项目 project-manifest.json 绝对路径').Trim()
if (-not (Test-Path -LiteralPath (Join-Path $SpecRepository 'tools\Test-OpsManifest.ps1'))) {
    throw "规范仓库路径无效：$SpecRepository"
}

pwsh -NoProfile -File (Join-Path $SpecRepository 'tools\Test-OpsManifest.ps1') $ProjectManifest
```

只接受退出码 0 和明确的验证成功。验证失败时修改声明，不要绕过。

### 7.4 项目代码提交要求

项目仓库应该提交：

- `ops/project-manifest.json`；
- 健康接口实现；
- 构建和测试入口；
- 不含 Secret 的配置示例。

项目仓库不得提交：

- `.env` 生产文件；
- 数据库文件；
- Token、私钥、PFX 密码；
- 服务器上的 InstalledState；
- 主机具体端口和绝对安装路径。

---

## 8. 运维人员编写 EnvironmentBinding

EnvironmentBinding 是“这台服务器的落地参数”，不应由项目开发者单方面决定。

### 8.1 最小示例

下面以“一个 `api` Windows Service”为例。所有主机路径都由运维人员输入，PowerShell 负责正确生成 JSON 和路径转义：

```powershell
$ProjectId = (Read-Host '请输入项目 ID').Trim()
$EnvironmentName = (Read-Host '请输入环境名').Trim()
$HostId = (Read-Host '请输入 Agent 配置中的 HostId').Trim()
$NativeServiceName = (Read-Host '请输入真实 Windows Service 名称').Trim()
$ProjectInstallRootInput = (Read-Host '请输入该项目的安装根目录').Trim()
$ProjectDataRootInput = (Read-Host '请输入该项目的数据根目录').Trim()
$ProjectLogsRootInput = (Read-Host '请输入该项目的日志根目录').Trim()
$CandidatePort = [int](Read-Host '请输入该项目 API 的实际端口')
$EnvironmentBindingInput = (Read-Host '请输入 EnvironmentBinding 输出文件路径').Trim()

foreach ($SelectedInput in @($ProjectInstallRootInput, $ProjectDataRootInput, $ProjectLogsRootInput, $EnvironmentBindingInput)) {
    if ([string]::IsNullOrWhiteSpace($SelectedInput)) {
        throw '项目目录和 EnvironmentBinding 输出路径都不能为空'
    }
}

$ProjectInstallRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProjectInstallRootInput)
$ProjectDataRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProjectDataRootInput)
$ProjectLogsRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProjectLogsRootInput)
$EnvironmentBinding = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EnvironmentBindingInput)

foreach ($SelectedPath in @($ProjectInstallRoot, $ProjectDataRoot, $ProjectLogsRoot, $EnvironmentBinding)) {
    if ([System.IO.Path]::GetPathRoot($SelectedPath) -notmatch '^[A-Za-z]:\\$') {
        throw "必须位于本机磁盘：$SelectedPath"
    }
}

$BindingOutputDirectory = Split-Path -Parent $EnvironmentBinding
New-Item -ItemType Directory -Force -Path $BindingOutputDirectory | Out-Null

$BindingDocument = [ordered]@{
    '$schema' = 'https://raw.githubusercontent.com/alo58-alt/Ops_Manifest_Specification/main/spec/v1/schemas/environment-binding.schema.json'
    apiVersion = 'ops.company/v1'
    manifestKind = 'EnvironmentBinding'
    metadata = [ordered]@{
        projectId = $ProjectId
        environment = $EnvironmentName
        hostId = $HostId
        revision = 1
    }
    roots = [ordered]@{
        install = $ProjectInstallRoot
        data = $ProjectDataRoot
        logs = $ProjectLogsRoot
    }
    componentBindings = @(
        [ordered]@{
            componentId = 'api'
            serviceAccountRef = "$ProjectId-runtime"
            nativeName = $NativeServiceName
        }
    )
    portBindings = @(
        [ordered]@{
            portId = 'api-http'
            componentId = 'api'
            protocol = 'tcp'
            address = '127.0.0.1'
            port = $CandidatePort
        }
    )
    settings = @()
}

$BindingDocument | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $EnvironmentBinding -Encoding utf8
Get-Content -Raw -LiteralPath $EnvironmentBinding
```

必须逐项核对：

- `projectId` 与 ProjectManifest 完全一致；
- `hostId` 与 Agent `appsettings.json` 完全一致；
- `componentId` 与组件 ID 完全一致；
- `nativeName` 是真实 Windows Service / IIS site / Task 名称；
- 三个 roots 是绝对 Windows 路径；
- 每个端口只分配给一个项目组件；
- Secret 使用 `secretRef`，不使用明文 `value`。

当前 MVP 中这些字段是受控声明，不是自动 provision 指令：

- `serviceAccountRef` 不会自动创建 Windows 账号或授予“作为服务登录”；
- `roots.data` 和 `roots.logs` 不会自动完成目录 ACL；
- `portBindings` 会参与端口预留，但不会自动改写业务项目配置；
- `routes` 不会自动创建 DNS、证书、反向代理或防火墙规则；
- `secretRef` 不会自动解析真实 Secret。

因此项目自己的受审安装流程仍要负责创建账号、目录 ACL、原生资源和非明文运行配置，直到对应 CompanyOps provider 实现并通过现场验收。

### 8.2 端口分配前检查

```powershell
$CandidatePort = 19201
Get-NetTCPConnection -LocalPort $CandidatePort -ErrorAction SilentlyContinue
```

如果已有监听，不要结束进程。查明归属后重新选择端口。

### 8.3 校验 EnvironmentBinding

```powershell
$SpecRepository = (Read-Host '请输入 Ops_Manifest_Specification 仓库绝对路径').Trim()
$EnvironmentBinding = (Read-Host '请输入 EnvironmentBinding JSON 绝对路径').Trim()
pwsh -NoProfile -File (Join-Path $SpecRepository 'tools\Test-OpsManifest.ps1') $EnvironmentBinding
```

---

## 9. 把项目声明安全放进服务器

Agent 只扫描 manifests 目录第一层的 `*.json`，不会递归扫描子目录。

目标目录：

```text
<安装时选择的 DataRoot>\manifests
```

推荐文件名：

```text
demo-api.project.json
demo-api.production.WIN-OPS-01.binding.json
```

### 9.1 使用临时扩展名复制，避免扫描到半个文件

```powershell
$ManifestRoot = Join-Path $DataRoot 'manifests'
$SourceProject = (Read-Host '请输入已校验的 ProjectManifest 绝对路径').Trim()
$SourceBinding = (Read-Host '请输入已校验的 EnvironmentBinding 绝对路径').Trim()

Copy-Item -LiteralPath $SourceProject -Destination (Join-Path $ManifestRoot 'demo-api.project.json.pending')
Copy-Item -LiteralPath $SourceBinding -Destination (Join-Path $ManifestRoot 'demo-api.production.WIN-OPS-01.binding.json.pending')

Move-Item -LiteralPath (Join-Path $ManifestRoot 'demo-api.project.json.pending') -Destination (Join-Path $ManifestRoot 'demo-api.project.json')
Move-Item -LiteralPath (Join-Path $ManifestRoot 'demo-api.production.WIN-OPS-01.binding.json.pending') -Destination (Join-Path $ManifestRoot 'demo-api.production.WIN-OPS-01.binding.json')
```

不要把 Release ZIP、日志、`.env` 或数据库复制到 manifests 目录。

### 9.2 等待扫描并验证

等待最多 35 秒，然后执行：

```powershell
Start-Sleep -Seconds 35
$CompanyOpsCli = Join-Path $InstallRoot 'Cli\companyops.exe'
& $CompanyOpsCli catalog
& $CompanyOpsCli projects
```

预期状态：

- 两个文件的 `isValid=true`；
- 项目状态是 `Declared`；
- 组件归属是 `DeclaredOnly`；
- 没有 `Conflict`。

常见异常：

| 现象 | 原因 | 处理 |
|---|---|---|
| 项目完全不出现 | binding 的 hostId 不等于 Agent HostId | 修正 hostId，revision 加 1 |
| catalog 显示无效 | JSON 或 Schema 错误 | 在规范仓库重新校验 |
| ProjectManifest 重复 | 同一项目放了两份声明 | 保留唯一权威文件 |
| EnvironmentBinding 重复 | 同项目/环境/主机有两份 binding | 保留唯一文件 |
| componentBinding 缺失 | 有组件未绑定 nativeName | 补全 binding |
| 端口冲突 | 相同协议、地址和端口重复 | 分配新端口，不结束未知进程 |

---

## 10. 生成业务项目发布包和 ReleaseManifest

本节应由 CI 自动完成。手工方式只用于试点。

### 10.1 推荐发布目录

```text
<你选择的发布根目录>\demo-api\1.0.0
├─ release-manifest.json
└─ demo-api-1.0.0.zip
```

ZIP 中不要再包一层不稳定的随机目录。示例：

```text
demo-api-1.0.0.zip
└─ api
   ├─ Demo.Api.exe
   ├─ Demo.Api.dll
   └─ appsettings.json.example
```

生产配置和 Secret 不进入 ZIP。

### 10.2 计算制品信息

```powershell
$ArtifactPath = (Read-Host '请输入业务制品 ZIP 的绝对路径').Trim()
$Artifact = Get-Item -LiteralPath $ArtifactPath
$ArtifactHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ArtifactPath).Hash.ToLowerInvariant()

[pscustomobject]@{
    FileName = $Artifact.Name
    SizeBytes = $Artifact.Length
    Sha256 = $ArtifactHash
} | Format-List
```

把三个值原样写入 ReleaseManifest。

### 10.3 ReleaseManifest 最小示例

```json
{
  "$schema": "https://raw.githubusercontent.com/alo58-alt/Ops_Manifest_Specification/main/spec/v1/schemas/release-manifest.schema.json",
  "apiVersion": "ops.company/v1",
  "manifestKind": "ReleaseManifest",
  "metadata": {
    "projectId": "demo-api",
    "version": "1.0.0",
    "releaseId": "demo-api-1.0.0-20260812.1",
    "builtAt": "2026-08-12T00:00:00Z",
    "sourceRevision": "0123456789abcdef0123456789abcdef01234567"
  },
  "target": {
    "os": "windows",
    "architecture": "x64",
    "minAgentVersion": "0.1.0"
  },
  "projectManifestSha256": "替换为64位小写SHA256",
  "artifacts": [
    {
      "id": "api-package",
      "fileName": "demo-api-1.0.0.zip",
      "mediaType": "application/zip",
      "sha256": "替换为制品64位小写SHA256",
      "sizeBytes": 123456
    }
  ],
  "componentPayloads": [
    {
      "componentId": "api",
      "entrypoint": "api-main",
      "artifactId": "api-package",
      "path": "api/Demo.Api.exe",
      "workingDirectory": "api"
    }
  ]
}
```

占位符必须全部替换，否则 Schema 校验失败。`path` 必须是 ZIP 内相对路径，不能使用盘符、UNC 或 `..`。

### 10.4 校验发布声明

```powershell
$SpecRepository = (Read-Host '请输入 Ops_Manifest_Specification 仓库绝对路径').Trim()
$ReleaseManifest = (Read-Host '请输入 release-manifest.json 绝对路径').Trim()
pwsh -NoProfile -File (Join-Path $SpecRepository 'tools\Test-OpsManifest.ps1') $ReleaseManifest
```

同一版本一旦交付，不允许覆盖 ZIP 或修改 ReleaseManifest。修复必须生成新版本。

---

## 11. 只做部署计划（普通运维到这里为止）

如果项目卡片仍显示旧组件数量，而本次受审版本新增了组件，先把服务器项目目录快进到包含新 `ops\project-manifest.json` 的提交，再在“接入现有项目”中选择原目录重新检查并确认。该刷新只允许新增组件并保留原根目录、原组件 kind 和原生绑定；不会启停业务服务。任何删除、改绑或类型变化必须走单独迁移，不得用重新接入绕过。

保持 `EnableMutations=false`。首次安装项目时 generation 使用 `0`。执行 Plan 前先由主机管理员在 Agent 配置中填写 `AllowedProjectInstallRoots`；每个 `roots.install` 必须是其中一个父目录下的独立项目子目录，不能直接等于共享父目录或盘符根目录，不同项目目录也不能相同或互相嵌套。

### 11.1 Console 图形页面（普通运维优先）

1. 在“项目与组件”找到目标项目，点击“更新项目”；
2. 在“项目更新”只填写发布包目录。该目录必须同时包含 `release-manifest.json` 和清单引用的发布 ZIP；
3. 点击“检查更新”执行只读预检，或点击“安全更新”进入受控更新；
4. Console 自动根据 InstalledState 选择首次 Install 或普通 Update，Agent 在切换前仍会重新校验 generation、归属、ReleaseManifest、ZIP 大小和 SHA-256；
5. 查看返回步骤和最近审计。用户不需要手工计算、填写或比较哈希。

已接入但尚无 InstalledState 的现有项目显示为 `Declared`；其 generation 可能是 EnvironmentBinding 修订号，不代表已经发布。此时“检查更新”按首次 Install 语义预检，“安全更新”建立第一份不可变 release 和 InstalledState。已有 InstalledState 时同一个按钮自动使用 Update，无需用户选择动作。

请求获得明确结果后页面会生成下一条新幂等键；网络错误或超时时保留原键。遇到不确定结果必须先查 `audit`，只允许使用同一键重试，不能生成新键猜测执行。

### 11.2 CLI 结构化请求（工程诊断）

输入实际发布目录，让 PowerShell 生成 `deploy-plan.json`，避免手工转义路径：

```powershell
$ReleaseDirectory = (Read-Host '请输入包含 ZIP 和 release-manifest.json 的发布目录绝对路径').Trim()
$ProjectId = (Read-Host '请输入 ProjectManifest 中的项目 ID').Trim()
$EnvironmentName = (Read-Host '请输入 EnvironmentBinding 中的环境名').Trim()
$GenerationText = (Read-Host '请输入当前 generation；首次安装请输入 0').Trim()
$ExpectedGeneration = [long]$GenerationText
$PlanId = $ProjectId + '-plan-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$DeployPlanPath = Join-Path $ReleaseDirectory 'deploy-plan.json'
$DeployPlan = [ordered]@{
    operationId = $PlanId
    idempotencyKey = $PlanId
    projectId = $ProjectId
    environment = $EnvironmentName
    action = 'Plan'
    expectedGeneration = $ExpectedGeneration
    releaseManifestPath = Join-Path $ReleaseDirectory 'release-manifest.json'
    artifactDirectory = $ReleaseDirectory
}

$DeployPlan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $DeployPlanPath -Encoding utf8
Get-Content -Raw -LiteralPath $DeployPlanPath

$CompanyOpsCli = Join-Path $InstallRoot 'Cli\companyops.exe'
& $CompanyOpsCli deploy --data-file $DeployPlanPath
```

成功结果必须同时包含：

- `outcome=Succeeded`；
- ReleaseManifest 与制品大小/SHA-256 校验通过；
- EnvironmentBinding 和 generation 校验通过；
- 显示计划目标 release 路径；
- 没有修改服务、IIS、任务计划和 current pointer。

> CLI 默认给 `deploy` 10 分钟、`operate` 2 分钟、只读命令 10 秒；必要时可用 `--timeout-seconds <1-1800>` 在受控范围内覆盖。超时不等于 Agent 没有执行，不要更换幂等键猜测重试；先查看 `audit`、目标目录和 Agent 日志确认真实状态。

---

## 12. 启用写操作前的强制门禁

以下项目缺一项都不能把 `EnableMutations` 改为 true：

- [ ] 使用非生产试点主机；
- [ ] Agent 和 Console 安装包哈希已留档；
- [ ] `catalog` 所有目标声明有效；
- [ ] `projects` 没有 Conflict；
- [ ] nativeName 与真实资源精确一致；
- [ ] 健康探针能区分“正常”和“业务降级”；
- [ ] 端口没有未知监听者；
- [ ] 已有项目安装、备份和回退方案；
- [ ] 数据库变更已单独评审；
- [ ] 试点窗口、负责人和回退负责人已明确；
- [ ] `AllowedProjectInstallRoots` 只包含受审项目父目录，且不包含盘符根目录；
- [ ] 已确认部署引擎不会自动创建原生资源；Windows Service 入口切换已通过本机受审 UAT，其他类型仍会失败关闭；
- [ ] 已获得本次真实启停/安装/更新的单独授权。

启用方式属于主机变更。经过审批后：

1. 停止 CompanyOps.Agent；
2. 备份 `Agent\appsettings.json` 到受控配置备份位置；
3. 填写 `AllowedProjectInstallRoots`，再将 `EnableMutations` 改为 `true`；
4. 启动 CompanyOps.Agent；
5. 执行 `ping`，确认模式变为 `mutations-enabled`；
6. 只操作一个试点组件。

完成试点后建议重新关闭 mutations，直到该主机的正式发布流程、Windows Service 入口切换 UAT 和项目专属 provision 流程全部获批。

---

## 13. 已有原生组件的启停

只有项目显示 `Installed`、目标组件显示 `Owned`，并且 generation 已知时，才能执行。

### 13.1 优先使用 Console

在服务器本机打开：

```text
http://127.0.0.1:19310
```

操作顺序：

1. 点击“刷新状态”；
2. 确认项目不是 Conflict；
3. 确认组件是 Owned；
4. 确认健康探针和最近观测时间；
5. 点击单个组件的“启动”“停止”或“重启”；
6. 阅读确认框中的项目和组件名称；
7. 确认后等待操作完成；
8. 再次刷新；
9. 检查审计记录。

不要连续点击，不要同时从 Console、CLI 和系统工具操作同一个组件。

### 13.2 CLI 结构化请求（仅工程诊断）

`operation.json`：

```json
{
  "operationId": "op-demo-api-restart-20260812-001",
  "idempotencyKey": "op-demo-api-restart-20260812-001",
  "projectId": "demo-api",
  "environment": "production",
  "componentId": "api",
  "action": "Restart",
  "expectedGeneration": 1
}
```

执行：

```powershell
$OperationFile = (Read-Host '请输入已审核的 operation.json 绝对路径').Trim()
$CompanyOpsCli = Join-Path $InstallRoot 'Cli\companyops.exe'
& $CompanyOpsCli operate --data-file $OperationFile
```

每次新的业务动作都使用新的 `operationId` 和 `idempotencyKey`。同一个幂等键不能用于不同请求。

CLI 对 `operate` 默认等待 2 分钟，也可用 `--timeout-seconds <1-1800>` 在受控范围内覆盖。CLI 超时不等于 Agent 没有执行，仍必须查审计和真实资源状态。

---

## 14. Install、Update 和 Rollback：仅工程试点

### 14.1 必须理解的限制

当前 `Install` / `Update` 会：

1. 校验 ReleaseManifest、ZIP、目标架构、最低 Agent 版本和 ProjectManifest SHA-256；
2. 在 `Plan` 阶段确认每个组件都具备发布激活适配器，并只读确认 Windows Service 的精确 SCM/NSSM 入口或 interactiveApp 的精确会话入口与可迁移状态；
3. 预留端口并解包到 `.staging`；
4. 移动为不可变 `releases/<version>`；
5. 对已存在 Windows Service 完成全量入口预检、反向依赖停止、SCM `ImagePath` 切换、依赖启动和声明式健康复核；
6. 所有入口和健康通过后写 `current.release.json`、InstalledState 并提交端口登记；
7. 入口、健康或状态提交失败时恢复旧 `ImagePath`、原运行状态和旧状态文件，并隔离失败 release。

当前不会：

- 创建 Windows Service / IIS site /任务计划；
- 修改 IIS、staticSite、任务计划或 PM2 的发布入口；这些类型会在 `Plan` 阶段失败关闭；
- 注入 Secret；
- 执行数据库迁移；
- 代替真实业务 UAT。

因此 Windows Service 的 `Succeeded` 已代表受控入口切换和声明式健康通过，但仍不能代替真实业务 UAT、数据库或外部系统验收。

### 14.2 工程试点请求

在 Plan 完全通过、写操作已审批后，复制 Plan JSON：

- 把 `action` 改为 `Install` 或 `Update`；
- 使用全新的 operationId；
- 使用全新的 idempotencyKey；
- 首次安装 expectedGeneration 为 0；
- 更新时使用 `projects` 当前 generation。

执行后必须人工验证：

- release 目录存在；
- `current.release.json` 指向正确版本；
- InstalledState generation 增加；
- 原生资源入口是否真的指向新 release；
- 服务是否运行；
- 健康探针是否通过；
- 真实业务 UAT 是否通过。

Windows Service 入口回读不一致时部署会失败，不会再提交成功状态。IIS、staticSite、任务计划和 PM2 在专用激活器完成前不会进入实际部署。

### 14.3 Rollback 的真实含义

Rollback 会先校验上一 release 内嵌的 ReleaseManifest 与 ProjectManifest 哈希，再按同一 Windows Service 激活事务切换旧 `ImagePath`、启动并复核健康，最后回拨 pointer 和 InstalledState。状态提交失败时恢复回滚前入口。

Rollback 当前仍不会自动：

- 修改 IIS physicalPath；
- 恢复数据库；
- 撤销外部系统变更。

工程师必须另行确认原生入口、数据库和业务状态。没有上一版本、上一 Manifest 或可信路径时，Rollback 会失败关闭。

---

## 15. 遗留 PM2 项目接入

新项目不要选择 PM2。本节仅用于已有 PM2 服务。

### 15.1 先确定真实 PM2 owner

必须在当前管理该 PM2 daemon 的 Windows 用户会话中执行：

```powershell
whoami
whoami /user
$NodeExecutable = (Get-Command node.exe -ErrorAction Stop).Source
$Pm2Command = (Get-Command pm2.cmd -ErrorAction Stop).Source
$Pm2Cli = Join-Path (Split-Path -Parent $Pm2Command) 'node_modules\pm2\bin\pm2'

$NodeExecutable
$Pm2Cli
Test-Path -LiteralPath $Pm2Cli
```

记录：

- Windows 账号；
- SID；
- Node 绝对路径；
- PM2 JavaScript CLI 绝对路径；
- 后续 Bridge 缩减快照中的 name、pm_id、pm_cwd、pm_exec_path。

不要在 LocalSystem、管理员的另一个账号或 Agent 服务账号下运行 PM2 命令来猜 owner。不要把完整 `pm2 jlist` 输出复制到日志、工单或文档，因为其中可能包含环境变量。

### 15.2 配置 Bridge

在管理员 PowerShell 中输入上一节记录的真实路径，让 PowerShell 生成 Bridge `appsettings.json`：

```powershell
$BridgeRoot = Join-Path $InstallRoot 'Pm2Bridge'
$BridgeConfigPath = Join-Path $BridgeRoot 'appsettings.json'
$NodeExecutable = (Read-Host '请输入真实 Node.exe 绝对路径').Trim()
$Pm2Cli = (Read-Host '请输入真实 PM2 JavaScript CLI 绝对路径').Trim()
$BridgePipeName = (Read-Host '请输入该 owner 的唯一 PipeName，例如 CompanyOps.Pm2Bridge.SampleOwner.v1').Trim()

if (-not (Test-Path -LiteralPath $NodeExecutable -PathType Leaf)) {
    throw "Node 路径不存在：$NodeExecutable"
}
if (-not (Test-Path -LiteralPath $Pm2Cli -PathType Leaf)) {
    throw "PM2 CLI 路径不存在：$Pm2Cli"
}

$BridgeSettings = [ordered]@{
    Pm2Bridge = [ordered]@{
        PipeName = $BridgePipeName
        ManifestDirectory = Join-Path $DataRoot 'manifests'
        SnapshotDirectory = Join-Path $DataRoot 'Agent\pm2-snapshots'
        NodeExecutablePath = $NodeExecutable
        Pm2CliPath = $Pm2Cli
        SnapshotIntervalSeconds = 10
    }
}

$BridgeSettings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $BridgeConfigPath -Encoding utf8
Get-Content -Raw -LiteralPath $BridgeConfigPath
```

EnvironmentBinding 的 `legacyPm2` 必须对应：

```json
{
  "ownerSid": "真实 owner SID",
  "snapshotFileName": "demo-api.pm2.json",
  "controlPipeName": "CompanyOps.Pm2Bridge.SampleOwner.v1",
  "maxAgeSeconds": 30
}
```

当前 Bridge 需要在共享快照目录中原子写文件。单 owner 试点时，由管理员只给该 owner 对快照目录的 Modify 权限：

```powershell
$OwnerAccount = 'DOMAIN\pm2-owner'
$SnapshotRoot = Join-Path $DataRoot 'Agent\pm2-snapshots'
$SnapshotAcl = Get-Acl -LiteralPath $SnapshotRoot
$SnapshotRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
    $OwnerAccount,
    'Modify',
    'ContainerInherit,ObjectInherit',
    'None',
    'Allow')
$SnapshotAcl.SetAccessRule($SnapshotRule)
Set-Acl -LiteralPath $SnapshotRoot -AclObject $SnapshotAcl
Get-Acl -LiteralPath $SnapshotRoot | Format-List
```

只授权 `$DataRoot\Agent\pm2-snapshots`，不授权整个 `$DataRoot\Agent`。修改前后都要保存 ACL 记录。

### 15.3 试点步骤

1. 保持 Agent mutations=false；
2. 由管理员保存 Bridge 配置；
3. 在真实 PM2 owner 会话前台启动 Bridge：

```powershell
$BridgeRoot = Join-Path $InstallRoot 'Pm2Bridge'
$BridgeExecutable = Join-Path $BridgeRoot 'CompanyOps.Pm2Bridge.exe'
Set-Location $BridgeRoot
& $BridgeExecutable
```

4. 在另一个窗口检查缩减快照只包含有限字段，不包含 env：

```powershell
$SnapshotFileName = (Read-Host '请输入 EnvironmentBinding 中的 snapshotFileName').Trim()
$SnapshotPath = Join-Path $DataRoot (Join-Path 'Agent\pm2-snapshots' $SnapshotFileName)
Get-Content -Raw -LiteralPath $SnapshotPath
```

5. 查看 Agent `projects`；
6. 目标 PM2 组件必须唯一 Matched / Owned；
7. 前台试运行稳定后，再由管理员注册“该 owner 登录时启动”的任务。注册前先确认任务不存在：

```powershell
$OwnerAccount = 'DOMAIN\pm2-owner'
$TaskName = 'CompanyOps-Pm2Bridge-SampleOwner'
$BridgeRoot = Join-Path $InstallRoot 'Pm2Bridge'
$BridgeExecutable = Join-Path $BridgeRoot 'CompanyOps.Pm2Bridge.exe'
$ExistingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -ne $ExistingTask) {
    throw "计划任务已存在，停止并人工核对：$TaskName"
}

$Action = New-ScheduledTaskAction `
  -Execute $BridgeExecutable `
  -WorkingDirectory $BridgeRoot
$Trigger = New-ScheduledTaskTrigger -AtLogOn -User $OwnerAccount
$Principal = New-ScheduledTaskPrincipal -UserId $OwnerAccount -LogonType Interactive -RunLevel Limited

Register-ScheduledTask `
  -TaskName $TaskName `
  -TaskPath '\CompanyOps\' `
  -Action $Action `
  -Trigger $Trigger `
  -Principal $Principal `
  -Description 'CompanyOps PM2 owner bridge，登录时启动'
```

8. 退出前台 Bridge，再让 owner 注销并重新登录，确认只有一个 Bridge 实例和一份持续更新的快照；
9. 获得单独授权后，仅测试一个 pm_id；
10. 确认其他项目的 pm_id、PID 和重启次数没有变化。

设计原则仍然是每个 PM2 owner 使用独立 Bridge 进程、独立配置目录、独立 Pipe 和独立快照文件，多个 owner 不能共用同一份 `appsettings.json`。

当前 MVP 的快照目录 ACL 还没有做到每个 owner 只能改自己的快照文件。因此本手册只允许“一台试点主机一个 PM2 owner”。多 owner 主机必须先实现 per-owner 快照子目录和 ACL 隔离，不能按本节直接上线。

以下任一情况都停止：

- 同名多实例；
- cwd 不一致；
- script 不一致；
- pm_id 与 InstalledState 不一致；
- 快照过期；
- owner SID 不一致；
- Bridge pipe 不可用；
- `jlist` 不是合法 JSON；
- EPERM 或 daemon 权限错误。

---

## 16. 日常巡检

建议每天或变更前执行：

```powershell
$CompanyOpsCli = Join-Path $InstallRoot 'Cli\companyops.exe'
Get-Service -Name CompanyOps.Agent,CompanyOps.Console | Format-Table Name,Status,StartType
& $CompanyOpsCli ping
& $CompanyOpsCli projects
& $CompanyOpsCli audit
```

每周检查：

- manifests 是否存在重复文件；
- 项目是否出现 Conflict / Degraded / Unknown；
- PM2 快照是否过期；
- `$DataRoot\Agent` 所在磁盘剩余空间；
- 项目 release、data、logs 盘剩余空间；
- 审计中是否出现持续失败；
- Console 是否仍只监听 127.0.0.1:19310；
- mutations 是否在无变更窗口时保持关闭。

检查 Console 监听范围：

```powershell
Get-NetTCPConnection -LocalPort 19310 -State Listen | Format-Table LocalAddress,LocalPort,OwningProcess
```

`LocalAddress` 必须是 `127.0.0.1`，不应是 `0.0.0.0`。

---

## 17. 故障排查表

| 现象 | 先检查 | 常见原因 | 安全处理 |
|---|---|---|---|
| Agent 启动失败 | appsettings、Application 日志 | 路径无效、配置错误、ACL | 修复配置；不要反复重装 |
| Console 打不开 | Console 服务、19310 监听 | 服务未运行、端口占用 | 查明占用者；不要结束未知进程 |
| Console 503 | Agent 状态、PipeName | Agent 未运行或 Pipe 不一致 | 先恢复 Agent，只读验证 |
| CLI Pipe 超时 | Agent 状态、管理员身份 | Pipe ACL、Agent忙、CLI 5秒限制 | 查 audit；不要盲目重复写请求 |
| 项目不出现 | hostId、binding | hostId 不匹配 | 修正 binding revision |
| catalog 无效 | Test-OpsManifest | JSON/Schema/语义错误 | 在构建机修正后原子替换 |
| 项目 Conflict | projects.problems | 重复、nativeId 不一致、PM2冲突 | 消除唯一性问题，不绕过 |
| 项目 Degraded | 盘点和资源存在性 | 原生资源缺失 | 使用项目受审安装程序恢复资源 |
| 服务 online 但健康失败 | `/health`、依赖 | 应用未就绪或降级 | 查业务日志，不强行启动下游 |
| PM2 SnapshotStale | Bridge、owner 会话 | Bridge 停止或用户未登录 | 恢复 owner Bridge，不启动第二 daemon |
| deployment 哈希失败 | ZIP 大小和哈希 | 传输损坏或包被覆盖 | 丢弃本次包，重新生成新交付 |
| generation 冲突 | `projects` 当前 generation | 使用了旧请求 | 重新读取状态并人工审阅新请求 |
| resource_busy | 当前操作和审计 | 同资源已有动作 | 等待当前动作结束，不并发重复 |

查看可能相关的 Windows Application 日志：

```powershell
Get-WinEvent -LogName Application -MaxEvents 200 |
  Where-Object { $_.Message -match 'CompanyOps' } |
  Select-Object TimeCreated,LevelDisplayName,ProviderName,Message
```

这只是诊断，不代表所有日志都使用 CompanyOps 作为 ProviderName。

### 17.1 私有 Git 仓库提示无法读取用户名

若“检查更新”提示 `could not read Username`、`terminal prompts disabled` 或 `Authentication failed`，说明项目声明的 HTTPS 仓库需要身份验证，而 CompanyOps Agent 作为 Windows 服务不会弹出 Git 登录窗口。

处理只需三步：

1. 在项目卡片点击“仓库凭据”；
2. 输入对该仓库具有只读权限的用户名和私人令牌，点击“安全保存”；
3. 关闭窗口后重新点击“检查更新”。

不得把令牌拼进 Git URL，也不得写入 `ops/project-manifest.json`。凭据由 Agent 使用 Windows DPAPI 加密保存在主机数据目录，文件只允许 LocalSystem 和本机管理员访问；失败的“检查更新”不会停止服务或修改项目工作树。

### 17.2 Git 被重写到失效镜像

如果输入的是 `https://github.com/...`，错误里却出现 `mirror.ghproxy.com` 或其他镜像域名，说明本机 Git 配置了 URL 重写，不是仓库地址写错。

先只读查看配置来源：

```powershell
git config --show-origin --get-regexp '^url\..*\.insteadof$'
git config --show-origin --get-regexp '^(http|https)\..*proxy$|^(http|https)\.proxy$'
```

看第一列来源：

- 用户目录中的 `.gitconfig`：用户全局配置；
- Git 安装目录中的 `etc\gitconfig`：系统配置；
- 仓库 `.git\config`：当前仓库配置。

只有输出明确包含下面这条失效重写，并且来源是用户全局配置时，才执行对应删除：

```powershell
git config --global --unset-all 'url.https://mirror.ghproxy.com/https://github.com/.insteadof'
if ($LASTEXITCODE -ne 0) {
    throw '删除失效镜像重写失败'
}
```

`url.https://github.com/.insteadof git@github.com:` 是把 GitHub SSH 地址改为官方 HTTPS 地址的规则，不是失效镜像，不要删除。

如果实际输出的镜像地址不同，不要照抄上面的 key；使用输出中的真实配置 key，或交给网络管理员处理。不要未经确认删除企业代理配置。

清理后先验证远程，再克隆：

```powershell
git ls-remote https://github.com/alo58-alt/Ops_Manifest_Specification.git refs/heads/main
if ($LASTEXITCODE -ne 0) {
    throw '仍无法连接 GitHub，请检查 DNS、代理、防火墙和 TLS，不要继续后续步骤'
}

$WorkspaceRootInput = (Read-Host '请输入源码工作区路径；相对路径按当前目录解析').Trim()
if ([string]::IsNullOrWhiteSpace($WorkspaceRootInput)) {
    throw '源码工作区不能为空'
}
$WorkspaceRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkspaceRootInput)
$RepositoryRoot = Join-Path $WorkspaceRoot 'Ops_Manifest_Specification'
New-Item -ItemType Directory -Force -Path $WorkspaceRoot | Out-Null
if (Test-Path -LiteralPath $RepositoryRoot) {
    throw "目标仓库目录已存在，拒绝覆盖：$RepositoryRoot"
}

git clone https://github.com/alo58-alt/Ops_Manifest_Specification.git $RepositoryRoot
if ($LASTEXITCODE -ne 0) {
    throw '克隆失败，停止后续步骤'
}
Set-Location $RepositoryRoot
```

---

## 18. 紧急处置

### 18.1 发现错误写操作风险

1. 停止发起新操作；
2. 记录 operationId、时间、项目、组件和操作者；
3. 查看 `audit`；
4. 停止 CompanyOps.Agent，阻断新的控制请求；
5. 不要删除 Agent 数据库、Manifest 或失败 release；
6. 按业务项目自己的回退方案处理；
7. 保留日志和状态用于复盘。

停止 Agent：

```powershell
Stop-Service -Name CompanyOps.Agent
Get-Service -Name CompanyOps.Agent
```

此动作会停止 CompanyOps 控制平面，但不会自动停止业务服务。

### 18.2 Console 故障但 Agent 正常

可以单独停止 Console：

```powershell
Stop-Service -Name CompanyOps.Console
Get-Service -Name CompanyOps.Agent,CompanyOps.Console
```

不要因为 Console 页面故障就删除 Agent 数据或重装两个服务。

### 18.3 当前不要手工卸载

本仓库尚无受验证的卸载器。不要直接执行递归删除、`sc delete` 或清空 ProgramData。需要卸载时先：

- 备份配置、Manifest、SQLite 和审计；
- 记录服务注册参数和 ACL；
- 确认业务组件不依赖 CompanyOps；
- 编写并验证专用卸载方案；
- 获得明确的删除授权。

---

## 19. 新服务器验收单

```text
[ ] 服务器名称和 HostId 已记录
[ ] Windows 和 .NET Runtime 前置条件满足
[ ] CompanyOps 交付 ZIP SHA-256 一致
[ ] 安装预览已人工核对
[ ] Agent / Console 首次安装成功
[ ] Agent / Console 服务身份正确
[ ] EnableMutations=false
[ ] Console 仅监听 127.0.0.1:19310
[ ] Windows 身份和角色正确
[ ] CLI ping / catalog / inventory / projects / audit 正常
[ ] 未修改防火墙、PM2_HOME 或未知业务进程
[ ] 配置、哈希、安装时间和操作者已留档
```

## 20. 新项目接入验收单

```text
[ ] ProjectManifest 已随项目代码提交
[ ] ProjectManifest 通过 Schema 和语义校验
[ ] 组件类型选择合理，PM2 仅用于遗留项目
[ ] 所有组件依赖存在且无循环
[ ] 健康探针可以识别业务降级
[ ] ReleaseManifest 由固定源码提交生成
[ ] ZIP 大小和 SHA-256 已记录
[ ] EnvironmentBinding 的 hostId 正确
[ ] nativeName 与真实主机资源精确一致
[ ] 端口无冲突且未结束未知监听进程
[ ] Secret 只写 secretRef
[ ] manifests 目录中没有重复声明
[ ] catalog 全部有效
[ ] projects 无 Conflict
[ ] mutations=false 下 Plan 成功
[ ] 数据库备份、迁移和回退另行验收
[ ] 原生资源 provision 仍由项目流程完成；Windows Service 入口切换和失败恢复已另行验收
[ ] 真实业务 UAT 已由负责人签字确认
```

---

## 21. 变更记录模板

每次试点或生产变更至少记录：

```text
变更时间：
目标主机 / HostId：
项目 / 环境：
当前版本 / generation：
目标版本：
ReleaseManifest SHA-256：
制品 SHA-256：
operationId：
idempotencyKey：
操作者：
审批人：
变更前健康：
变更后健康：
业务 UAT 结果：
数据库迁移结果：
回退条件：
回退结果：
遗留风险：
```

---

## 22. 当前推荐落地顺序

不要一次接入所有项目。按下面顺序推进：

1. 在一台非生产服务器安装 CompanyOps；
2. 保持 mutations=false，连续只读运行至少一个观察周期；
3. 接入一个单 Windows Service 项目的 ProjectManifest 和 Binding；
4. 完成 catalog、projects 和 Plan；
5. 使用已存在的 Windows Service 完成入口切换、健康失败恢复和状态提交失败恢复现场 UAT；
6. 分别通过 Console 部署页面和 CLI 提交受控 Plan，核对 generation、固定幂等键与 audit；
7. 对一个项目完成 Install、Update、失败恢复和 Rollback 演练；
8. 按实际项目需要补齐 Secret Provider、数据库备份/迁移和平台升级器；
9. 完成真实账号、权限、DPI、浏览器和业务 UAT；
10. 最后才逐项目迁移遗留 PM2。

第 5、7、9 项完成后，单 Windows Service 项目才可以按受审流程开放生产写入。Secret、数据库或平台升级未由 CompanyOps 接管时，继续沿用项目既有受审流程；多主机集中控制不属于当前阶段目标。
