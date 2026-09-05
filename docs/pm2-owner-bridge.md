# PM2 Owner Bridge

## 为什么必须有 Bridge

Windows 上的 PM2 daemon 属于启动它的用户会话。高权限 Agent 直接运行 `pm2 jlist` 可能连接或拉起另一个 daemon，因此不能把 Agent 用户、`PM2_HOME` 技巧或共享 PM2 GUI 当作隔离边界。

`CompanyOps.Pm2Bridge` 必须由真实 PM2 owner 用户启动。它只有两项能力：

1. 执行固定 Node 可执行文件和固定 PM2 JavaScript CLI 的 `jlist`，删去环境变量等敏感字段，并在项目接入前持续原子写入 owner 发现快照；
2. 接收受 ACL 保护的结构化请求，每次重新 `jlist`，精确验证 name、数字 pm_id、规范化 cwd 和 script，随后只执行 `start|stop|restart <pm_id>`。

Bridge 没有 `all`、`delete`、`kill` 或任意命令字段，代码也不读取、设置或改写 `PM2_HOME`。

## 普通运维只做两步

1. 在真实 PM2 owner 登录的桌面上，双击 CompanyOps 程序目录 `Pm2Bridge\配置PM2主机接管.cmd`。它会自动识别当前账号 SID、Node、PM2 CLI、CompanyOps 数据目录，验证当前 daemon，配置 owner 专属 Pipe 和登录任务，并等待发现快照生成。一个 PM2 owner 只做一次；升级会保留配置。
2. 回到 CompanyOps Console，选择包含 `ops\project-manifest.json` 的服务器项目目录，点击“检查项目”，确认所有 PM2 组件显示同一个 owner 且 name、pm_id、cwd、script 唯一匹配后，点击“确认只读接入”。

Console 自动生成 EnvironmentBinding 的 `legacyPm2`。不要把 SID、快照名或 Pipe 复制到项目仓库，也不需要为每个项目生成接入 EXE。项目声明中的必填配置和 Secret 仍由既有运行环境持有，发现与接入过程不会读取或复制这些值。

## 工程排障配置

在 PM2 owner 账号下准备独立 `appsettings.json`。下面只表示字段结构；`<InstallRoot>`、`<DataRoot>`、Node 和 PM2 CLI 都必须替换为本机实际选择或探测到的绝对路径。可直接执行[完整操作手册](complete-operations-manual.md)第 15 章的交互式 PowerShell 来生成配置，避免手工转义路径。

```json
{
  "Pm2Bridge": {
    "PipeName": "CompanyOps.Pm2Bridge.v1",
    "OwnerSid": "<真实 PM2 owner SID>",
    "ManifestDirectory": "<DataRoot>\\manifests",
    "SnapshotDirectory": "<DataRoot>\\Agent\\pm2-snapshots",
    "NodeExecutablePath": "<实际 Node.exe 绝对路径>",
    "Pm2CliPath": "<实际 PM2 JavaScript CLI 绝对路径>",
    "SnapshotIntervalSeconds": 10
  }
}
```

Bridge 以 `CompanyOps.Pm2Bridge.<ownerSid>.discovery.json` 写入发现快照。Agent 只枚举这个固定模式，验证协议、文件名、owner SID、Pipe 和 30 秒新鲜度，然后要求项目的全部 PM2 组件在同一 owner 下精确匹配。EnvironmentBinding 的 `legacyPm2` 由 Agent 根据该结果生成。

当前 MVP 的 `Pm2SnapshotDirectory` 仍是共享目录，首次安装器尚未建立 per-owner 子目录和文件级 ACL。普通试点只支持一台主机一个 PM2 owner；多 owner 主机必须先补齐 per-owner 快照目录、ACL 和对应自动化测试，不能仅依靠不同 Pipe 宣称已完成隔离。

## 上线步骤

1. 保持 Agent mutations 关闭，先运行 `配置PM2主机接管.cmd`；
2. 查看发现快照，仅应包含协议、owner、Pipe、采集时间及 name、pmId、cwd、script、status、pid、restartCount；
3. 在 Console 完成只读接入，所有 PM2 组件必须由同一发现快照唯一匹配；
4. Agent `projects` 必须显示 PM2 组件 `Owned`；存在 InstalledState 时 pm_id 还必须一致；
5. 单独授权后，对一个试点 pm_id 做 start/stop/restart，确认其他 pm_id 无变化。

如果 owner、快照年龄、同名数量、cwd、script 或 pm_id 任一不一致，控制必然失败。
