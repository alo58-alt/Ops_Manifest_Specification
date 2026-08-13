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

L3 有两条互斥路径：

- 小型兼容更新可声明 `update.source.kind=gitFastForward`，CompanyOps 只允许 HTTPS 远端、声明分支、干净工作树和 fast-forward。依赖清单变化、前端源码未同时交付构建产物、本地分支分叉或远端 URL 不符时拒绝；
- 依赖、迁移、入口或制品布局变化必须由构建/CI 生成 `ReleaseManifest + ZIP + SHA-256`，再走受控发布。

私有 HTTPS 仓库不得把账号、密码或私人令牌写入 `remoteUrl`、ProjectManifest 或仓库文件。项目接入后，由授权运维者在 Console 的项目卡片点击“仓库凭据”，为声明的精确远端配置一次只读凭据。凭据只保存在 CompanyOps 主机数据目录，由 Agent 使用 Windows DPAPI（LocalMachine）加密，并把文件 ACL 限制为 LocalSystem 和本机管理员；审计只记录配置结果，不记录明文。凭据缺失或被远端拒绝时，更新检查失败关闭，且不会停止业务服务或修改工作树。

`ReleaseManifest` 是每个版本的发布产物，不应作为长期静态文件手工维护。当前“已存在的 Windows Service（含 NSSM 承载）”与 `interactiveApp` 已完成同一声明式发布激活代码闭环；IIS、静态站点、计划任务和遗留 PM2 在没有对应现场验收前，不获得 L3 更新权限。

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

GUI 项目声明只允许项目目录内的 `.exe`，不允许 `.ps1` / `.cmd` / `.bat` 或 Shell 字符串。Session Agent 会二次核验 project/environment/component、EXE、工作目录与参数，并只停止自己精确启动的进程。

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

PM2 接入额外要求名称、规范化 cwd、精确 script 唯一匹配；只按 `pm_id` 控制，禁止 `stop all`、`delete all` 和 `kill`。

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
3. 输入服务器项目目录，例如 `D:\project\webquizbot`，点击“检查项目”；
4. 检查结果必须唯一匹配原生资源，并证明 Windows Service/IIS 入口位于项目目录内；
5. 点击“确认只读接入”；CompanyOps 原子导入 ProjectManifest，并为当前主机生成 EnvironmentBinding；
6. 下方出现正确的 `项目 ID / 环境 / 组件`；L1 显示 `Declared / DeclaredOnly` 是正常结果；
7. 接入后自动执行声明式健康探针，但不会创建 InstalledState；
8. Agent 完成受控安装后，才允许显示 `Installed / Owned`；
9. 名称、真实原生资源、路径、端口或已有声明无法唯一对应时必须失败关闭；
10. 接入测试不得启动、停止、更新或重建业务服务；写操作另行授权。

仅把 `ops` 目录复制到服务器不会自动注册，必须在 Console 中执行上述一次接入。不得手工伪造 InstalledState。

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
