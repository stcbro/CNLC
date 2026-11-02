# ============================================================
#  AutoLogin.ps1 - 校园网自动认证登录器（GUI 登录框版）
#  功能：弹出登录窗口输入校园网账号密码，支持【记住密码】【开机自启动】【开机自登录】；
#        登录后缩到系统托盘，定时检测断网，断网后自动重新认证
#  用法：双击"启动网络连接.bat"，或右键"使用 PowerShell 运行"
#  说明：记住密码 = 账号密码编码保存（跨电脑可用）；开机自启动 = 写入注册表 Run 项；
#        开机自登录 = 通过开机自启动拉起时静默登录直接进托盘（手动双击仍弹窗口）；
#        提交前密码自动 RC4 加密（与 1.1.1.3 登录页算法一致）
# ============================================================

# 开机自启动标记：不使用 param()（ps2exe 合并脚本中 param 不在首行会报"无法识别"错），
# 直接检查本进程命令行是否带 -AutoStart（注册表 Run 启动时由 Run 项传入）
$AutoStart = $false
try { if ([System.Environment]::CommandLine -match '-AutoStart') { $AutoStart = $true } } catch { }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# 加载配置与核心函数
. (Join-Path $PSScriptRoot 'Config.ps1')

# 防止多开：基于锁文件记录 PID（不依赖 WMI，避免部分运行环境下 WMI 不可用导致程序崩溃）
$lockFile = Join-Path $PSScriptRoot 'auto_login.lock'
$alreadyRunning = $false
try {
    if (Test-Path $lockFile) {
        $oldPid = (Get-Content $lockFile -Raw -ErrorAction SilentlyContinue).Trim()
        if ($oldPid -and $oldPid -ne "$PID") {
            $proc = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
            if ($proc) { $alreadyRunning = $true }
        }
    }
} catch { }
if ($alreadyRunning) {
    [void][System.Windows.Forms.MessageBox]::Show(
        "校园网自动登录器已在运行（右下角托盘盾牌图标）。`n`n如需切换账号，请先右键托盘图标退出，再重新启动。",
        '提示')
    exit 0
}
# 写入本实例 PID 作为锁（进程被强杀时会残留死 PID，下次启动检测到进程已不存在会自动覆盖）
$PID | Set-Content $lockFile -Encoding UTF8
$PID | Set-Content (Join-Path $PSScriptRoot 'auto_login.pid') -Encoding UTF8

# 隐藏黑色控制台窗口（SW_HIDE = 0），只显示图形界面
Add-Type -Namespace Win32 -Name Native -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
'@
[Win32.Native]::ShowWindow([Win32.Native]::GetConsoleWindow(), 0) | Out-Null

# ---------- 记住密码（Base64 编码存储，跨电脑通用；注意：仅混淆防误看，非加密） ----------
$credFile = Join-Path $PSScriptRoot 'credentials.txt'

$script:SavedUser = ''
$script:SavedPass = ''
$script:SavedAutoLogin = $false
if (Test-Path $credFile) {
    try {
        $saved = Get-Content $credFile -Raw | ConvertFrom-Json
        $script:SavedUser = $saved.user
        $script:SavedPass = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($saved.pass))
        $script:SavedAutoLogin = [bool]$saved.autoLogin
    } catch {
        Remove-Item $credFile -Force -ErrorAction SilentlyContinue   # 文件损坏 → 删除重来
    }
}
# 迁移清理：删除旧版 DPAPI 绑定本机的凭据文件（换电脑无法解密）
Remove-Item (Join-Path $PSScriptRoot 'credentials.bin') -Force -ErrorAction SilentlyContinue

# 注销冷却期标记：注销后置 $true，登录成功后自动复位（详见 Config.ps1 Invoke-CheckAndRelogin）
$script:ExtendedRetry = $false
# 守护开关状态：$true = 定时器运行中（断网自动重连）
$script:GuardOn = $false

# ---------- 开机自启动（HKCU 注册表 Run 项，无需管理员） ----------
$regPath  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runName  = 'CampusAutoLogin'
$psExe    = Join-Path $PSHOME 'powershell.exe'
$runValue = ('"{0}" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{1}" -AutoStart' -f $psExe, (Join-Path $PSScriptRoot 'AutoLogin.ps1'))

