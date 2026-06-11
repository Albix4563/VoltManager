# VoltManager build script (Windows PowerShell 5.1 compatible)
# Steps: test -> publish (self-contained win-x64) -> download WebView2 bootstrapper -> Inno Setup installer
param(
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $root 'publish'
$distDir = Join-Path $root 'dist'
$installerDir = Join-Path $root 'installer'

Write-Host '=== VoltManager build ===' -ForegroundColor Cyan

# 1. Unit tests
if (-not $SkipTests) {
    Write-Host '[1/4] dotnet test' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'VoltManager.sln') -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
} else {
    Write-Host '[1/4] tests skipped' -ForegroundColor Yellow
}

# 2. Publish (self-contained single folder = portable deliverable)
Write-Host '[2/4] dotnet publish (self-contained win-x64)' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish (Join-Path $root 'src\VoltManager\VoltManager.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Host ("Portable folder: " + $publishDir) -ForegroundColor Green

if ($SkipInstaller) {
    Write-Host '[3/4][4/4] installer skipped' -ForegroundColor Yellow
    exit 0
}

# 3. WebView2 Evergreen bootstrapper (required: target machines may lack the runtime, e.g. LTSC)
Write-Host '[3/4] WebView2 bootstrapper' -ForegroundColor Cyan
$wv2 = Join-Path $installerDir 'MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path $wv2)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $wv2
}
Write-Host ("Bootstrapper: " + $wv2) -ForegroundColor Green

# 4. Inno Setup
Write-Host '[4/4] Inno Setup' -ForegroundColor Cyan
$iscc = $null
foreach ($candidate in @(
    "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)) {
    if (Test-Path $candidate) { $iscc = $candidate; break }
}
if ($null -eq $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if ($null -eq $iscc) {
    throw 'ISCC.exe (Inno Setup 6) not found. Install from https://jrsoftware.org/isdl.php'
}

if (-not (Test-Path $distDir)) { New-Item -ItemType Directory $distDir | Out-Null }
& $iscc (Join-Path $installerDir 'VoltManager.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

Write-Host '=== Build OK ===' -ForegroundColor Green
Get-ChildItem $distDir
