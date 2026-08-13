# CompanyOps MVP 运维手册

## 1. 进程与权限

| 进程 | 推荐身份 | 权限 | 说明 |
|---|---|---|---|
| `CompanyOps.Agent` | 专用高权限服务账号或 LocalSystem | 主机资源控制 | 每台主机一个；Named Pipe 是唯一写入口 |
| `CompanyOps.Console` | NetworkService 或专用低权限账号 | 只访问 Agent Pipe | 仅监听 `127.0.0.1:19310` |
| `CompanyOps.Pm2Bridge` | 真实 PM2 daemon owner | 只访问该 owner 的 PM2 | 仅存量 PM2 项目需要，不以高权限 Agent 身份运行 |
| 项目组件 | 各自服务账号 | 最小目录/网络权限 | 不获得 Agent 或其他项目权限 |

Console 服务账号 SID 必须加入 Agent 的 `Ops:AllowedClientSids`。NetworkService SID 是 `S-1-5-20`。生产环境不要让所有 Authenticated Users 访问 Agent Pipe。

## 2. 配置目录

程序目录和数据目录由安装者选择，并通过 `-InstallRoot`、`-DataRoot` 显式传给安装器。建议固定结构而不是固定盘符：

```text
<InstallRoot>\Agent\
<InstallRoot>\Console\
<InstallRoot>\Pm2Bridge\
<DataRoot>\manifests\
<DataRoot>\Agent\ops-agent.db
<DataRoot>\Agent\pm2-snapshots\
<项目安装根目录>\releases\<version>\
<项目数据根目录>\
<项目日志根目录>\
```

Agent 生产配置至少明确：

```json
{
  "Ops": {
    "HostId": "WIN-OPS-01",
    "ManifestDirectory": "<DataRoot>\\manifests",
    "StateDirectory": "<DataRoot>\\Agent",
    "Pm2SnapshotDirectory": "<DataRoot>\\Agent\\pm2-snapshots",
    "PipeName": "CompanyOps.Agent.v1",
    "InventoryIntervalSeconds": 30,
    "EnableMutations": false,
    "AllowedProjectInstallRoots": ["<ApprovedProjectParentRoot>"],
    "AllowedClientSids": ["S-1-5-20"]
  }
}
```

首次上线保持 `EnableMutations=false`，完成只读盘点、归属解释和冲突修正后，再针对试点主机单独变更。`AllowedProjectInstallRoots` 是主机管理员批准的项目父目录；每个 `EnvironmentBinding.roots.install` 必须是其中某一项的子目录，不能直接等于共享父目录或盘符根目录，不同项目的安装目录也不能相同或互相嵌套。未配置时 `Plan` 和所有部署写入失败关闭。

发布与首次安装脚本默认都不触碰现有业务服务：

```powershell
# 只生成 artifacts\publish
pwsh -NoProfile -File .\tools\Publish-OpsPlatform.ps1

$InstallRoot = (Read-Host '请输入 CompanyOps 程序目录绝对路径').Trim()
$DataRoot = (Read-Host '请输入 CompanyOps 数据目录绝对路径').Trim()

# 只显示安装计划；显式传入用户选择的目录
& .\tools\Install-OpsPlatform.ps1 -InstallRoot $InstallRoot -DataRoot $DataRoot

# 在提升的 PowerShell 中显式首次安装；仍不启动，mutations=false
& .\tools\Install-OpsPlatform.ps1 -InstallRoot $InstallRoot -DataRoot $DataRoot -Apply -Confirm
```

完整的路径校验、预览和首次安装步骤见[傻瓜式完整操作手册](complete-operations-manual.md)第 5 章。

首次安装脚本发现同名服务或已有安装目录会拒绝覆盖。它不是升级器；平台自身升级必须采用后续签名、版本化安装流程。

## 3. 项目接入顺序

1. 项目仓库提交 `ProjectManifest`，不得写主机绝对路径、账号密码或真实 Secret；
2. CI 构建 ZIP，计算 SHA-256，生成 `ReleaseManifest`；
3. 运维为具体主机创建 `EnvironmentBinding`，分配 nativeName、根目录、端口和 Secret 引用；
4. 将声明放入 Agent Manifest 目录，先查询 `catalog` 与 `projects`；
5. 只有状态无 Conflict，才执行 `deploy` 的 `Plan`；
6. 试点授权后启用 mutations，执行 Install/Update；
7. 观察 InstalledState generation、健康和审计；故障时执行 Rollback。