function Test-AutoStartEnabled {
    return $null -ne (Get-ItemProperty -Path $regPath -Name $runName -ErrorAction SilentlyContinue).$runName
}
function Set-AutoStart([bool]$Enable) {
    if ($Enable) {
        Set-ItemProperty -Path $regPath -Name $runName -Value $runValue
    } else {
        Remove-ItemProperty -Path $regPath -Name $runName -ErrorAction SilentlyContinue
    }
}

# ---------- GUI 登录窗口（原版朴素风格） ----------
$form = New-Object System.Windows.Forms.Form
$form.Text            = '校园网自动登录器'
$form.ClientSize      = New-Object System.Drawing.Size(320, 215)
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedSingle
$form.StartPosition   = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.MaximizeBox     = $false
$form.TopMost         = $true

function New-Label([string]$text, [int]$x, [int]$y) {
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $text
    $l.Location = New-Object System.Drawing.Point($x, $y)
    $l.AutoSize = $true
    return $l
}

$lblUser = New-Label '工号:' 25 15
$txtUser = New-Object System.Windows.Forms.TextBox
$txtUser.Location = New-Object System.Drawing.Point(25, 35)
$txtUser.Size     = New-Object System.Drawing.Size(270, 23)

$lblPass = New-Label '密码:' 25 64
$txtPass = New-Object System.Windows.Forms.TextBox
$txtPass.Location     = New-Object System.Drawing.Point(25, 84)
$txtPass.Size         = New-Object System.Drawing.Size(270, 23)
$txtPass.PasswordChar = '*'

$btnLogin = New-Object System.Windows.Forms.Button
$btnLogin.Text     = '登 录'
$btnLogin.Location = New-Object System.Drawing.Point(25, 116)
$btnLogin.Size     = New-Object System.Drawing.Size(130, 30)

$btnLogout = New-Object System.Windows.Forms.Button
$btnLogout.Text     = '注 销'
$btnLogout.Location = New-Object System.Drawing.Point(165, 116)
$btnLogout.Size     = New-Object System.Drawing.Size(130, 30)

$chkRemember = New-Object System.Windows.Forms.CheckBox
$chkRemember.Text     = '记住密码'
$chkRemember.Location = New-Object System.Drawing.Point(25, 156)
$chkRemember.AutoSize = $true

$chkAutoStart = New-Object System.Windows.Forms.CheckBox
$chkAutoStart.Text     = '开机自启动'
$chkAutoStart.Location = New-Object System.Drawing.Point(25, 182)
$chkAutoStart.AutoSize = $true

# 断网自动守护开关（与"登录"分离：登录只认证上网，守护负责断网自动重连）
$chkGuard = New-Object System.Windows.Forms.CheckBox
$chkGuard.Text     = '断网自动重连'
$chkGuard.Location = New-Object System.Drawing.Point(165, 156)
$chkGuard.AutoSize = $true
$chkGuard.Checked  = $true

$chkAutoStart.Location = New-Object System.Drawing.Point(25, 182)

# 开机自登录：开机自启动拉起时静默登录直接进托盘（需先记住密码）
$chkAutoLogin = New-Object System.Windows.Forms.CheckBox
$chkAutoLogin.Text     = '开机自登录'
$chkAutoLogin.Location = New-Object System.Drawing.Point(165, 182)
$chkAutoLogin.AutoSize = $true

$form.Controls.AddRange(@($lblUser, $txtUser, $lblPass, $txtPass, $btnLogin, $btnLogout, $chkRemember, $chkGuard, $chkAutoStart, $chkAutoLogin))
$form.AcceptButton = $btnLogin   # 在输入框按回车 = 点登录

# 若已记住密码：自动填入并勾选
if ($script:SavedUser) {
    $txtUser.Text        = $script:SavedUser
    $txtPass.Text        = $script:SavedPass
    $chkRemember.Checked = $true
}
# 若已设置开机自登录：勾选
if ($script:SavedAutoLogin) { $chkAutoLogin.Checked = $true }
# 若已设置开机自启动：勾选
if (Test-AutoStartEnabled) { $chkAutoStart.Checked = $true }

# 开机自启动勾选框：即时写入/删除注册表 Run 项
$script:SuppressAutoStartEvent = $false
$chkAutoStart.Add_CheckedChanged({
    if ($script:SuppressAutoStartEvent) { return }
    try {
        Set-AutoStart $chkAutoStart.Checked
        Write-Log $(if ($chkAutoStart.Checked) { '已开启开机自启动' } else { '已取消开机自启动' }) -Color Green
    } catch {
        [void][System.Windows.Forms.MessageBox]::Show("设置开机自启动失败：$($_.Exception.Message)", '错误')
        $script:SuppressAutoStartEvent = $true
        $chkAutoStart.Checked = -not $chkAutoStart.Checked   # 回滚勾选状态
        $script:SuppressAutoStartEvent = $false
    }
})

