# Ops Manifest v1 规范

## 1. 契约分层

五类文档不是同一份大配置的不同名字，而是由不同主体维护的权威记录。

| 文档 | 写入主体 | 是否随源码提交 | 权威内容 |
|---|---|---:|---|
| ProjectManifest | 项目开发者 | 是 | 项目需要什么 |
| ReleaseManifest | CI/发布系统 | 随制品 | 本次发布具体包含什么 |
| EnvironmentBinding | Ops Agent/授权运维者 | 否 | 这台主机如何满足需求 |
| InstalledState | Ops Agent | 否 | 实际安装了什么 |
| PortRegistry | Ops Agent | 否 | 主机端口归谁所有 |

任何一层不得越权。例如 ProjectManifest 可以申请名为 `http` 的动态 TCP 端口，但不能宣称端口已经属于自己；EnvironmentBinding 才能写入实际分配值。

## 2. 通用字段

所有文档必须包含：

- `$schema`：指向对应 v1 Schema 的相对或发布 URL；
- `apiVersion`：当前固定为 `ops.company/v1`；
- `manifestKind`：五类文档之一；
- `metadata`：文档自身身份信息。

项目 ID、组件 ID、端口名等稳定标识使用小写字母开头的 DNS 风格字符串，只允许小写字母、数字和连字符。

## 3. ProjectManifest

ProjectManifest 包含：

- 项目显示名和负责人；
- 有限类型的组件清单；
- 组件依赖及其健康解锁关系；
- HTTP、TCP 或文件心跳探针；
- 动态或固定端口需求及暴露范围；
- 普通配置与 Secret 引用需求；
- 持久数据目录和备份等级；
- 更新策略和失败回滚要求。

组件依赖必须引用同一项目内存在的组件，不得自依赖或形成环。`pm2Legacy` 必须提供精确名称、cwd 和 script，供 Agent 做唯一归属校验。

## 4. ReleaseManifest

ReleaseManifest 由构建过程生成，不由现场人工编辑。每个制品至少包含文件名、字节数和 64 位小写 SHA-256。组件载荷只能引用已声明的制品，并给出制品内部相对路径；禁止盘符、UNC 和父目录跳转。

`projectManifestSha256` 必须是本次构建所使用 ProjectManifest 原始文件字节的 SHA-256；Agent 会在 `Plan` 阶段与当前唯一声明精确比对。当前 Windows Service 激活器只解析启动参数中的 `${PORT_<PORT_ID>}`，其中连字符转换为下划线，例如 `api-http` 对应 `${PORT_API_HTTP}`；未知、Secret 或未绑定占位符失败关闭。Windows Service 的 `workingDirectory` 如存在，只能等于入口文件所在目录，且服务本身仍应以 `AppContext.BaseDirectory` 等可靠基准解析资源。

同一项目版本一旦发布，其 ReleaseManifest 和制品必须不可变。需要修复时发布新版本，不能替换旧版本同名 ZIP。

## 5. EnvironmentBinding

遗留 PM2 项目必须通过 legacyPm2 绑定 daemon owner SID、离线缩减快照文件名、owner 专属 controlPipeName 和最大年龄。
主机 Agent 不根据用户名猜测 owner，也不在 LocalSystem 上下文直接执行可能自动拉起新 daemon 的
pm2 jlist。快照只能包含归属判定需要的有限字段，不能复制完整环境变量。

EnvironmentBinding 只保存非敏感绑定：主机 ID、环境名、安装/数据/日志根目录、服务账户引用、端口分配、路由和配置值。敏感配置只能使用 `secretRef`，禁止同时出现明文 `value`。

实际路径必须是绝对 Windows 路径。正式 Agent 还必须进一步验证路径位于管理员允许的根目录下；Schema 只完成格式层约束，不能替代 ACL 和规范化路径校验。

## 6. InstalledState

InstalledState 是 Agent 的事实快照，不是用户期望状态。它记录当前与上一版本、ReleaseManifest 哈希、组件适配器、Windows 原生资源 ID、安装时间和最近操作结果。

健康状态不得只来自 PID、端口监听或“服务已注册”。绿色状态必须同时满足资源归属、运行状态和所有必需健康探针。

## 7. PortRegistry

PortRegistry 以主机为范围登记 `protocol + address + port`。同一组合只能有一个活动预留；端口发现只能报告未知监听者，不能因为端口冲突直接终止未知进程。

端口状态：

- `reserved`：事务已预留但服务尚未验证监听；
- `active`：服务归属和监听状态均已确认；
- `releasing`：进入显式释放事务，尚未完成复核。

## 8. 版本策略

- `ops.company/v1` 内允许增加向后兼容的可选字段和新的枚举能力。
- 删除字段、收紧既有合法输入或改变字段语义必须建立 `v2`。
- Agent 遇到未知主版本必须失败关闭；遇到未知组件类型不得猜测执行。

## 9. 当前限制

v1 首个增量尚未定义防火墙规则、证书自动签发、数据库迁移执行器、备份提供方、Agent 自更新和多主机调度。这些能力必须继续采用有限资源模型，不得以“自定义脚本”绕过契约治理。
