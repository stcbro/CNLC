# ============================================================
#  Build-Exe.ps1 - Compile CNLC.cs into a single-file exe
#  Usage:
#    powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Exe.ps1
#  Output: dist\CNLC.exe  (uses the built-in .NET Framework compiler,
#          no third-party packer -> far fewer antivirus false positives)
# ============================================================

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
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
Get-Process -Name 'CNLC' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host 'Stopping running CNLC process...' -ForegroundColor Yellow
    $_.Kill()
    $_.WaitForExit(3000)
}

# 3) Compile
$exePath = Join-Path $dist 'CNLC.exe'
if (Test-Path $exePath) { Remove-Item $exePath -Force }

& $csc /nologo /target:winexe /platform:anycpu /out:"$exePath" `
    /win32icon:"$root\app.ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "$root\CNLC.cs"

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exePath)) {
    Write-Host 'Build FAILED.' -ForegroundColor Red
    exit 1
}

$sizeKB = [Math]::Round((Get-Item $exePath).Length / 1KB)
Write-Host "Build OK: $exePath ($sizeKB KB)" -ForegroundColor Green