# 断网自动守护开关：勾选=开守护（立即认证并缩到托盘）；取消=暂停守护
$script:SuppressGuardEvent = $false
$chkGuard.Add_CheckedChanged({
    if ($script:SuppressGuardEvent) { return }
    if ($chkGuard.Checked) {
        if ($script:PortalUser) {
            $form.Hide()
            Set-GuardState $true
        } else {
            $script:SuppressGuardEvent = $true
            $chkGuard.Checked = $false
            $script:SuppressGuardEvent = $false
            [void][System.Windows.Forms.MessageBox]::Show('请先登录，再开启守护。', '提示')
        }
    } else {
        Set-GuardState $false
    }
})

# 开机自登录开关：依赖"记住密码"保存的账号；勾选状态写入 credentials.txt
$script:SuppressAutoLoginEvent = $false
$chkAutoLogin.Add_CheckedChanged({
    if ($script:SuppressAutoLoginEvent) { return }
    if ($chkAutoLogin.Checked -and -not (Test-Path $credFile)) {
        $script:SuppressAutoLoginEvent = $true
        $chkAutoLogin.Checked = $false
        $script:SuppressAutoLoginEvent = $false
        [void][System.Windows.Forms.MessageBox]::Show('开机自登录需要先勾选"记住密码"保存账号。', '提示')
        return
    }
    if (Test-Path $credFile) {
        try {
            $c = Get-Content $credFile -Raw | ConvertFrom-Json
            @{ user = $c.user; pass = $c.pass; autoLogin = $chkAutoLogin.Checked } |
                ConvertTo-Json | Set-Content $credFile -Encoding UTF8
            Write-Log $(if ($chkAutoLogin.Checked) { '已开启开机自登录' } else { '已关闭开机自登录' }) -Color Green
        } catch { }
    }
})

# 自动消失的轻提示浮层（2.5 秒后自动关闭，无需点击确认）
function Show-Toast([string]$Message, [bool]$IsError = $false) {
    $tf = New-Object System.Windows.Forms.Form
    $tf.FormBorderStyle  = [System.Windows.Forms.FormBorderStyle]::None
    $tf.StartPosition    = [System.Windows.Forms.FormStartPosition]::CenterScreen
    $tf.Size             = New-Object System.Drawing.Size(300, 52)
    $tf.TopMost          = $true
    $tf.ShowInTaskbar    = $false
    $tf.BackColor        = if ($IsError) { [System.Drawing.Color]::FromArgb(198, 40, 40) } else { [System.Drawing.Color]::FromArgb(46, 125, 50) }
    $lb = New-Object System.Windows.Forms.Label
    $lb.Text      = $Message
    $lb.Dock      = 'Fill'
    $lb.ForeColor = [System.Drawing.Color]::White
    $lb.TextAlign = 'MiddleCenter'
    $lb.AutoSize  = $false
    $tf.Controls.Add($lb)
    $script:ToastForm  = $tf
    $script:ToastTimer = New-Object System.Windows.Forms.Timer
    $script:ToastTimer.Interval = 2500
    $script:ToastTimer.Add_Tick({ $script:ToastForm.Close() })
    $script:ToastTimer.Start()
    [void]$tf.ShowDialog()
    $script:ToastTimer.Stop()
    $script:ToastTimer.Dispose()
}

