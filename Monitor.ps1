# ============================================================
#  Monitor.ps1 - 自动登录程序 状态监控（只读，随时可运行）
#  用法：powershell -File Monitor.ps1
# ============================================================

$scriptDir = $PSScriptRoot

$pidFile   = Join-Path $scriptDir 'auto_login.pid'
$stateFile = Join-Path $scriptDir 'auto_login_status.txt'
$logDir    = Join-Path $scriptDir 'logs'
$configFile = Join-Path $scriptDir 'Config.ps1'

# 判断守护进程是否存活
$running = $false
$pidVal = ''
if (Test-Path $pidFile) {
    $pidVal = (Get-Content $pidFile -Raw).Trim()
    $proc = Get-Process -Id $pidVal -ErrorAction SilentlyContinue
    if ($proc) { $running = $true }
}

# 读取状态文件
$status = '未知'
$lastCheck = '-'
if (Test-Path $stateFile) {
    $st = Get-Content $stateFile -Raw | ConvertFrom-Json
    $status   = $st.online_status
    $lastCheck = $st.last_check
}

# 当前实时联网检测
$nowOnline = '?'
try {
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri 'https://www.baidu.com' -UseBasicParsing -TimeoutSec 5 -Method Head | Out-Null
    $nowOnline = '在线'
} catch {
    $nowOnline = '断网'
}

# 读取今日日志最后 5 行
$lastLogs = @()
$dayLog = Join-Path $logDir "auto_login_$(Get-Date -Format 'yyyyMMdd').log"
if (Test-Path $dayLog) { $lastLogs = Get-Content $dayLog -Tail 5 }

Write-Host ''
Write-Host '==============================================' -ForegroundColor Cyan
Write-Host '   校园网自动认证程序 - 运行状态' -ForegroundColor Cyan
Write-Host '==============================================' -ForegroundColor Cyan
if ($running) {
    Write-Host ("守护进程:     运行中 (PID: " + $pidVal + ")")
} else {
    Write-Host '守护进程:     未运行'
}
Write-Host ("实时网络:     {0}" -f $nowOnline)
Write-Host ("最近检测:     {0}" -f $lastCheck)
Write-Host ("状态记录:     {0}" -f $status)
Write-Host ''
Write-Host '--- 最近日志 ---' -ForegroundColor Gray
if ($lastLogs) { $lastLogs | Write-Host } else { Write-Host '(暂无日志)' }
Write-Host '==============================================' -ForegroundColor Cyan
Write-Host ''
