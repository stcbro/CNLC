# ============================================================
#  Build-Exe.ps1 - 把 Config.ps1 + AutoLogin.ps1 合并并打包成单文件 exe
#  用法（在本目录打开 PowerShell 执行）：
#    powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Exe.ps1
#  产物：dist\校园网登录器.exe（单文件，可拷贝到任意电脑直接运行）
#  依赖：ps2exe 模块（脚本会自动尝试安装，仅需 CurrentUser 权限）
# ============================================================

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

# 1) 确认 ps2exe 模块可用
if (-not (Get-Module -ListAvailable ps2exe)) {
    Write-Host 'ps2exe 模块未安装，正在安装（CurrentUser 范围）...' -ForegroundColor Yellow
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
            Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope CurrentUser | Out-Null
        }
        Set-PSRepository PSGallery -InstallationPolicy Trusted
        Install-Module ps2exe -Scope CurrentUser -Force
    } catch {
        Write-Host "安装 ps2exe 失败：$($_.Exception.Message)" -ForegroundColor Red
        Write-Host '请检查网络后重试，或手动执行：Install-Module ps2exe -Scope CurrentUser' -ForegroundColor Yellow
        exit 1
    }
}
Import-Module ps2exe

# 2) 合并脚本：Config.ps1 内容内联进 AutoLogin.ps1，统一用 $AppRoot 代替 $PSScriptRoot
$cfg  = Get-Content (Join-Path $root 'Config.ps1')   -Raw
$auto = Get-Content (Join-Path $root 'AutoLogin.ps1') -Raw

# 去掉 AutoLogin 中对 Config.ps1 的 dot-source 引用（单引号字符串避免转义歧义）
$auto = $auto -replace '(?m)^\s*\. \(Join-Path \$PSScriptRoot .Config\.ps1.\)\s*\r?\n', ''

# 打包后 $PSScriptRoot 可能为空（exe 场景），统一替换为 $AppRoot
$cfg  = $cfg  -replace '\$PSScriptRoot', '$AppRoot'
$auto = $auto -replace '\$PSScriptRoot', '$AppRoot'

$merged = @"
# ===== 本文件由 Build-Exe.ps1 自动生成（Config.ps1 + AutoLogin.ps1 合并），请勿直接修改 =====
# 应用目录：exe 场景取 exe 所在目录；脚本场景取脚本目录（logs/状态/记住密码文件都放这里）
`$AppRoot = if (`$PSScriptRoot) { `$PSScriptRoot } else { Split-Path -Parent ([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName) }

# ps2exe -noConsole 模式没有控制台，Write-Host 会逐条变成 MessageBox 弹窗 → 全部静默（日志仍写文件）
`$script:SilentConsole = `$true

$cfg
$auto
"@

$mergedPath = Join-Path $dist 'AutoLogin_merged.ps1'
[System.IO.File]::WriteAllText($mergedPath, $merged, (New-Object System.Text.UTF8Encoding $true))   # UTF-8 BOM

# 3) 编译 exe
$exePath = Join-Path $dist '校园网登录器.exe'
try {
    Invoke-ps2exe -inputFile $mergedPath -outputFile $exePath `
        -noConsole -title '校园网自动登录器' `
        -description '校园内网登录器（Campus Network Login Client, CNLC）——校园网 portal 断网自动重连工具' `
        -product 'Campus Network Login Client (CNLC)' -company '' -version '1.2.0.0' `
        -iconFile (Join-Path $root 'app.ico') | Out-Null
    if (Test-Path $exePath) {
        $sizeKB = [Math]::Round((Get-Item $exePath).Length / 1KB)
        Write-Host "打包成功：$exePath（${sizeKB} KB）" -ForegroundColor Green
        Write-Host '说明：单文件即可携带；运行后生成的 logs/状态/credentials.txt 会放在 exe 旁边。' -ForegroundColor Gray
        Write-Host '提示：ps2exe 生成的 exe 偶尔被杀毒软件误报，属正常现象，添加信任即可。' -ForegroundColor Yellow
    } else {
        Write-Host '打包失败：未找到输出文件。' -ForegroundColor Red
        exit 1
    }
} catch {
    Write-Host "打包失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
