# 项目接入 CompanyOps 开发规范

> 适用范围：部署到 Windows 主机、需要被 CompanyOps 盘点或运维的所有项目
> 当前契约：`ops.company/v1`
> 默认准入等级：L1 只读接入

## 1. 一句话执行方式

进入具体项目后，让开发人员或 AI 完整阅读本规范，并完成：

1. 在项目根目录建立 `ops\project-manifest.json`；
2. 建立 `ops\README.md`，写清运行事实和验收结果；
3. 为每个运行组件提供可自动判断成功或失败的健康探针；
4. 运行 CompanyOps 的项目材料验收脚本，直到 L1 全部通过。

不要为每个项目开发专用接入 EXE。项目只生成标准材料，主机绑定由 CompanyOps 在服务器上完成。

## 2. 三个准入等级

### 2.1 L1：只读接入，所有项目首先完成

目标：CompanyOps 能识别项目、组件、依赖、端口需求和健康方式，并且不会误认同机其他资源。

项目必须交付：

```text
<ProjectRoot>\
└─ ops\
   ├─ project-manifest.json
   └─ README.md
```

验收结果：

- `project-manifest.json` 通过 v1 Schema 和语义校验；
- 每个组件都有健康探针；
- 组件依赖无缺失、自依赖和循环；
- 声明不包含服务器绝对路径、HostId、真实账号密码或 Secret 值；
- 项目首次进入 Console 时允许显示 `Declared / DeclaredOnly`；这代表声明已接入，但尚未由 Agent 证明实际资源归属；
- 出现 `Conflict` 不合格，必须先解决重复项目 ID、原生资源名、安装根目录或端口冲突。

### 2.2 L2：服务控制

目标：在 L1 已证明唯一归属后，由 CompanyOps 精确启动、停止和重启既有原生服务。

当前已完成现场试点的是 Windows Service。L2 不读取远端代码、不改项目文件、不安装依赖，也不等同于发布成功。服务控制前后仍会重复校验项目目录、原生服务名和健康探针。

### 2.3 L3：受控更新与发布

在 L1 基础上增加：

- 可重复的 Windows 构建流程；
- 构建产物 ZIP；
- 构建时自动生成的 `ReleaseManifest`；
- 制品大小和 SHA-256；
- `projectManifestSha256`；
- 启动入口、参数和工作目录；
- 更新失败回滚验证；
- 数据库、配置和持久数据的兼容或备份方案。

L3 有三条互斥路径：

- 小型兼容更新可声明 `update.source.kind=gitFastForward`，CompanyOps 只允许 HTTPS 远端、声明分支、干净工作树和 fast-forward。依赖清单变化、前端源码未同时交付构建产物、本地分支分叉或远端 URL 不符时拒绝；
- 已提供标准发布脚本的独立项目可声明 `update.source.kind=gitBuildRelease` 与 `buildProfile=projectReleaseV1`。接入时分别选择源码目录和安装目录；CompanyOps 快进源码仓库，只接受目标提交上唯一的 `v<SemVer>` 标签，只调用固定的 `tools\Build-CompanyOpsRelease.ps1`，然后把生成的 `ReleaseManifest + ZIP + SHA-256` 送入通用受控发布；
- 不允许服务器构建或不满足固定约定的项目，由构建/CI 生成 `ReleaseManifest + ZIP + SHA-256`，再手工选择发布目录走受控发布。

`gitBuildRelease` 不在 CompanyOps 仓库内复制业务项目，也不要求两个仓库合并。服务器保留一个独立业务源码 clone；`EnvironmentBinding.roots.source` 绑定该 clone，`roots.install` 绑定不可变 release 根。两个目录必须位于管理员配置的受控项目父目录之下，并且不能相同或互相嵌套。构建环境必须预先装好项目明确需要的 SDK/虚拟环境/前端依赖；Agent 不在发布时临时安装依赖。构建失败时当前业务 release 不停止、不切换。