# ---------- 托盘守护 ----------
function Invoke-TrayCheck {
    $wasOffline = Test-Offline
    if ($wasOffline) {
        $script:TrayIcon.ShowBalloonTip(4000, '校园网自动登录',
            '检测到断网，正在自动重新认证 ...', [System.Windows.Forms.ToolTipIcon]::Warning)
    }
    Invoke-CheckAndRelogin -Time (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    if ($wasOffline -and -not (Test-Offline)) {
        $script:TrayIcon.ShowBalloonTip(4000, '校园网自动登录',
            '登录成功，网络已恢复！', [System.Windows.Forms.ToolTipIcon]::Info)
    }
}

# 左键点击托盘图标 → 弹出状态窗口（账号 / 实时网络状态 / 最近检测与登录时间）
function Show-StatusWindow {
    if (-not $script:TrayIcon) {
        [void][System.Windows.Forms.MessageBox]::Show('尚未登录，守护未启动。', '状态')
        return
    }
    # 单实例：状态窗已打开时不重复弹（MouseUp 可能连续触发）
    if ($script:StatusForm -and -not $script:StatusForm.IsDisposed) {
        $script:StatusForm.Activate()
        return
    }
    $offline  = Test-Offline
    $st       = $null
    try { if (Test-Path $StateFile) { $st = Get-Content $StateFile -Raw | ConvertFrom-Json } } catch { }

    $sf                = New-Object System.Windows.Forms.Form
    $sf.Text           = '运行状态'
    $sf.ClientSize     = New-Object System.Drawing.Size(300, 185)
    $sf.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $sf.StartPosition  = [System.Windows.Forms.FormStartPosition]::CenterScreen
    $sf.MaximizeBox    = $false
    $sf.TopMost        = $true

    $lines = @(
        "当前账号：$script:PortalUser",
        "网络状态：$(if ($offline) { '已断网' } else { '在线' })",
        "检测间隔：${CheckIntervalSec} 秒",
        "最近检测：$($st.last_check)",
        "最近登录：$($st.last_login_at)"
    )
    $y = 15
    foreach ($t in $lines) {
        $lb = New-Label $t 20 $y
        $sf.Controls.Add($lb)
        $y += 24
    }
    $ok        = New-Object System.Windows.Forms.Button
    $ok.Text   = '确定'
    $ok.Location = New-Object System.Drawing.Point(110, 150)
    $ok.Size   = New-Object System.Drawing.Size(80, 26)
    $ok.Add_Click({ $sf.Close() })
    $sf.Controls.Add($ok)
    $sf.AcceptButton = $ok
    $script:StatusForm = $sf
    [void]$sf.ShowDialog()
}

function Start-TrayGuard {
    # 托盘图标/定时器只在首次创建
    if (-not $script:Timer) {

    Write-Log "校园网自动认证程序已启动（托盘守护）" -Color Green
    Write-Log "账号: $script:PortalUser   检测间隔: ${CheckIntervalSec}s" -Color Cyan
    Write-Log "目标服务器: $PortalHost   登录接口: $PortalLoginApi" -Color Cyan

    $script:TrayIcon = New-Object System.Windows.Forms.NotifyIcon
    $script:TrayIcon.Icon    = [System.Drawing.SystemIcons]::Shield
    $script:TrayIcon.Text    = "校园网自动登录 - $script:PortalUser（每 ${CheckIntervalSec}s 检测）"
    $script:TrayIcon.Visible = $true

    $menu     = New-Object System.Windows.Forms.ContextMenu
    $miGuard  = New-Object System.Windows.Forms.MenuItem '断网自动重连'
    $miGuard.Checked = $true
    $miSwitch = New-Object System.Windows.Forms.MenuItem '切换账号'
    $miLogout = New-Object System.Windows.Forms.MenuItem '注销'
    $miExit   = New-Object System.Windows.Forms.MenuItem '退出'
    [void]$menu.MenuItems.AddRange(@($miGuard, $miSwitch, $miLogout, (New-Object System.Windows.Forms.MenuItem '-'), $miExit))
    $script:TrayIcon.ContextMenu = $menu
    $script:MiGuard = $miGuard

    # 左键点击托盘图标 → 显示运行状态窗口（必须过滤右键：右键会同时触发 Click 事件）
    $script:TrayIcon.Add_MouseUp({
        param($sender, $e)
        if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) { Show-StatusWindow }
    })
    # 切换账号：暂停守护，重新弹出登录窗口
    $miSwitch.Add_Click({
        $script:Timer.Stop()
        $script:GuardOn = $false
        if ($script:MiGuard) { $script:MiGuard.Checked = $false }
        $script:TrayIcon.Text = '校园网自动登录 - 待重新登录'
        $txtUser.Text = $script:PortalUser
        $txtPass.Text = $script:PortalPass
        $chkRemember.Checked = (Test-Path $credFile)
        $form.Show()
    })
    $miExit.Add_Click({
        Write-Log "用户从托盘菜单选择退出" -Color Yellow
        [System.Windows.Forms.Application]::Exit()
    })
    $miLogout.Add_Click({ Invoke-TrayLogout })
    # 守护开关：点一下开，再点一下关
    $miGuard.Add_Click({ Set-GuardState (-not $script:GuardOn) })

    $script:Timer = New-Object System.Windows.Forms.Timer
    $script:Timer.Interval = $CheckIntervalSec * 1000
    $script:Timer.Add_Tick({ Invoke-TrayCheck })

    Write-Log "已最小化到系统托盘（右下角盾牌图标）。" -Color Cyan
    $script:TrayIcon.ShowBalloonTip(4000, '校园网自动登录',
        "守护已开始（$script:PortalUser）：每 ${CheckIntervalSec}s 检测一次，断网自动重新认证",
        [System.Windows.Forms.ToolTipIcon]::Info)
    }

    # 点登录后：跳过联网检测等待，直接立即发认证请求，一步到位上网
    $now = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Write-Log "正在立即登录认证（跳过检测）..." -Color Cyan
    $null = Invoke-PortalLogin
    Start-Sleep -Seconds 1
    if (Test-Offline) {
        # 立即认证未恢复（如撞上服务器拒绝）→ 转入重试流程
        Write-Log "立即认证未恢复，转入自动重试 ..." -Color Yellow
        Invoke-CheckAndRelogin -Time $now
    } else {
        Write-Log "登录成功，网络已恢复 !" -Color Green
        $script:ExtendedRetry = $false
        Set-Online -Time $now -LoginSuccess $true
        if ($script:TrayIcon) {
            $script:TrayIcon.ShowBalloonTip(4000, '校园网自动登录',
                '登录成功，网络已恢复！', [System.Windows.Forms.ToolTipIcon]::Info)
        }
    }
    $script:GuardOn = $true
    if ($script:MiGuard) { $script:MiGuard.Checked = $true }
    $script:Timer.Start()
}