项目不主动向 Agent 注册。Agent 发现声明后构建项目视图；所有主机资源仍由 EnvironmentBinding 和 Agent 决定。

## 4. CLI

只读查询：

```powershell
companyops ping
companyops catalog
companyops inventory
companyops projects
companyops audit
```

结构化操作通过 JSON 文件，CLI 不接受 shell 字符串：

```powershell
companyops operate --data-file .\operation.json
companyops deploy --data-file .\deployment.json
```

CLI 默认超时为：`deploy` 10 分钟、`operate` 2 分钟、其他命令 10 秒；仅在受审场景使用 `--timeout-seconds <1-1800>` 覆盖。任何超时都先查 `audit` 和真实资源状态，不得更换幂等键盲目重试。

`operate` 示例：

```json
{
  "operationId": "op-20260812-001",
  "idempotencyKey": "sample-prod-api-restart-20260812-001",
  "projectId": "sample-system",
  "environment": "production",
  "componentId": "api",
  "action": "Restart",
  "expectedGeneration": 3
}
```

任一条件都会失败关闭：项目/环境不唯一、generation 变化、InstalledState/nativeId 不一致、资源盘点缺失、依赖未归属、健康失败、资源锁冲突或 mutations 关闭。

## 5. 发布与回滚语义

- `Plan` 校验 ReleaseManifest、目标架构、最低 Agent 版本、ProjectManifest SHA-256、项目 generation 和发布激活能力，并只读确认每个 Windows Service 或 interactiveApp 的精确入口与可迁移状态，不修改系统；
- `Install/Update` 先原子预留端口，再解包到 `.staging/<operationId>`；
- ZIP 每个目标必须位于 staging 内，禁止覆盖已有 release；
- 发布先对全部组件做精确预检，再按反向依赖停止、切换声明式入口、按依赖启动并复核声明式健康；原生服务切换 SCM `ImagePath`，NSSM 服务切换其应用入口，interactiveApp 切换 Session Agent 共享的当前入口状态；
- 原生入口与健康全部通过后，才提交 pointer、InstalledState 和端口；状态提交失败时恢复旧入口、原运行状态和旧状态文件；
- 失败 release 移入 `.failed/<operationId>`，便于取证，不覆盖旧版本；
- `Rollback` 只接受 pointer 记录、仍位于本项目 `releases` 根下且内嵌 ReleaseManifest/ProjectManifest 哈希可信的上一版本。

当前已存在 Windows Service（含 NSSM 承载）与 interactiveApp 具备发布激活代码闭环；不自动创建服务或执行项目脚本。IIS、静态站点、Task Scheduler 和 PM2 发布会在 `Plan` 阶段失败关闭，其既有资源启停仍走独立白名单适配器。数据库变更必须另行设计备份/兼容性策略。

当前恢复保证覆盖受控异常、取消、健康失败以及 pointer / InstalledState / 端口提交失败；主机断电或 Agent 进程在入口切换与状态提交之间崩溃时，尚无跨进程持久化激活日志。该能力完成前只允许有人值守维护窗口，必须预先留存旧 SCM `ImagePath` 和状态文件恢复点。

## 6. 现场验收清单

- [ ] Agent/Console 二进制签名和哈希已留档；
- [ ] 服务账号与目录 ACL 最小化；
- [ ] Console 仅 loopback，企业防火墙无额外入站规则；
- [ ] `projects` 无 Conflict，PM2 快照 owner SID 正确；
- [ ] mutations=false 下所有写请求均被拒绝；
- [ ] 试点组件精确 start/stop/restart，不影响其他项目；
- [ ] 依赖或健康失败时后续步骤停止；
- [ ] 更新失败保留旧 pointer，失败 release 被隔离；
- [ ] Windows Service 的 SCM/NSSM 入口与 interactiveApp 当前入口写入后回读一致，失败时旧入口和原运行状态恢复；
- [ ] Rollback 后健康重新通过；
- [ ] 审计包含同一 operationId、动作和结果；
- [ ] 业务负责人完成真实 UAT。