私有 HTTPS 仓库不得把账号、密码或私人令牌写入 `remoteUrl`、ProjectManifest 或仓库文件。项目接入后，由授权运维者在 Console 的项目卡片点击“仓库凭据”，为声明的精确远端配置一次只读凭据。凭据只保存在 CompanyOps 主机数据目录，由 Agent 使用 Windows DPAPI（LocalMachine）加密，并把文件 ACL 限制为 LocalSystem 和本机管理员；审计只记录配置结果，不记录明文。凭据缺失或被远端拒绝时，更新检查失败关闭，且不会停止业务服务或修改工作树。

项目从旧声明增加新组件时，先把项目目录更新到包含新 `ProjectManifest` 的受审提交，再通过“接入现有项目”重新检查并确认。CompanyOps 保留既有组件原生绑定，允许向声明和 EnvironmentBinding 增加新组件；删除组件、改变既有组件 kind 或改绑既有原生资源均失败关闭。已有绑定在未重新填写数据、日志目录时沿用原值。增量声明不会启停服务，新组件仍须由精确主机盘点证明归属，随后才能进入受控发布。

旧项目首次转入 `gitBuildRelease` 时，原绑定可能把源码目录同时作为 `roots.install`。仅当旧绑定没有 `roots.source`、项目没有 InstalledState、旧目录没有 `current.release.json` 或 `releases`、清单目录没有无效文件时，重新接入才允许把**原安装目录保留为源码目录**，并选择另一个**已存在且为空、互不嵌套的安装目录**。原数据、日志和既有原生绑定必须保持不变；此操作只同步声明，不移动文件或启停服务。预检后若出现发布状态或目录变化，确认时会重新检查并拒绝。已有发布状态的项目仍不能借重新接入搬迁安装目录。

接入向导中的端口解析顺序为：操作者明确填写的新端口、当前主机既有 EnvironmentBinding 实际端口、ProjectManifest 的 `preferredPort`。界面默认只展示解析结果，不要求重复填写；“指定新端口”留空表示沿用，只有操作者输入新值才形成端口变更计划。

`ReleaseManifest` 是每个版本的发布产物，不应作为长期静态文件手工维护。当前“已存在的 Windows Service（含 NSSM 承载）”与 `interactiveApp` 已完成同一声明式发布激活代码闭环；IIS、静态站点、计划任务和遗留 PM2 在没有对应现场验收前，不获得 L3 更新权限。

日常操作从 Console“项目与组件”中的更新入口进入。`gitBuildRelease` 项目点击“检查更新”读取远端标签和变更，再点击“构建并更新”；不需要 FTP、共享目录、复制 ZIP 或手工解压。其他项目在“更新项目”中选择同时包含 `release-manifest.json` 与发布 ZIP 的目录。Console 自动区分首次 Install 和后续 Update。哈希、大小、generation、归属、组件启停、健康检查与失败恢复均由 Agent 执行，Plan、Install、Update 和 SHA-256 不要求日常操作人员手工选择或计算。

### 2.4 Git 构建发布的项目复用流程