# 守护开关（托盘菜单/勾选框共用）：开=立即认证并进入守护；关=停止自动重连
function Set-GuardState([bool]$On) {
    if ($On) {
        Start-TrayGuard
        $script:GuardOn = $true
        if ($script:MiGuard) { $script:MiGuard.Checked = $true }
    } else {
        if ($script:Timer) { $script:Timer.Stop() }
        $script:GuardOn = $false
        if ($script:MiGuard) { $script:MiGuard.Checked = $false }
        Write-Log '守护已关闭（不再自动重连）' -Color Yellow
        if ($script:TrayIcon) { $script:TrayIcon.Text = '校园网自动登录 - 守护已关闭' }
    }
}

# ---------- 登录按钮 ----------
$btnLogin.Add_Click({
    $u = $txtUser.Text.Trim()
    $p = $txtPass.Text
    if (-not $u -or -not $p) {
        [void][System.Windows.Forms.MessageBox]::Show('请填写账号和密码。', '提示')
        return
    }
    $script:PortalUser = $u
    $script:PortalPass = $p   # 仅存内存；提交时由 Invoke-RC4 自动加密

    # 记住密码：Base64 编码保存（跨电脑通用，含开机自登录开关状态）/ 取消勾选则删除
    if ($chkRemember.Checked) {
        try {
            @{ user = $u; pass = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($p)); autoLogin = $chkAutoLogin.Checked } |
                ConvertTo-Json | Set-Content $credFile -Encoding UTF8
        } catch {
            Write-Log "保存记住密码失败（不影响本次使用）: $($_.Exception.Message)" -Color Yellow
        }
    } else {
        Remove-Item $credFile -Force -ErrorAction SilentlyContinue
        $script:SuppressAutoLoginEvent = $true
        $chkAutoLogin.Checked = $false   # 没有记住密码就无法开机自登录
        $script:SuppressAutoLoginEvent = $false
    }

    if ($script:TrayIcon) {
        $script:TrayIcon.Text = "校园网自动登录 - $u（每 ${CheckIntervalSec}s 检测）"
        Write-Log "账号已切换为 $u" -Color Green
    }
    if ($chkGuard.Checked) {
        # 登录 + 开启守护：隐藏窗口，立即认证并进入守护
        $form.Hide()
        Set-GuardState $true
    } else {
        # 仅登录：只认证上网，不开守护（窗口保持打开，不弹提示框）
        $now = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
        Write-Log "正在登录认证（仅登录，守护未开启）..." -Color Cyan
        $null = Invoke-PortalLogin
        Start-Sleep -Seconds 1
        if (Test-Offline) {
            Write-Log '登录未成功：服务器可能暂时拒绝，请稍后重试或检查账号密码。' -Color Red
            Show-Toast '登录未成功，请稍后重试' $true
        } else {
            $script:ExtendedRetry = $false
            Set-Online -Time $now -LoginSuccess $true
            Write-Log '登录成功，网络已恢复！（守护未开启）' -Color Green
            Show-Toast '登录成功，网络已恢复'
            if ($script:TrayIcon) {
                $script:TrayIcon.ShowBalloonTip(4000, '校园网自动登录',
                    '登录成功，网络已恢复！（守护未开启）', [System.Windows.Forms.ToolTipIcon]::Info)
            }
        }
    }
})

