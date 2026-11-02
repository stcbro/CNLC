# Diagnostic: simulate exactly what AutoLogin.ps1 does on startup
Write-Output '=== STEP 1: dot-source Config.ps1 ==='
try {
    . 'D:\WorkBuddy\2026-10-30-23-15-08\campus-auto-login\Config.ps1'
    Write-Output 'OK: Config loaded'
} catch {
    Write-Output ('FAIL: ' + $_.Exception.Message)
    exit 1
}

Write-Output ('PortalUser = ' + $(if ($PortalUser) { $PortalUser } else { '(启动时手动输入)' }))
Write-Output ('CheckIntervalSec = ' + $CheckIntervalSec)
Write-Output ('LogDir = ' + $LogDir)
Write-Output ('StateFile = ' + $StateFile)

Write-Output ''
Write-Output '=== STEP 2: Test-Offline ==='
try {
    $offline = Test-Offline
    Write-Output ('Offline = ' + $offline)
} catch {
    Write-Output ('FAIL Test-Offline: ' + $_.Exception.Message)
}

Write-Output ''
Write-Output '=== STEP 3: Invoke-CheckAndRelogin (one cycle) ==='
try {
    Invoke-CheckAndRelogin -Time (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    Write-Output 'OK: one cycle completed without error'
} catch {
    Write-Output ('FAIL Invoke-CheckAndRelogin: ' + $_.Exception.Message)
    Write-Output ('Line: ' + $_.InvocationInfo.PositionMessage)
}

Write-Output ''
Write-Output '=== STEP 4: Verify log + state files ==='
$logFile = Join-Path $LogDir ('auto_login_' + (Get-Date -Format 'yyyyMMdd') + '.log')
Write-Output ('Log exists: ' + (Test-Path $logFile))
if (Test-Path $logFile) {
    Write-Output '--- last 3 log lines ---'
    Get-Content $logFile -Tail 3
}
Write-Output ('State exists: ' + (Test-Path $StateFile))
if (Test-Path $StateFile) {
    Write-Output ('State content: ' + (Get-Content $StateFile -Raw).Trim())
}
Write-Output ''
Write-Output 'DIAG COMPLETE'
