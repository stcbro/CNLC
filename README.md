# 校园内网登录器（Campus Network Login Client, CNLC）

> 检测到断网后，自动执行 portal 认证登录，保持网络持续在线。
> 针对沈阳城市学院校园网（1.1.1.3 portal 认证）定制。

## 快速开始

### 第一步：在登录窗口输入账号密码

双击 **`launch_login.vbs`**（推荐）或 `启动网络连接.bat`，会弹出图形登录窗口：

```
┌─ 校园网自动登录器 ──────────┐
│  账号（学号/工号）: [________]  │
│  密码:              [********]  │
│  ┌────────────────────────┐ │
│  │         登 录           │ │
│  └────────────────────────┘ │
│  □ 记住密码（下次自动登录）     │
│  □ 开机自启动                 │
└─────────────────────────────┘
```

- **记住密码**：账号密码以 Base64 编码保存在 `credentials.txt`（**跨电脑通用**：把程序文件夹连同此文件拷到别的电脑，照样自动登录；注意这只是防止直接看到密码，并非加密）。勾选后下次启动不再弹窗、自动登录直接进托盘守护；取消勾选并登录即删除。
- **注销**：点击后停止守护并向 portal 发送注销请求（与登录页"注销"一致），**网络随之断开**；随后回到登录窗口，可重新登录或换账号。
- **开机自启动**：写入当前用户注册表 Run 项（无需管理员权限），勾选/取消即时生效。
- 密码提交登录时由程序自动按校园网要求做 RC4 加密，不会明文传输。
- 换账号：托盘右键"切换账号"，或取消"记住密码"重新登录。

> **无黑窗口说明**：请用 `launch_login.vbs` 启动，全程无任何黑色控制台窗口。`启动网络连接.bat` 只是转发到 VBS，会有短暂的 cmd 一闪而过；直接运行 AutoLogin.ps1 则能看到控制台一秒后被隐藏。

### 第二步：启动守护程序

**方法一：直接运行（前台，能看到实时日志）**

右键 `AutoLogin.ps1` → "使用 PowerShell 运行"

**方法二：后台运行（不推荐了）**

因为启动时需要手动输入账号密码，必须用**前台窗口**运行才能看到输入提示。如果用 `-WindowStyle Hidden` 隐藏运行，程序会因无法输入而一直等待。

### 第三步：程序缩到托盘运行

输入账号密码后，窗口会自动隐藏，程序常驻**系统托盘**（屏幕右下角盾牌图标）：

- **左键点击图标**：弹出运行状态窗口（当前账号 / 实时网络状态 / 检测间隔 / 最近检测与成功登录时间）
- **右键图标 → 立即检测一次**：手动触发一次断网检测
- **右键图标 → 切换账号**：暂停守护，重新弹出登录窗口换账号登录
- **右键图标 → 退出**：结束程序
- 断网/恢复时会有气泡通知；日志仍写入 `logs/` 目录

### 随时查看状态

双击运行 `Monitor.ps1`，会显示：
- 守护进程是否存活
- 实时网络状态（在线/断网）
- 最近检测时间
- 最近 5 条日志

## 工作原理

```
┌─────────────────────────────────────┐
│  守护循环（每 60 秒检测一次）          │
│                                      │
│  ┌──────────┐   断网   ┌──────────┐ │
│  │ 检测网络  │─────────→│ RC4 加密  │ │
│  │ (百度)   │          │ 密码提交  │ │
│  └──────────┘          └────┬─────┘ │
│       ↑                      │      │
│       │ 网络正常            │ 认证 │
│       └──────────────────────┘      │
│           登录成功？               │
│              ↓ 是                   │
│           网络恢复                  │
└─────────────────────────────────────┘
```

- **检测**：每 30 秒访问 `https://www.baidu.com`，不通则判定断网
- **认证**：断网后，用 RC4 加密密码（密钥=时间戳），POST 到 `http://1.1.1.3/ac_portal/login.php`
- **重试**：登录失败自动重试，最多 10 次，间隔 5 秒
- **日志**：每天写入 `logs/auto_login_日期.log`，同时输出到控制台

## 文件说明

| 文件 | 作用 |
|---|---|
| `Config.ps1` | 配置文件（服务器地址、检测间隔等；不含账号密码） |
| `AutoLogin.ps1` | 主程序（守护循环） |
| `Monitor.ps1` | 状态监控（随时查看运行情况） |
| `test_rc4.ps1` | RC4 算法测试 |
| `test_login.ps1` | 登录接口测试 |
| `logs/` | 日志目录（自动创建） |
| `auto_login_status.txt` | 状态记录文件 |
| `auto_login.pid` | 进程 PID（运行时自动生成） |

## 常用命令

```powershell
# 启动（后台）
powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "D:\WorkBuddy\2026-10-30-23-15-08\campus-auto-login\AutoLogin.ps1"

# 查看状态
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\WorkBuddy\2026-10-30-23-15-08\campus-auto-login\Monitor.ps1"

# 停止（按 PID 结束进程）
Stop-Process -Id (Get-Content "D:\WorkBuddy\2026-10-30-23-15-08\campus-auto-login\auto_login.pid")
```

## 调整参数（Config.ps1 中修改）

| 参数 | 默认值 | 说明 |
|---|---|---|
| `$CheckIntervalSec` | 60 | 检测间隔（秒），调小更快响应断网 |
| `$MaxLoginAttempts` | 10 | 断网后最多重试次数 |
| `$ProbeUrl` | https://www.baidu.com | 断网检测目标 |

## 打包成单文件 exe（可选）

运行 `Build-Exe.ps1`，会把 Config.ps1 + AutoLogin.ps1 合并并编译成**单文件 exe**（需联网自动安装 ps2exe 模块，仅 CurrentUser 权限）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Exe.ps1
```

产物：`dist\校园网登录器.exe`（约 58 KB）。**拷贝这个 exe 到任何电脑即可使用**，双击运行，运行时生成的 `logs/`、`auto_login_status.txt`、`credentials.txt` 都会放在 exe 旁边，随 exe 一起携带。

> 注意：ps2exe 生成的 exe 偶尔会被杀毒软件误报，添加信任即可；换电脑后首次运行建议先联网弹出登录窗口输入账号。

## 常见问题

**Q: 每次开机都要手动启动吗？**
A: 是的。如需开机自启，可创建计划任务（见下方）。

**Q: 账号密码会泄露吗？**
A: 程序不保存账号密码。账号密码只在启动时手动输入、仅存在于内存中，密码提交前按校园网要求 RC4 加密，只发送给校园网认证服务器（1.1.1.3）。所以把整个程序文件夹拷给别人使用，不会带上你的账号密码。

**Q: 断网后多久能自动恢复？**
A: 取决于 `$CheckIntervalSec`。默认 60 秒检测一次，断网后立即认证，认证过程约 1-2 秒，所以最快约 60 秒内恢复。

**Q: 认证失败怎么办？**
A: 查看 `logs/` 下的日志文件，确认账号密码是否正确。程序会自动重试，直到成功。

## 开机自启（可选）

创建计划任务，开机自动运行：

```powershell
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"D:\WorkBuddy\2026-10-30-23-15-08\campus-auto-login\AutoLogin.ps1`""
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskName "CampusAutoLogin" -Action $action -Trigger $trigger -Settings $settings -Force
```