# 注销：停止守护 → portal 注销（断网）→ 回到登录窗口（登录窗口按钮与托盘菜单共用）
function Invoke-TrayLogout {
    if (-not $script:PortalUser) {
        [void][System.Windows.Forms.MessageBox]::Show('尚未登录，无需注销。', '提示')
        return
    }
    $ans = [System.Windows.Forms.MessageBox]::Show(
        "确定注销账号 $($script:PortalUser) 吗？`n注销后网络将断开，守护暂停。", '确认注销',
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question)
    if ($ans -ne [System.Windows.Forms.DialogResult]::Yes) { return }

    if ($script:Timer) { $script:Timer.Stop() }
    $script:GuardOn = $false
    if ($script:MiGuard) { $script:MiGuard.Checked = $false }
    Write-Log "正在注销账号 $script:PortalUser ..." -Color Yellow
    $ok = Invoke-PortalLogout
    if ($ok) {
        # portal 服务器注销后有几分钟冷却期，重登录会被暂时拒绝 → 开启加密重试模式
        $script:ExtendedRetry = $true
        Write-Log '注销成功，网络已断开。' -Color Green
        if ($script:TrayIcon) {
            $script:TrayIcon.ShowBalloonTip(4000, '校园网自动登录',
                "账号 $($script:PortalUser) 已注销，网络已断开", [System.Windows.Forms.ToolTipIcon]::Info)
            $script:TrayIcon.Text = '校园网自动登录 - 已注销'
        }
    } else {
        Write-Log '注销请求未成功（可能已离线或被服务器拒绝）' -Color Red
        if ($script:TrayIcon) {
            $script:TrayIcon.ShowBalloonTip(4000, '校园网自动登录',
                '注销请求未成功（可能已离线）', [System.Windows.Forms.ToolTipIcon]::Warning)
        }
    }
    # 回到登录窗口（保留已填账号，供重新登录）
    $txtUser.Text = $script:PortalUser
    $txtPass.Text = $script:PortalPass
    $form.Show()
}
$btnLogout.Add_Click({ Invoke-TrayLogout })

# 直接点 X 关闭登录窗口（尚未登录）= 退出程序
$form.Add_FormClosed({ [System.Windows.Forms.Application]::Exit() })

try {
    if ($AutoStart -and $script:SavedUser -and $script:SavedPass -and $script:SavedAutoLogin) {
        # 开机自启动拉起 + 已记住密码 + 已开启开机自登录 → 静默登录，直接进托盘守护
        $script:PortalUser = $script:SavedUser
        $script:PortalPass = $script:SavedPass
        Write-Log "开机自启动：静默登录 $script:SavedUser" -Color Green
        $script:SuppressGuardEvent = $true
        $chkGuard.Checked = $true
        $script:SuppressGuardEvent = $false
        Start-TrayGuard
    } else {
        # 手动双击 / 未满足静默条件 → 弹出登录窗口（已记住密码则自动填好）
        $form.Show()
    }
    [System.Windows.Forms.Application]::Run()   # 消息泵，Exit 调用后返回
} finally {
    if ($script:Timer)    { $script:Timer.Stop() }
    if ($script:TrayIcon) {
        try { $script:TrayIcon.Visible = $false; $script:TrayIcon.Dispose() } catch { }
    }
    # 退出时清理 PID / 锁文件
    $pidFile = Join-Path $PSScriptRoot 'auto_login.pid'
    if (Test-Path $pidFile) { Remove-Item $pidFile -Force -ErrorAction SilentlyContinue }
    if (Test-Path $lockFile) { Remove-Item $lockFile -Force -ErrorAction SilentlyContinue }
}
