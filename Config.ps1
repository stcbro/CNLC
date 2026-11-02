# ============================================================
#  Config.ps1 - 校园网自动认证登录 配置文件
#  只需修改本文件，无需改动其他脚本
# ============================================================

# ---------- 认证信息 ----------
# 程序不内置账号密码。每次启动 AutoLogin.ps1 时会提示手动输入，
# 密码在提交登录时由程序自动按校园网 portal 要求做 RC4 加密。
# $PortalUser / $PortalPass 由 AutoLogin.ps1 在启动时赋值，无需在此填写。

# ---------- 认证服务器地址 ----------
# portal 认证入口（注意：真正用于上网认证的是 ac_portal，不是 homepage）
$PortalHost      = 'http://1.1.1.3'
# ac_portal 登录页（断网后 captive portal 重定向到此页，用于取会话 cookie）
$PortalLoginPage = 'http://1.1.1.3/ac_portal/20240318141412/pc.html'
# ac_portal 登录提交接口（与登录页同目录的 login.php）
$PortalLoginApi  = 'http://1.1.1.3/ac_portal/login.php'
# 探测"是否断网"的目标（国内站点，响应快）
$ProbeUrl   = 'https://www.baidu.com'

# ---------- 检测与登录策略 ----------
# 每隔多少秒检测一次断网（秒）。默认 30 = 每 30 秒一次。
# 重要：每次检测都会产生网络流量，起到"保活"作用，避免因空闲超时断网。
# 如果 30 秒仍断网，说明空闲阈值更短，可尝试改为 15 或 10。
$CheckIntervalSec = 30
# 发现断网后，最多重试登录多少次（每次间隔 5 秒）
$MaxLoginAttempts = 10

# ---------- 日志 ----------
# 日志目录（脚本/exe 所在目录下的 logs 文件夹，启动时自动创建）
$LogDir = Join-Path $PSScriptRoot 'logs'
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }

# ---------- 运行状态文件 ----------
$StateFile = Join-Path $PSScriptRoot 'auto_login_status.txt'

# ---------- RC4 加密（与校园网 portal 前端算法一致，勿改） ----------
function Invoke-RC4 {
    param([string]$InputText, [string]$Key)
    $bytes   = [System.Text.Encoding]::UTF8.GetBytes($InputText)
    $keyByte = [System.Text.Encoding]::UTF8.GetBytes($Key)

    # 构造初始 S 盒（用泛型 List，避免数组初始化问题）
    $s = New-Object 'System.Collections.Generic.List[byte]'
    for ($i = 0; $i -lt 256; $i++) { $s.Add([byte]$i) }

    $j = 0
    for ($i = 0; $i -lt 256; $i++) {
        $j = (($j + [int]$s[$i] + [int]$keyByte[$i % $keyByte.Length]) % 256)
        $tmp = $s[$i]; $s[$i] = $s[$j]; $s[$j] = $tmp
    }
    # 加密输出为小写十六进制字符串（与 portal 前端一致）
    $i = 0; $j = 0
    $hex = New-Object System.Text.StringBuilder
    foreach ($b in $bytes) {
        $i = (($i + 1) % 256)
        $j = (($j + [int]$s[$i]) % 256)
        $tmp = $s[$i]; $s[$i] = $s[$j]; $s[$j] = $tmp
        $k   = $s[(($s[$i] + $s[$j]) % 256)]
        [void]$hex.Append($((($b -bxor $k).ToString('x2'))))
    }
    return $hex.ToString()
}

# ---------- 核心功能 ----------

