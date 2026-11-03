# ============================================================
#  Build-Exe.ps1 - Compile CNLC.cs into a single-file exe
#  Usage:
#    powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Exe.ps1
#      -> CNLC\CNLC.exe       正式版：零残留（不写日志/状态文件）
#    powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Exe.ps1 -Test
#      -> CNLC\CNLC-Test.exe  测试版（/define:DIAG）：始终写日志与状态文件，作者手持排查用
#  Output: uses the built-in .NET Framework compiler, no third-party
#          packer -> far fewer antivirus false positives
# ============================================================

param([switch]$Test)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'CNLC'
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

# 1) Locate the .NET Framework C# compiler that ships with Windows
$cscCandidates = @(
    (Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    Write-Host 'ERROR: csc.exe not found (need .NET Framework 4.x installed).' -ForegroundColor Red
    exit 1
}
Write-Host "Compiler: $csc" -ForegroundColor Gray

# 2) Stop a running instance so the output file is not locked
$procName = if ($Test) { 'CNLC-Test' } else { 'CNLC' }
$exeName  = if ($Test) { 'CNLC-Test.exe' } else { 'CNLC.exe' }
Get-Process -Name $procName -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping running $procName process..." -ForegroundColor Yellow
    $_.Kill()
    $_.WaitForExit(3000)
}

# 3) Compile
$exePath = Join-Path $dist $exeName
if (Test-Path $exePath) { Remove-Item $exePath -Force }

$defines = if ($Test) { '/define:DIAG' } else { '' }

& $csc /nologo /target:winexe /platform:anycpu $defines /out:"$exePath" `
    /win32icon:"$root\app.ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "$root\CNLC.cs"

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exePath)) {
    Write-Host 'Build FAILED.' -ForegroundColor Red
    exit 1
}

$sizeKB = [Math]::Round((Get-Item $exePath).Length / 1KB)
$kind = if ($Test) { 'TEST (log + status file ON)' } else { 'RELEASE (zero residue)' }
Write-Host "Build OK: $exePath ($sizeKB KB) [$kind]" -ForegroundColor Green
