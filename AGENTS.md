# Ops Manifest Specification 项目规则

通用协作、工作树保护、分层验证及授权连续性见 `C:\Users\zheng\.codex\AGENTS.md` 和 `..\AGENTS.md`；本文件只维护平台契约与主机资源边界。

## 项目定位

- 本仓库定义 Windows 多项目统一运维平台的声明式契约，并逐步承载主机级 Ops Agent 与统一 Ops Console。
- Agent、Console、SessionAgent 与安装工具的实际实现以 `src/`、`tests/` 和配置模型为准；阶段与验收记录见 `docs/roadmap.md`、`docs/project-onboarding-standard.md`，历史“已完成/待完成”不替代当前候选或现场证据。
- `Ops:EnableMutations` 的代码默认值为 false，不据此推断任一已部署主机的开关。自动化使用假适配器、任务自有临时目录和隔离状态库，不得借测试控制本机现有服务、PM2 daemon、IIS 或任务计划；真实操作仅在当前授权明确覆盖对应主机、资源和副作用时执行。
- PM2 仅是迁移期 `pm2Legacy` 适配器，不是新架构的运行时基础。

## 契约变更要求

- `spec/v1/schemas` 是 v1 契约的机器可读权威来源；`docs/specification-v1.md` 解释其语义。
- 修改 Schema 时必须同步检查有效示例、无效示例、校验器和契约测试。
- v1 已发布字段不得改变既有含义；破坏性修改必须建立新的主版本目录。
- Manifest 只能声明有限、可审计的能力，不得提供任意 PowerShell、CMD、脚本或提权命令入口。
- Secret 只允许引用，不得进入项目声明、发布声明、环境绑定、安装状态或端口登记的明文值。

## Windows 安全边界

- 项目声明不拥有主机资源；端口、服务账户、安装目录和路由由 Ops Agent 根据环境绑定分配。
- 真实主机资源的写操作均须采用预检、精确目标、操作门禁、有限超时、健康复核、审计和失败回滚；普通源码/文档编辑不套用主机运维门禁。
- 遗留 PM2 操作必须按精确名称、规范化 cwd、精确脚本唯一匹配后，仅按 `pm_id` 控制；禁止 `PM2_HOME` 隔离、`stop all`、`delete all` 和 `kill`。
- 当前仓库验证可以只读盘点主机状态；未经单独的现场操作授权，不得启动、停止、重启、安装、更新或改写任何真实服务。

## 开发与验证

- 默认使用简体中文文档和 PowerShell 7 命令。
- 文本沿用项目 UTF-8 与 JSON 两空格约定；面向 Windows PowerShell 5.1 的含中文发布脚本按 `docs/project-onboarding-standard.md` 保留所需 UTF-8 BOM，不因格式偏好全仓转码。
- .NET SDK 以 `global.json` 为准，工程入口为 `OpsManifest.slnx`；测试分别位于 `tests/Ops.Agent.Tests`、`tests/Ops.Console.Tests`、`tests/Ops.SessionAgent.Tests` 和 `tests/Ops.Setup.Tests`，从受影响项目/失败节点开始。
- 单份或选定声明使用 `tools/Test-OpsManifest.ps1 -Path <明确文件>`，目录递归仅在任务确需覆盖该目录时使用。完整契约集合入口 `tests/Run-ContractTests.ps1` 仅在契约、校验器、共享依赖或发布门禁触发时运行。
- Console 前端以 `src/Ops.Console/ClientApp/package.json` scripts 为准，不因文档改动运行 npm 安装或构建。
- Markdown/指令仅检查内容、引用和差异，不运行全方案构建/测试、打包或真实运维。现场 UAT 缺口不自动阻塞独立代码或文档交付，也不能被 Mock 结果填平。
- `tools/` 与安装/更新命令可能写文件或控制主机；名称含 Test、Check、Plan 不证明无副作用，先查参数与依赖。操作测试限定假适配器和任务自有资源，真实资源另按当前授权判断。
- 优先维护上述现有文档；命令教程和历史现场步骤按当前目标选用，不自动跨项目接入、补健康接口、生成声明或发布。无需变更时不创建交付物。

- 未经相应明确授权，不安装系统级依赖、不部署 Agent/Console、不注册服务、不修改端口或防火墙；同目标、主机、资源与副作用范围内的明确授权持续有效。是否本地提交按当前任务判断，不作为所有任务的完成条件；未经明确授权不推送。