# 检测当前是否断网：返回 $true 表示"已断网"
function Test-Offline {
    # 强制直连：长运行进程下仅清空环境变量不生效（DefaultWebProxy 已缓存代理），
    # 必须把 DefaultWebProxy 重置为空代理对象，才能真正绕过本地 VPN/代理。
    $hp = $env:HTTP_PROXY; $sp = $env:HTTPS_PROXY
    try {
        $env:HTTP_PROXY = ''; $env:HTTPS_PROXY = ''
        [System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy
        $ProgressPreference = 'SilentlyContinue'
        # 用 GET 并检查返回内容，可识别"captive portal 假在线"（返回的是登录页而非百度）
        $resp = Invoke-WebRequest -Uri $ProbeUrl -UseBasicParsing -TimeoutSec 6 -Method Get -ErrorAction Stop
        if ($resp.StatusCode -eq 200 -and $resp.Content -match 'baidu') {
            return $false   # 真·联网
        }
        return $true        # 拿到的是 portal 页面等非百度内容 = 假在线/断网
    } catch {
        return $true        # 不通 = 断网
    } finally {
        $env:HTTP_PROXY = $hp; $env:HTTPS_PROXY = $sp
    }
}

# 执行 portal 认证登录：返回 [bool] 表示成功/失败
function Invoke-PortalLogin {
    # rckey 与 portal 前端一致：当前毫秒时间戳
    $rckey    = [string]([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())
    $encPass  = Invoke-RC4 -InputText $PortalPass -Key $rckey
    # 用 Uri.EscapeDataString 编码，无需加载 System.Web 程序集（PS 5.1 原生支持）
    $user     = [System.Uri]::EscapeDataString($PortalUser)

    # 字段名与 ac_portal 前端提交格式一致：opr=pwdLogin / userName / pwd(RC4) / auth_tag
    $body = "opr=pwdLogin&userName=$user&pwd=$encPass&auth_tag=$rckey&rememberPwd=0"

    # 强制直连：清空代理环境变量 + 重置 DefaultWebProxy（关键，绕过本地 VPN/代理）
    $hp = $env:HTTP_PROXY; $sp = $env:HTTPS_PROXY
    try {
        $env:HTTP_PROXY = ''; $env:HTTPS_PROXY = ''
        [System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy
        $ProgressPreference = 'SilentlyContinue'
        # 先访问 ac_portal 登录页拿到会话 cookie（部分 portal 需要），再带着 cookie 提交登录
        $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        try {
            Invoke-WebRequest -Uri $PortalLoginPage -Method Get `
                    -UseBasicParsing -TimeoutSec 8 -WebSession $session `
                    -Headers @{ 'X-Requested-With' = 'XMLHttpRequest' } -ErrorAction SilentlyContinue | Out-Null
        } catch { }
        $resp = Invoke-WebRequest -Uri $PortalLoginApi `
                -Method Post -Body $body -UseBasicParsing -TimeoutSec 15 `
                -ContentType 'application/x-www-form-urlencoded' `
                -Headers @{ 'X-Requested-With' = 'XMLHttpRequest'; 'Referer' = $PortalLoginPage } `
                -WebSession $session -ErrorAction Stop
        $text = $resp.Content
        # 无论成败都记录原始响应，便于诊断（成功时不必赘述）
        if ($text -notmatch '"success"\s*:\s*true') {
            Write-Log "登录返回(HTTP $($resp.StatusCode)): $text" -Color DarkRed
        }
    } catch {
        $text = ''
        Write-Log "登录请求异常: $($_.Exception.Message)" -Color DarkRed
    } finally {
        $env:HTTP_PROXY = $hp; $env:HTTPS_PROXY = $sp
    }
    # 认证成功特征：响应含 success 且为 true
    if ($text -match '"success"\s*:\s*true') { return $true }
    return $false
}

# 执行 portal 注销（与前端 onLogout 一致：POST opr=logout 到 login.php，服务器按 IP 注销当前会话）
function Invoke-PortalLogout {
    $body = "opr=logout"
    # 强制直连（同登录逻辑，绕过本地 VPN/代理）
    $hp = $env:HTTP_PROXY; $sp = $env:HTTPS_PROXY
    $text = ''
    try {
        $env:HTTP_PROXY = ''; $env:HTTPS_PROXY = ''
        [System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy
        $ProgressPreference = 'SilentlyContinue'
        $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        try {
            Invoke-WebRequest -Uri $PortalLoginPage -Method Get `
                    -UseBasicParsing -TimeoutSec 8 -WebSession $session `
                    -Headers @{ 'X-Requested-With' = 'XMLHttpRequest' } -ErrorAction SilentlyContinue | Out-Null
        } catch { }
        $resp = Invoke-WebRequest -Uri $PortalLoginApi `
                -Method Post -Body $body -UseBasicParsing -TimeoutSec 15 `
                -ContentType 'application/x-www-form-urlencoded' `
                -Headers @{ 'X-Requested-With' = 'XMLHttpRequest'; 'Referer' = $PortalLoginPage } `
                -WebSession $session -ErrorAction Stop
        $text = $resp.Content
        Write-Log "注销返回: $text" -Color DarkGray
    } catch {
        Write-Log "注销请求异常: $($_.Exception.Message)" -Color DarkRed
    } finally {
        $env:HTTP_PROXY = $hp; $env:HTTPS_PROXY = $sp
    }
    if ($text -match '"success"\s*:\s*true') { return $true }
    return $false
}

# 单次"检测 + 必要时登录"流程
function Invoke-CheckAndRelogin {
    param([string]$Time)
    if (Test-Offline) {
        Write-Log "检测到断网，开始重新认证登录 ..." -Color Yellow
        # 注销后的冷却期（portal 服务器需几分钟清掉旧会话）→ 加密重试密度
        $max = $MaxLoginAttempts
        $coolHint = ''
        if ($script:ExtendedRetry) { $max = 40; $coolHint = '（注销冷却期，服务器放行需几分钟，请耐心等待）' }
        for ($att = 1; $att -le $max; $att++) {
            $ok = Invoke-PortalLogin
            if ($ok) {
                Start-Sleep -Seconds 2
                if (-not (Test-Offline)) {
                    Write-Log "登录成功，网络已恢复 !" -Color Green
                    $script:ExtendedRetry = $false
                    Set-Online -Time $Time -LoginSuccess $true
                    return
                }
            }
            Write-Log "第 $att 次认证失败，5 秒后重试$coolHint ..." -Color Red
            Start-Sleep -Seconds 5
        }
        Write-Log "已连续 $max 次认证失败，将在下一个检测周期重试$coolHint" -Color Red
    } else {
        Write-Log "网络正常，无需操作 ..." -Color Cyan
        Set-Online -Time $Time
    }
}

# 记录当前状态到状态文件（供监控脚本读取）
function Set-Online {
    param([string]$Time, [bool]$LoginSuccess = $false)
    $prev = if (Test-Path $StateFile) {
        try { (Get-Content $StateFile -Raw | ConvertFrom-Json).last_login_at } catch { '' }
    } else { '' }
    $info = @{
        last_check     = $Time
        online_status  = 'ONLINE'
        last_login_at  = if ($LoginSuccess) { $Time } else { $prev }
    }
    $info | ConvertTo-Json | Set-Content $StateFile -Encoding UTF8
}

# 日志输出（同时写入文件 + 控制台）
function Write-Log {
    param([string]$Message, [string]$Color = 'Gray')
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message"
    # 打包成 exe（ps2exe -noConsole）时 Write-Host 会逐条变成弹窗，注入的 $script:SilentConsole=$true 会跳过
    if (-not $script:SilentConsole) {
        try { Write-Host $line -ForegroundColor $Color } catch { }
    }
    # 文件日志尽力而为：目录只读等异常不影响主流程
    try {
        $dayFile = Join-Path $LogDir "auto_login_$(Get-Date -Format 'yyyyMMdd').log"
        Add-Content -Path $dayFile -Value $line -Encoding UTF8
    } catch { }
}
