# M1 只读 Ops Agent

## 能力

当前 Agent 是 .NET 10 Worker Service 工程，但尚未注册到 Windows SCM。它只实现：

- 从受控目录读取五类 JSON Manifest，使用内置 Schema 和语义规则验证；
- 读取 Windows Service 注册状态；
- 读取 IIS applicationHost.config，不存在时明确显示不可用；
- 只读枚举 Windows 任务定义文件，不执行任务；
- 读取 TCP/UDP 监听端点，不结束未知监听进程；
- 将自己的盘点快照和审计事件写入独立 SQLite；
- 通过受 ACL 保护的 Named Pipe 提供本机查询。

不存在服务启停、安装、更新、端口分配、IIS 写入、任务注册、PM2 控制或远程 shell。

## 本地查询协议

默认 Pipe 名为 CompanyOps.Agent.v1，协议版本为 ops-agent/v1。请求必须是单行 UTF-8 JSON，
上限 64 KiB。当前白名单命令：

| 命令 | 结果 |
|---|---|
| ping | Agent 版本、主机 ID 和只读模式 |
| inventory | 最近一次主机盘点快照 |
| catalog | 最近一次 Manifest 校验结果 |
| audit | 最近 50 条 Agent 自身审计事件 |

Pipe ACL 默认只允许当前运行身份、LocalSystem 和本机 Administrators。正式安装器后续可以创建
专用本地操作员组，并将其 SID 写入 Ops:AllowedClientSids。Agent 不按名称猜测本地组。

## 开发态运行

先构建：

~~~powershell
dotnet restore .\OpsManifest.slnx --configfile .\NuGet.config
dotnet build .\OpsManifest.slnx --no-restore
~~~

在一个 PowerShell 窗口中使用隔离状态目录运行：

~~~powershell
$env:Ops__StateDirectory = Join-Path $env:TEMP 'CompanyOps-Agent-Dev'
$env:Ops__ManifestDirectory = Join-Path $PWD 'examples\valid'
$env:Ops__PipeName = 'CompanyOps.Agent.Dev'
dotnet .\src\Ops.Agent\bin\Debug\net10.0-windows\CompanyOps.Agent.dll
~~~

在另一个窗口查询：

~~~powershell
dotnet .\src\Ops.Cli\bin\Debug\net10.0-windows\companyops.dll ping --pipe CompanyOps.Agent.Dev
dotnet .\src\Ops.Cli\bin\Debug\net10.0-windows\companyops.dll catalog --pipe CompanyOps.Agent.Dev
dotnet .\src\Ops.Cli\bin\Debug\net10.0-windows\companyops.dll inventory --pipe CompanyOps.Agent.Dev
dotnet .\src\Ops.Cli\bin\Debug\net10.0-windows\companyops.dll audit --pipe CompanyOps.Agent.Dev
~~~

开发态验证结束后用 Ctrl+C 停止。此流程不会注册 Windows Service。

## SQLite 状态

Agent 只在自身状态目录创建 ops-agent.db，当前表为：

- schema_info：Agent 状态库版本；
- inventory_snapshots：最多保留最近 100 份只读盘点；
- audit_events：Agent 启动和盘点失败审计。

业务数据库、项目上传文件和项目配置不进入该状态库。

## PM2 边界

pm2 jlist 在某些环境可能自动拉起 daemon。主机 Agent 未来通常以 LocalSystem 运行，而现有 PM2
daemon 属于某个交互用户；未绑定 owner 时直接查询可能连接或创建错误 daemon。因此当前
pm2-legacy 来源固定返回 Unavailable，且不会查找、安装或启动 PM2。

后续实现必须先登记 owner 身份和既有 IPC，再由 node.exe 直接调用 PM2 JS CLI取得缩减快照。
只保留 name、pm_id、pm_cwd、pm_exec_path、状态、PID 和重启次数；按 Manifest 与
EnvironmentBinding 计算的精确 cwd/script 判定唯一归属。M1 仍然只读，不执行任何控制。

## 现场验证记录

2026-08-11 使用临时 SQLite 和独立 Pipe 前台验证：

- ping 返回 FIELD-VALIDATION、read-only；
- 5 份示例 Manifest 全部有效；
- Windows Service 来源可用，读取 413 项；
- 网络监听来源可用，读取 357 项；
- IIS 未安装，正确返回 Unavailable；
- 任务定义来源可用，当前读取 0 项；
- PM2 按上述安全规则返回 Unavailable；
- audit 返回 Agent 启动事件；
- 验证后 Agent 已停止，未注册服务。

这些数字只是本次开发主机快照，不是生产资产基线。
