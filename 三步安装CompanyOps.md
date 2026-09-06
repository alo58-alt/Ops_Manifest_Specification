# CompanyOps 安装与升级

## 日常升级：开发机编译，自动传包，服务器安装

CompanyOps 和业务项目都默认使用预编译制品。Git push 发布源码；开发机完成编译、测试和制品校验后，发布工具把文件自动传到服务器。服务器不编译，不运行测试，不需要 Git、.NET SDK、Node.js/npm 或 PowerShell 7。

### 开发端准备并发布

在开发电脑的 Ops 仓库中完成提交及相关检查，然后执行：

```powershell
pwsh -NoProfile -File .\tools\Build-CompanyOpsSetup.ps1
pwsh -NoProfile -File .\tools\Publish-CompanyOpsPackage.ps1 `
  -PackageRoot .\artifacts\setup\package\CompanyOps-Offline `
  -FeedRoot '\\s4090\project\CompanyOps-Packages\platform'
```

`FeedRoot` 是本次明确授权的服务器发布目录，其他主机应改为自己的共享路径。发布工具在临时目录传送文件并逐一验证 SHA-256 和大小，再将完整源码提交作为不可变版本目录，最后原子更新 `latest.json`。传送失败时旧索引保持不变；同名版本目录已存在时拒绝覆盖。安装器及依赖也在逐文件记录内，不只校验业务载荷。工具同时生成服务器使用的 `更新CompanyOps.cmd` 和 PowerShell 脚本，不启动安装器、不操作服务。

开发机依赖由仓库 `global.json` 和前端清单限定；这些依赖不转移给服务器。旧的源码更新工具保留为 `tools\Update-CompanyOpsFromSource.ps1`，仅供明确选择源码构建的工程场景，日常入口不会调用它。

### 服务器升级

在服务器发布目录双击 `更新CompanyOps.cmd`。本例入口为：

```text
D:\project\CompanyOps-Packages\platform\更新CompanyOps.cmd
```

脚本使用 Windows 自带 PowerShell 5.1，校验发布索引、全包哈希、文件大小和路径，再打开 Setup。核对原程序目录、数据目录及既有授权后点击“升级并启动”。仓库根目录的同名入口会让操作者选择含 `latest.json` 的发布目录，不会拉取源码或构建。

### SSH 命令行升级

通过 SSH 使用现有 Session Agent 所属的管理员账号执行。程序只接受既有安装，保留程序目录、数据目录、会话归属及授权配置；旧版本、包版本或目录与请求不一致时拒绝，不会转为首次安装。

在服务器 PowerShell 中先准备明确的参数，读取安装包与当前程序的完整提交版本：

```powershell
$feed = 'D:\project\CompanyOps-Packages\platform'
$install = 'D:\CompanyOps'
$data = 'D:\CompanyOpsData'
$version = (Get-Item -LiteralPath "$install\Agent\CompanyOps.Agent.dll").VersionInfo.ProductVersion
if ($version -notmatch '\+([a-f0-9]{40})$') { throw '无法识别已安装版本' }
$from = $Matches[1]
& "$feed\tools\Update-CompanyOps.ps1" -FeedRoot $feed -Unattended `
  -FromRevision $from -InstallRoot $install -DataRoot $data
```

未加 `-Apply` 时只预检并返回 `Planned`；确认目标后用相同参数增加 `-Apply` 执行。相同版本返回 `AlreadyCurrent`。只有实际切换和健康检查通过才返回 `Upgraded`；失败返回非零退出码并复用 Setup 失败恢复。日志及机器可读结果位于发布目录 `upgrade-logs`。此入口不弹 GUI，不改变主机授权，不注册首套服务；图形安装器的 UAC 仍由操作者确认。

更新工具只检查文件时使用 `-FeedRoot <目录> -CheckOnly`，不会启动安装器。CMD 入口采用 ASCII + CRLF；会由 Windows PowerShell 5.1 执行且含中文的脚本采用 UTF-8 with BOM，避免服务器代码页差异。

### 业务项目

WebQuizBot 等业务项目由各自开发仓库构建和发布 `ProjectManifest + ReleaseManifest + ZIP`，传到服务器后使用 Console“更新项目”或本机 CLI 的 Plan / Update。项目默认不声明服务器 Git 构建来源。已有项目保留安装根、数据目录、日志目录和原生资源绑定；声明变化先按接入规则同步。Ops 只有自身发生变化时才需要升级。

## 首次安装：使用开发机生成的离线包

## 第一步：生成安装包

在开发电脑双击：

```text
生成CompanyOps安装包.cmd
```

等待窗口显示 `[DONE]`。程序会自动打开生成文件所在目录。

## 第二步：复制并解压一个 ZIP

把下面这个文件复制到目标服务器：

```text
output\CompanyOps-Offline-win-x64.zip
```

服务器收到后，右键 ZIP 选择“全部解压”。不要直接在压缩包预览窗口里运行程序。

## 第三步：安装

进入解压后的 `CompanyOps-Offline\Setup`，双击 `CompanyOps-Setup.exe`：

1. 在 UAC 提示中选择“是”；
2. 选择程序存放位置，安装程序自动新建 `CompanyOps`；
3. 选择数据存放位置，安装程序自动新建 `CompanyOpsData`；
4. 如需让 CompanyOps 执行项目版本更新，勾选“启用受控项目更新”，选择项目父目录。比如项目位于 `D:\project\webquizbot`，选择 `D:\project`；磁盘根目录不能作为授权范围；
5. 首次安装点击“安装并启动”；检测到旧版时，目录和已有授权会自动带出，点击“升级并启动”。

这项授权只在安装或以后需要改变管理范围时配置，不需要每次发布重新填写。安装器会把允许目录写入 Agent 配置，并随本次安装或升级统一重启 CompanyOps 自身服务。

安装程序会先按 `Payload.sha256.json` 逐文件校验，再自动完成文件复制、配置生成、Windows 服务注册、服务启动和本机访问检查。成功后会自动打开 CompanyOps Console。ZIP 是透明离线包，服务器仍不需要安装 .NET。

升级时安装程序会先停止 CompanyOps 自身的 Agent 和 Console，等待旧进程完全退出，再原子切换目录；失败会恢复并重新启动旧版。出现失败恢复提示时不要删除程序目录，也不要反复点击同一个旧安装包，应改用修复后的新包。

如果安装失败，不要继续手工执行命令。安装程序会显示失败原因并自动恢复本次创建的服务和目录。
