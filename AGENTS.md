# Ops Manifest Specification 项目规则

## 项目定位

- 本仓库定义 Windows 多项目统一运维平台的声明式契约，并逐步承载主机级 Ops Agent 与统一 Ops Console。
- 当前阶段为 Windows MVP 的 M1-M4 实现；真实写操作默认禁用，开发与自动化测试只能使用假适配器、临时目录和隔离状态库，不得借测试控制本机现有服务、PM2 daemon、IIS 或任务计划。
- PM2 仅是迁移期 `pm2Legacy` 适配器，不是新架构的运行时基础。

## 契约变更要求

- `spec/v1/schemas` 是 v1 契约的机器可读权威来源；`docs/specification-v1.md` 解释其语义。
- 修改 Schema 时必须同步检查有效示例、无效示例、校验器和契约测试。
- v1 已发布字段不得改变既有含义；破坏性修改必须建立新的主版本目录。
- Manifest 只能声明有限、可审计的能力，不得提供任意 PowerShell、CMD、脚本或提权命令入口。
- Secret 只允许引用，不得进入项目声明、发布声明、环境绑定、安装状态或端口登记的明文值。

## Windows 安全边界

- 项目声明不拥有主机资源；端口、服务账户、安装目录和路由由 Ops Agent 根据环境绑定分配。
- 任何写操作均须采用预检、精确目标、操作门禁、有限超时、健康复核、审计和失败回滚。
- 遗留 PM2 操作必须按精确名称、规范化 cwd、精确脚本唯一匹配后，仅按 `pm_id` 控制；禁止 `PM2_HOME` 隔离、`stop all`、`delete all` 和 `kill`。
- 当前仓库验证可以只读盘点主机状态；未经单独的现场操作授权，不得启动、停止、重启、安装、更新或改写任何真实服务。

## 开发与验证

- 默认使用简体中文文档和 PowerShell 7 命令。
- 修改文件使用 UTF-8；JSON 使用两个空格缩进。
- 完整契约验证入口：

```powershell
pwsh -NoProfile -File .\tests\Run-ContractTests.ps1
```

- 验证单个或多个声明：

```powershell
pwsh -NoProfile -File .\tools\Test-OpsManifest.ps1 .\examples\valid\*.json
```

- 未经明确授权，不安装其他系统级依赖、不部署 Agent/Console、不注册服务、不修改端口或防火墙、不提交或推送 Git。