本节是项目开发者落实 `gitBuildRelease` 的通用入口。发布人员的 Console 操作见[完整操作手册 11.1](complete-operations-manual.md#111-console-图形页面普通运维优先)。新项目复用同一契约和发布事务，不复制 WebQuizBot 的端口、原生服务名称、浏览器依赖或业务数据。

1. **发布前固定源码。** 在项目自己的独立仓库准备代码和受版本控制的产物，完成相关验证，再由有权限的发布者提交并发布目标分支和唯一 `v<SemVer>` 标签。当前实现选择的是声明分支的远端 HEAD，该提交没有标签时会拒绝，不会自动退回最近一个旧标签。Git push 完成只证明远端代码可取得。
2. **在干净 clone 复现构建。** 固定项目所需 SDK、Python 虚拟环境、前端依赖与网络策略。对会进入载荷或参与哈希的文本/构建文件明确 `.gitattributes`，使 Windows checkout 不改变预期字节。被跟踪的前端产物必须和源码同步；不能删除“构建后工作树必须干净”的门禁来放行旧产物。不要把 `node_modules`、虚拟环境、账号库或认证状态复制进发布包。
3. **实现固定构建约定。** `tools/Build-CompanyOpsRelease.ps1` 接收 `Version`、`ReleaseId`、`OpsSpecificationRoot`、`OutputDirectory`，核对 `COMPANYOPS_EXPECTED_SOURCE_REVISION` 与实际 HEAD，生成完整组件载荷及 `release-manifest.json`。Manifest 中的项目、版本、ReleaseId、sourceRevision 与实际构建一致，ProjectManifest 哈希与主机已接入声明的字节一致。脚本仅构建，不启动业务进程或控制服务。
4. **发布前跑隔离事务演练。** 使用下方通用命令把实际 ZIP 接入真实部署引擎，验证失败路径。每次输出到新的独立目录，保留输入哈希、步骤和审计；演练标签/提交只在隔离仓库使用，不冒充已发布版本。
5. **现场完成绑定和验收。** 在明确的目标主机上核对独立的 source/install 目录、既有原生资源、持久数据根、只读仓库凭据与操作权限。ProjectManifest 变化时先按现有重新接入规则同步声明；仅字节或换行变化也可能触发哈希不一致，不能绕过校验。之后由有权限的操作者在 Console 执行“检查更新 → 构建并更新”，记录安装版本、原生入口、健康和一次真实业务操作；需要交互进程的项目另核对登录会话。数据库回滚与断电恢复必须单独验收。

开发机在本规范仓库执行（需预先具备本仓库的 .NET SDK/NuGet 依赖，脚本不安装系统依赖）：

```powershell
$ProjectManifest = (Read-Host '请输入构建对应的 ProjectManifest 绝对路径').Trim()
$ReleaseManifest = (Read-Host '请输入实际发布目录中 release-manifest.json 的绝对路径').Trim()
pwsh -NoProfile -File .\tools\Test-ProjectReleaseRehearsal.ps1 `
  -ProjectManifestPath $ProjectManifest `
  -ReleaseManifestPath $ReleaseManifest
```

此工具只支持当前具有激活实现的 `windowsService` / `interactiveApp` 组合。输入声明和制品只读；它在 `artifacts/release-rehearsals/<运行 ID>` 建立临时主机绑定、安装目录、持久数据哨兵与隔离 SQLite，不连接 Agent、不注册服务、不启动发布包中的 EXE、不访问业务端口。生产部署引擎、原生激活编排、ZIP 解包、Schema、SHA-256、pointer、InstalledState、端口登记和审计使用真实实现，原生控制与健康探针使用假适配器。

服务器默认构建宿主是 Windows PowerShell 5.1。随 Agent 发布的 `New-ProjectRelease.ps1` 按需加载 ZIP 程序集，以流式 SHA-256 计算哈希，并调用同目录的自包含 `CompanyOps.Agent.exe --validate-release <Manifest> <ArtifactDirectory>` 校验 Schema、制品哈希和大小。该入口在创建 Host 之前返回，不启动服务、盘点或写入 Agent 状态；服务器不需要为校验额外安装 PowerShell 7 或 .NET SDK。规范源码目录仍使用 `Test-OpsManifest.ps1` 和开发机 PowerShell 7。发布前必须验证实际安装包布局，不能只在源码工具目录构建通过。

默认从候选包的**同一载荷**衍生 `0.0.0-rehearsal.baseline`，因此证明的是部署事务，不证明两个业务版本之间的数据兼容。可通过 `-BaselineReleaseManifestPath` 传入另一个真实基线包（须匹配同一 ProjectManifest 契约），通过 `-OutputDirectory` 指定不存在的输出目录。报告 `rehearsal-result.json` 显式记录是否衍生基线；每次演练覆盖：

| 检查 | 通过标准 |
|---|---|
| Plan 与首次 Install | Plan 不执行控制；安装后生成不可变 release，generation 为 1 |
| 篡改制品哈希 | 拒绝候选包，不切换入口或 pointer |
| 候选版本健康探针异常 | 恢复全部原入口及运行状态；旧 pointer、InstalledState 字节不变；失败目录隔离 |
| 更新失败的端口归属 | 旧版有效登记仍阻止其他项目占用，不随失败预留一起删除 |
| Update 与幂等重放 | 更新后 generation 为 2；同一请求重放不再次启停 |
| 显式 Rollback | 回到基线入口，generation 为 3；数据哨兵不变；审计可追溯 |

#### WebQuizBot 试点经验（2026-09-06）

本轮实际生成 `3.0.4-rehearsal.20260906` 的 API + BrowserHost 发布包并完成上述隔离演练。源码 revision 为隔离 clone 的 `78d9a4df937e133a4cf8161af61686d0cb7e43fe`，不是用户仓库的正式发布提交；ZIP 为 97,506,055 字节，SHA-256 为 `f4889d85b74736403b333363e64b3a76e62e71194c4a7a0218bfce193deb98e1`。本机证据保留在 `artifacts/release-rehearsals/webquizbot-20260906/rehearsal-result.json`（忽略产物，不进入 Git）。项目侧构建命令及包内容证据维护在 WebQuizBot 的 `ops/README.md`。

本次发现并落实三条可复用经验：

- 真实远端源码第一次构建被工作树门禁拒绝，暴露出前端源码变更没有同步受跟踪的 `dist`。发布者应在提交前同步产物，且在全新 clone 再验证一次，不能靠开发机已有产物判断可发布。
- Windows `core.autocrlf` 会改变构建输入或产物字节。项目用 `.gitattributes` 固定前端源码 LF，并让受跟踪 `dist` 不作文本换行转换；发布一致性需要验证 checkout 后的实际字节。
- 故障注入重现了“旧版入口已恢复，但旧端口登记被删除”的缺陷。CompanyOps 已修复：更新预留保留同一归属的 active 登记，失败仅释放本次新 reserved 记录；另一操作也不能劫持同一归属的未完成预留。回滚验收应同时查入口、pointer、InstalledState、端口和审计。

本轮未执行真实 SCM / 登录会话切换、生产数据库备份恢复、Agent 崩溃恢复或外部业务验收；隔离演练和构建通过不提升为生产验收通过。

## 3. 谁负责生成什么

| 材料 | 生成者 | 是否提交项目仓库 | 内容 |
|---|---|---:|---|
| `ops/project-manifest.json` | 项目开发者 | 是 | 项目、组件、依赖、健康、端口需求、配置需求、数据目录 |
| `ops/README.md` | 项目开发者 | 是 | 项目真实启动方式、健康语义、数据/日志/构建说明 |
| `ReleaseManifest` | 构建或 CI | 随制品 | 版本、入口、文件大小、SHA-256 |
| `EnvironmentBinding` | CompanyOps / 授权运维者 | 否 | 服务器 HostId、绝对路径、实际端口、原生服务名、账号引用 |
| `InstalledState` | CompanyOps Agent | 否 | 实际安装版本、generation、原生资源 ID、运行与健康状态 |
| `PortRegistry` | CompanyOps Agent | 否 | 主机端口最终归属 |

项目开发者不得手工伪造 `InstalledState` 来让 Console 显示绿色。

## 4. ProjectManifest 编写要求

### 4.1 项目身份

- `metadata.id` 使用稳定的小写短横线 ID，例如 `contract-review-system`；创建后不要因目录改名而修改；
- 禁止使用 `sample-system`、`demo-api`、`change-me` 等占位值交付；
- `displayName` 使用用户能识别的名称；
- `owners` 至少包含一个真实团队或职责角色，不写密码、邮箱 Token 或个人凭据。

### 4.2 组件拆分

只有能独立启停、独立健康判断或有依赖顺序的运行单元才拆成组件。

| 运行形态 | `kind` | `EnvironmentBinding.nativeName` 的服务器含义 |
|---|---|---|
| Windows 服务 | `windowsService` | SCM ServiceName，不是 DisplayName |
| IIS 应用/站点 | `iisSite` | IIS Site 名称 |
| IIS 静态站点 | `staticSite` | IIS Site 名称 |
| Windows 计划任务 | `scheduledTask` | 完整 TaskPath |
| 存量 PM2 服务 | `pm2Legacy` | PM2 精确 name，并额外核验 cwd 和 script |
| 桌面窗口、托盘、摄像头或浏览器自动化 | `interactiveApp` | 项目内相对 `.exe` 路径、工作目录、参数、登录用户 SID |

`interactiveApp` 不是新建一个 Windows Service。CompanyOps 安装器只为当前操作员注册一个“仅用户登录时运行”的 Session Agent，所有 GUI 项目通过这一个会话代理启停。用户未登录时必须显式不可用，不得退回 Session 0，不得启用“允许服务与桌面交互”。

GUI 项目声明只允许项目目录内的 `.exe`，不允许 `.ps1` / `.cmd` / `.bat` 或 Shell 字符串。Session Agent 会二次核验 project/environment/component、EXE、工作目录与参数，并只停止自己精确启动或在登记丢失后按“同一绝对 EXE 路径 + 同一登录 Session + 唯一父子进程树”接续登记的进程树。存在两个独立树根或父子关系断裂时失败关闭，不按进程名猜测。

首次走不可变制品 Install 时，ProjectManifest 声明的交互 EXE 可以尚未出现在旧项目目录中；此时组件只能保持 `Declared / Missing`，不能执行 L2 启停。只有 ReleaseManifest 为该组件提供了通过哈希校验的 `.exe` 载荷，L3 Plan 才允许把它作为“首次安装入口”切换。若已存在交互入口登记、运行进程或 InstalledState，而旧 EXE 消失，仍按冲突失败关闭，不能借首次安装规则绕过归属校验。

GUI 组件必须包含 `interactiveProcess` 健康探针；如果程序还能提供 HTTP、TCP 或心跳文件，可以同时声明，CompanyOps 会全部校验。

文件心跳默认相对于 `roots.data`；程序把心跳写入日志根时，声明 `rootRef=logs`。可选根仅限 `install|data|logs`，不得在探针中写服务器绝对路径。

同一个后端同时托管前端静态文件时，通常只声明一个服务组件，不要虚构第二个前端进程。

### 4.3 依赖

- `dependsOn` 只引用同一 ProjectManifest 内的组件 ID；
- 先启动的组件是依赖项，例如 Worker 依赖 API，则 Worker 的 `dependsOn` 写 API；
- 不得自依赖或形成循环；
- 依赖解锁以健康探针通过为准，不以 PID 或端口存在代替。

### 4.4 健康探针

本规范的 L1 要求每个组件至少一个探针：

- Web/API：优先 HTTP，使用本机可访问路径；最好用 `expectJson` 断言业务状态；
- 非 HTTP 服务：使用 TCP；
- Worker：使用原子更新的文件心跳，并限制最大年龄；
- 定时任务：使用反映最近一次成功完成时间的文件心跳；最大年龄应覆盖任务周期和允许延迟；
- 健康接口不得修改业务数据、启动任务或依赖人工登录；
- HTTP 200 但数据库、队列或关键依赖不可用时，不应返回“健康”。

健康探针只证明声明的范围。外部供应商、真实浏览器、硬件或业务 UAT 仍需单独验收。

### 4.5 端口

- 每个监听端口建立独立稳定 ID；
- 固定协议端口使用 `allocation=fixed` 和 `preferredPort`；
- 可由主机分配的端口使用 `allocation=dynamic`；
- `exposure=loopback` 表示只允许本机访问；`lan` 表示业务需要局域网访问；
- ProjectManifest 只表达需求，最终端口归 `EnvironmentBinding` 和 `PortRegistry` 管理；
- 禁止因为端口冲突结束未知进程。

### 4.6 配置与 Secret

- 普通配置只声明键、类型、是否必填和用途；
- 密钥、Token、密码、Cookie、证书密码只声明 `type=secret`；
- 项目声明、ReleaseManifest、EnvironmentBinding、日志和接入材料中都不得出现 Secret 明文；
- 主机绑定只能使用 `secretRef`，不能使用明文 `value` 保存敏感信息。

### 4.7 数据与日志

- 数据库、上传文件、浏览器会话、缓存、模型和任务状态应明确是否持久化；
- 每个需要保护的数据目录声明备份等级；
- `critical` 必须有备份和恢复验收；
- 运行数据不得塞入版本化程序目录后随更新覆盖；
- `ops/README.md` 写清真实数据和日志位置的决定方式，但不要提交某台服务器的绝对路径。

## 5. 项目仓库禁止出现的材料

以下内容属于主机状态，不得提交到项目仓库：

- `EnvironmentBinding`；
- `InstalledState`；
- `PortRegistry`；
- 服务器 HostId；
- `C:\...`、`D:\...` 等具体服务器安装/数据/日志路径；
- 生产 Windows 账号、SID、PM2 owner SID；
- `.env`、密码、Token、Cookie、私钥、生产证书或 Secret 值；
- 为制造绿色状态而手工填写的运行状态、PID、pm_id 或健康结果。

ProjectManifest 中 `pm2Legacy.cwd` 和 `pm2Legacy.script` 是项目内相对路径，是允许的；不得写绝对路径。

## 6. 按运行形态选择模板

| 运行形态 | 模板 |
|---|---|
| Windows Service | `templates\project-onboarding\windows-service\ops\project-manifest.json` |
| IIS 应用/站点 | `templates\project-onboarding\iis-site\ops\project-manifest.json` |
| IIS 静态站点 | `templates\project-onboarding\static-site\ops\project-manifest.json` |
| Windows 计划任务 | `templates\project-onboarding\scheduled-task\ops\project-manifest.json` |
| 遗留 PM2 | `templates\project-onboarding\pm2-legacy\ops\project-manifest.json` |

一个项目有多种组件时，把所需模板中的组件、端口和数据目录合并进同一份 ProjectManifest；不要建立多份 ProjectManifest。必须替换所有 `change-me` 相关值、组件名称、端口和健康语义，不要只改显示名称。

PM2 接入额外要求名称、规范化 cwd、精确 script 唯一匹配；只按 `pm_id` 控制，禁止 `stop all`、`delete all` 和 `kill`。项目开发者只负责在 ProjectManifest 声明这三个相对值，绝不提交 owner SID、Pipe、快照文件名或 `PM2_HOME`。运维只需在每个真实 PM2 owner 下运行一次 CompanyOps 的“配置PM2主机接管”，以后每个 PM2 项目都由接入页自动识别 owner 并生成主机绑定。

## 7. ops/README.md 必须回答的问题

使用模板：

`templates\project-onboarding\ops-README.template.md`

至少回答：

1. 项目 ID 和负责人是谁；
2. 有哪些长期运行组件；
3. 每个组件真实入口和运行形态是什么；
4. 组件依赖顺序是什么；
5. 健康探针成功和失败分别代表什么；
6. 申请哪些端口、是否需要 LAN 暴露；
7. 哪些目录需要持久化和备份；
8. 配置和 Secret 从哪里引用；
9. 本地构建、自动测试和受控 UAT 分别如何验证；
10. 还存在哪些 CompanyOps 未接管的能力。

## 8. 自动验收

在 CompanyOps 规范仓库根目录执行：

```powershell
$ProjectRoot = (Read-Host '请输入待接入项目根目录绝对路径').Trim()
pwsh -NoProfile -File .\tools\Test-ProjectOnboarding.ps1 `
    -ProjectRoot $ProjectRoot `
    -Level L1
```

L2 服务控制材料验证：

```powershell
pwsh -NoProfile -File <OpsSpecRoot>\tools\Test-ProjectOnboarding.ps1 `
    -ProjectRoot <ProjectRoot> `
    -Level L2
```

L3 更新材料验证：

```powershell
$ProjectRoot = (Read-Host '请输入项目根目录绝对路径').Trim()
$ReleaseManifest = (Read-Host '请输入本次 ReleaseManifest 绝对路径').Trim()
$ArtifactDirectory = (Read-Host '请输入本次制品目录绝对路径').Trim()

pwsh -NoProfile -File .\tools\Test-ProjectOnboarding.ps1 `
    -ProjectRoot $ProjectRoot `
    -Level L3 `
    -ReleaseManifestPath $ReleaseManifest `
    -ArtifactDirectory $ArtifactDirectory
```

脚本通过只证明材料和制品的静态契约正确，不证明服务器资源、真实健康、外部服务或生产 UAT 已通过。

构建或 CI 可复制 `templates\project-onboarding\release-recipe.json` 为项目自己的 `ops\release-recipe.json`，先把已构建文件放入独立载荷目录，再调用：

```powershell
pwsh -NoProfile -File .\tools\New-ProjectRelease.ps1 `
    -ProjectManifestPath <ProjectRoot>\ops\project-manifest.json `
    -RecipePath <ProjectRoot>\ops\release-recipe.json `
    -PayloadDirectory <PayloadDirectory> `
    -OutputDirectory <EmptyReleaseDirectory> `
    -Version 1.2.3 `
    -ReleaseId project-1.2.3-build.1 `
    -SourceRevision <GitCommit>
```

该工具只压缩项目已构建载荷并生成 ReleaseManifest，不执行配方中的命令，也不接受 PowerShell、批处理或任意脚本字段。新项目只需维护标准声明和构建步骤，不得为其在 CompanyOps 中新增项目专用更新分支。

## 9. 服务器接入测试标准

材料通过 L1 后，部署到服务器再做接入测试：

1. 确认服务器上的项目目录已经包含 `ops\project-manifest.json` 和 `ops\README.md`；
2. 打开 CompanyOps Console 的“接入现有项目”；
3. 点击“选择目录…”，在服务器目录选择器中选取项目源码目录（例如 `D:\CompanyOps\Sources\webquizbot`）；如果声明为 `gitBuildRelease`，再选择独立安装目录（例如 `D:\CompanyOps\Projects\webquizbot`），然后点击“检查项目”；
4. 检查结果必须唯一匹配原生资源，并证明 Windows Service/IIS 入口位于项目目录内；PM2 项目还必须显示同一个 owner 下每个组件的精确 name、pm_id、cwd、script；
5. 点击“确认只读接入”；CompanyOps 原子导入 ProjectManifest，并为当前主机生成 EnvironmentBinding；已接入项目无需重复接入，声明变化时从项目卡点击“同步声明”，系统会自动回显原生资源名称和当前绑定端口；
6. 下方出现正确的 `项目 ID / 环境 / 组件`；L1 显示 `Declared / DeclaredOnly` 是正常结果；
7. 接入后自动执行声明式健康探针，但不会创建 InstalledState；
8. Agent 完成受控安装后，才允许显示 `Installed / Owned`；
9. 名称、真实原生资源、路径、端口或已有声明无法唯一对应时必须失败关闭；
10. 接入测试不得启动、停止、更新或重建业务服务；写操作另行授权。

仅把 `ops` 目录复制到服务器不会自动注册，必须在 Console 中执行上述一次接入。不得手工伪造 InstalledState。PM2 项目的现有配置与 Secret 不导入 CompanyOps，接入不会读取它们；如果没有新鲜 owner 发现快照、多个 owner 同时命中、同名多实例或 cwd/script 不一致，按钮必须保持禁用。

## 10. 在具体项目中交给 AI 的标准任务

复制下面文字，将规范仓库路径和目标项目路径替换为实际值：

```text
请完整阅读并遵守：
<Ops规范仓库>\docs\project-onboarding-standard.md

目标项目：<项目根目录>

请为该项目完成 CompanyOps L1 只读接入材料：
1. 先阅读项目级 AGENTS.md/README、确认 Git 根目录和现有脏工作树；
2. 识别所有真实长期运行组件、依赖、端口、健康、数据、日志和 Secret 需求；
3. 生成 ops/project-manifest.json 和 ops/README.md；
4. 必要时实现无副作用健康接口或 Worker/定时任务心跳；
5. 不生成或提交 EnvironmentBinding、InstalledState、PortRegistry 和服务器绝对路径；
6. 使用 Ops 规范仓库的 tools/Test-ProjectOnboarding.ps1 执行 L1 验收；
7. 运行与改动相称的项目测试；
8. 报告自动验证、尚未验证的服务器/外部服务/真实业务 UAT，以及服务器接入时仍需提供的主机参数；
9. 不擅自部署、重启、提交或推送。
```

## 11. 完成定义

项目只有同时满足以下条件，才能声明“L1 材料完成”：

- [ ] `ops/project-manifest.json` 通过自动验收；
- [ ] `ops/README.md` 与当前代码、启动方式和数据事实一致；
- [ ] 每个组件至少一个无副作用健康探针；
- [ ] 项目 ID、组件 ID、端口 ID 稳定且无占位符；
- [ ] 依赖顺序可形成 DAG；
- [ ] 无服务器绝对路径和 Secret 明文；
- [ ] 项目自身最小构建/测试通过；
- [ ] 自动测试与服务器、外部服务、真实业务 UAT 的证据已分开表述；
- [ ] 未执行未经授权的部署、启停、更新、提交或推送。
