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

建议固定布局：

```text
C:\Program Files\CompanyOps\Agent\
C:\Program Files\CompanyOps\Console\
C:\Program Files\CompanyOps\Pm2Bridge\
C:\ProgramData\CompanyOps\manifests\
C:\ProgramData\CompanyOps\Agent\ops-agent.db
C:\ProgramData\CompanyOps\Agent\pm2-snapshots\
C:\CompanyOps\Apps\<project>\releases\<version>\
C:\CompanyOps\Data\<project>\
C:\CompanyOps\Logs\<project>\
```

Agent 生产配置至少明确：

```json
{
  "Ops": {
    "HostId": "WIN-OPS-01",
    "ManifestDirectory": "C:\\ProgramData\\CompanyOps\\manifests",
    "StateDirectory": "C:\\ProgramData\\CompanyOps\\Agent",
    "Pm2SnapshotDirectory": "C:\\ProgramData\\CompanyOps\\Agent\\pm2-snapshots",
    "PipeName": "CompanyOps.Agent.v1",
    "InventoryIntervalSeconds": 30,
    "EnableMutations": false,
    "AllowedClientSids": ["S-1-5-20"]
  }
}
```

首次上线保持 `EnableMutations=false`，完成只读盘点、归属解释和冲突修正后，再针对试点主机单独变更。

发布与首次安装脚本默认都不触碰现有业务服务：

```powershell
# 只生成 artifacts\publish
pwsh -NoProfile -File .\tools\Publish-OpsPlatform.ps1

# 只显示安装计划
pwsh -NoProfile -File .\tools\Install-OpsPlatform.ps1

# 在提升的 PowerShell 中显式首次安装；仍不启动，mutations=false
pwsh -NoProfile -File .\tools\Install-OpsPlatform.ps1 -Apply -Confirm
```

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

- `Plan` 校验 ReleaseManifest、项目和 generation，不修改系统；
- `Install/Update` 先原子预留端口，再解包到 `.staging/<operationId>`；
- ZIP 每个目标必须位于 staging 内，禁止覆盖已有 release；
- 成功后移动为 `releases/<version>`，提交 pointer、InstalledState 和端口；
- 失败 release 移入 `.failed/<operationId>`，便于取证，不覆盖旧版本；
- `Rollback` 只接受 pointer 记录且仍位于本项目 `releases` 根下的上一版本。

当前 activation 确认不可变 release 已就绪；SCM/IIS/Task Scheduler 的运行控制仍走独立白名单适配器和健康门禁。数据库变更必须另行设计备份/兼容性策略。

## 6. 现场验收清单

- [ ] Agent/Console 二进制签名和哈希已留档；
- [ ] 服务账号与目录 ACL 最小化；
- [ ] Console 仅 loopback，企业防火墙无额外入站规则；
- [ ] `projects` 无 Conflict，PM2 快照 owner SID 正确；
- [ ] mutations=false 下所有写请求均被拒绝；
- [ ] 试点组件精确 start/stop/restart，不影响其他项目；
- [ ] 依赖或健康失败时后续步骤停止；
- [ ] 更新失败保留旧 pointer，失败 release 被隔离；
- [ ] Rollback 后健康重新通过；
- [ ] 审计包含同一 operationId、动作和结果；
- [ ] 业务负责人完成真实 UAT。
