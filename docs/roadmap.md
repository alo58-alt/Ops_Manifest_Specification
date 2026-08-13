# 开发路线与验收状态

## M0：契约地基 — 已完成

- 五类 v1 JSON Schema、有效/无效示例、语义校验 CLI；
- 唯一 ID、引用、依赖环、端口冲突和发布载荷校验；
- 架构、信任边界和版本策略。

自动化验收：契约测试全部通过，过程不接触真实 Windows 资源。

## M1：只读 Agent — 已完成代码与隔离现场验证

- .NET 10 Windows Worker、Manifest Catalog、SQLite 状态/审计；
- SCM、IIS、Task Scheduler、监听端口和 PM2 缩减快照；
- ProjectManifest + EnvironmentBinding + InstalledState + 主机盘点聚合；
- 受 ACL 保护的 Named Pipe 与诊断 CLI。

PM2 Agent 不直接执行 `jlist`；只有 owner 用户下的 Bridge 生成缩减快照。

## M2：安全控制 — 已完成代码与假适配器验证

- 精确单组件 start/stop/restart；
- generation、幂等、资源级 Operation Gate、依赖拓扑；
- HTTP、TCP、文件心跳健康门禁；
- SCM、IIS site、Task Scheduler 固定能力适配器；
- PM2 owner bridge 每次控制前重新验证 name、pm_id、cwd、script，仅按数字 pm_id 操作。

默认 `EnableMutations=false`。尚需单独授权的现场 UAT：为一个试点项目启用 mutations，逐项验证权限、超时、健康失败和回滚，不得在共享 PM2 上做广域动作。

## M3：部署、更新与回滚 — Windows Service 代码闭环已完成，现场 UAT 待完成

- ReleaseManifest、大小和 SHA-256；
- ZIP 绝对路径、`..` 逃逸、符号链接、条目数和展开大小限制；
- 主机端口 SQLite `IMMEDIATE` 批量事务和通配地址冲突；
- `.staging` 到不可变 `releases/<version>` 的同卷移动；
- 原子 `current.release.json`、InstalledState generation、失败目录隔离；
- 上一 release 存在性/路径边界校验和回滚 pointer。
- ReleaseManifest 目标架构、最低 Agent 版本和 ProjectManifest SHA-256 绑定校验；
- Windows Service 精确入口预检、反向依赖停止、SCM `ImagePath` 切换、依赖启动、声明式健康复核和失败恢复；
- pointer、InstalledState 或端口提交失败时恢复旧原生入口、原运行状态和旧状态文件；
- 主机级 Windows Service、IIS site 和任务计划声明冲突检测；
- IIS、静态站点、任务计划和 PM2 发布激活器缺失时在 `Plan` 阶段失败关闭，不再把 release 目录存在当作发布成功。

MVP 不接受项目传入任意迁移脚本。真实数据库迁移、备份提供方和破坏性变更仍属于后续受控能力，不得把文件回滚当作数据库回滚。

自动化使用假入口、假控制适配器和临时状态目录，没有修改本机真实 SCM、NSSM 或用户进程。正式生产写入仍需在试点主机完成 Windows Service + interactiveApp 的 Install、Update、健康失败恢复和 Rollback 现场演练。

## M4：Ops Console — 已完成 MVP

- ASP.NET Core 10 + Vue 3/TypeScript；
- 仅绑定 `127.0.0.1`，Windows Negotiate；
- reader/operator/admin、Allowlist、CSRF 和浏览器安全头；
- 项目、组件、版本、归属、健康和审计；
- 受控 Plan / Install / Update / Rollback 表单，明示本机路径、generation 和固定幂等键；
- 高风险确认与按钮级禁用，Agent 仍执行独立服务端校验；
- 无远程 shell、无任意命令接口。

既有浏览器验收已覆盖真实 Windows 身份、只读角色、刷新、项目/审计数据和 390×844 视口；新增部署表单已通过 TypeScript/Vite 生产构建，但真实 operator 提交和试点主机写入尚未执行。

## 下一阶段（不阻塞 MVP 代码交付）

- 试点项目现场 UAT 与正式 Windows Service 安装；
- 数据库备份/迁移提供方、日志分页/流式进度；
- HTTPS 反向代理或企业内网远程 Console（当前只允许本机）；
- Agent/Console 签名安装包、升级策略和灾备演练；
- 跨进程持久化激活日志与 Agent 启动恢复，封住断电或进程崩溃窗口；
- IIS/静态站点/任务计划的专用发布激活器。

多主机编排、HA 和集中身份平台不是当前阶段目标；每台主机独立部署 Agent 与本机 Console 即满足当前范围。
