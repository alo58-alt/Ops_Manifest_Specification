# PM2 Owner Bridge

## 为什么必须有 Bridge

Windows 上的 PM2 daemon 属于启动它的用户会话。高权限 Agent 直接运行 `pm2 jlist` 可能连接或拉起另一个 daemon，因此不能把 Agent 用户、`PM2_HOME` 技巧或共享 PM2 GUI 当作隔离边界。

`CompanyOps.Pm2Bridge` 必须由真实 PM2 owner 用户启动。它只有两项能力：

1. 执行固定 Node 可执行文件和固定 PM2 JavaScript CLI 的 `jlist`，删去环境变量等敏感字段，原子写入缩减快照；
2. 接收受 ACL 保护的结构化请求，每次重新 `jlist`，精确验证 name、数字 pm_id、规范化 cwd 和 script，随后只执行 `start|stop|restart <pm_id>`。

Bridge 没有 `all`、`delete`、`kill` 或任意命令字段，代码也不读取、设置或改写 `PM2_HOME`。

## 配置

在 PM2 owner 账号下准备独立 `appsettings.json`。下面只表示字段结构；`<InstallRoot>`、`<DataRoot>`、Node 和 PM2 CLI 都必须替换为本机实际选择或探测到的绝对路径。可直接执行[完整操作手册](complete-operations-manual.md)第 15 章的交互式 PowerShell 来生成配置，避免手工转义路径。

```json
{
  "Pm2Bridge": {
    "PipeName": "CompanyOps.Pm2Bridge.v1",
    "ManifestDirectory": "<DataRoot>\\manifests",
    "SnapshotDirectory": "<DataRoot>\\Agent\\pm2-snapshots",
    "NodeExecutablePath": "<实际 Node.exe 绝对路径>",
    "Pm2CliPath": "<实际 PM2 JavaScript CLI 绝对路径>",
    "SnapshotIntervalSeconds": 10
  }
}
```

EnvironmentBinding 的 `legacyPm2.ownerSid` 必须等于该账号真实 SID；`snapshotFileName` 只能是单一 JSON 文件名；`controlPipeName` 必须与该 owner Bridge 的 `PipeName` 相同。设计上每个 owner 使用不同 Pipe、配置和快照文件。

当前 MVP 的 `Pm2SnapshotDirectory` 仍是共享目录，首次安装器尚未建立 per-owner 子目录和文件级 ACL。普通试点只支持一台主机一个 PM2 owner；多 owner 主机必须先补齐 per-owner 快照目录、ACL 和对应自动化测试，不能仅依靠不同 Pipe 宣称已完成隔离。

## 上线步骤

1. 在 PM2 owner 会话执行 `whoami /user`，记录 SID；
2. 确认 Node 和 PM2 CLI 绝对路径属于该 owner 的实际安装；
3. 保持 Agent mutations 关闭，前台启动 Bridge；
4. 查看缩减快照，仅应包含 name、pmId、cwd、script、status、pid、restartCount；
5. Agent `projects` 必须显示 PM2 组件 `Owned`，pm_id 必须与 InstalledState 一致；
6. 将 Bridge 注册为该 owner 的“登录时启动”任务，而不是 LocalSystem 服务；
7. 单独授权后，对一个试点 pm_id 做 start/stop/restart，确认其他 pm_id 无变化。

如果 owner、快照年龄、同名数量、cwd、script 或 pm_id 任一不一致，控制必然失败。
