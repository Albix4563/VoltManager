# VoltManager build script (Windows PowerShell 5.1 compatible)
# Steps: test -> publish app + supervisor -> WebView2 bootstrapper -> build WPF setup
param(
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [string]$Version = "1.1.1"
)

$ErrorActionPreference = 'Stop'
$root                 = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir           = Join-Path $root 'publish'
$supervisorPublishDir = Join-Path $root 'publish-supervisor'
$distDir              = Join-Path $root 'dist'
$setupDir             = Join-Path $root 'src\VoltManager.Setup'
$payloadDir           = Join-Path $setupDir 'Payload'

Write-Host '=== VoltManager build ===' -ForegroundColor Cyan

# 1. Unit tests
if (-not $SkipTests) {
    Write-Host '[1/4] dotnet test' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'VoltManager.sln') -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
} else {
    Write-Host '[1/4] tests skipped' -ForegroundColor Yellow
}

# 2. Publish main app (self-contained single folder)
Write-Host '[2/4] dotnet publish app (self-contained win-x64)' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish (Join-Path $root 'src\VoltManager\VoltManager.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -p:AssemblyVersion="$Version.0" `
    -p:FileVersion="$Version.0" `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }

# 2a. Publish the external supervisor into the same self-contained folder.
Write-Host '[2a]  dotnet publish supervisor' -ForegroundColor Cyan
if (Test-Path $supervisorPublishDir) { Remove-Item -Recurse -Force $supervisorPublishDir }
dotnet publish (Join-Path $root 'src\VoltManager.Supervisor\VoltManager.Supervisor.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -p:AssemblyVersion="$Version.0" `
    -p:FileVersion="$Version.0" `
    -o $supervisorPublishDir
if ($LASTEXITCODE -ne 0) { throw 'Supervisor publish failed.' }
Copy-Item (Join-Path $supervisorPublishDir '*') $publishDir -Recurse -Force
Remove-Item -Recurse -Force $supervisorPublishDir
Write-Host ("Published app + supervisor to: " + $publishDir) -ForegroundColor Green

# 2b. Jump-list helper (net48, asInvoker) copied next to the main exe
Write-Host '[2b]  dotnet build VoltManager.PlanSwitch' -ForegroundColor Cyan
$planSwitchOut = Join-Path $root 'publish-planswitch'
if (Test-Path $planSwitchOut) { Remove-Item -Recurse -Force $planSwitchOut }
dotnet build (Join-Path $root 'src\VoltManager.PlanSwitch\VoltManager.PlanSwitch.csproj') `
    -c Release `
    -p:Version=$Version `
    -p:AssemblyVersion="$Version.0" `
    -p:FileVersion="$Version.0" `
    -o $planSwitchOut
if ($LASTEXITCODE -ne 0) { throw 'PlanSwitch build failed.' }
Copy-Item (Join-Path $planSwitchOut 'VoltManagerPlanSwitch.exe') $publishDir -Force
$planSwitchConfig = Join-Path $planSwitchOut 'VoltManagerPlanSwitch.exe.config'
if (Test-Path $planSwitchConfig) { Copy-Item $planSwitchConfig $publishDir -Force }
Remove-Item -Recurse -Force $planSwitchOut

if ($SkipInstaller) {
    Write-Host '[3/4][4/4] installer skipped' -ForegroundColor Yellow
    exit 0
}

# 3. WebView2 Evergreen bootstrapper
Write-Host '[3/4] WebView2 bootstrapper' -ForegroundColor Cyan
New-Item -ItemType Directory -Force $payloadDir | Out-Null
$wv2 = Join-Path $payloadDir 'MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path $wv2)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $wv2
}
Write-Host ("Bootstrapper: " + $wv2) -ForegroundColor Green

# Refresh setup project icon from source of truth (Assets is committed; this keeps it in sync)
$iconSrc = Join-Path $root 'src\VoltManager\Assets\voltmanager.ico'
$iconDst = Join-Path $setupDir 'Assets\voltmanager.ico'
if (Test-Path $iconSrc) { Copy-Item $iconSrc $iconDst -Force }

# 3b. Zip publish folder into payload.zip
Write-Host '    Zipping payload…' -ForegroundColor Cyan
$zipPath = Join-Path $payloadDir 'payload.zip'
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Add-Type -Assembly 'System.IO.Compression.FileSystem'
[IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath,
    [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host ("Payload zip: " + $zipPath) -ForegroundColor Green

# 4. Build WPF setup project
Write-Host '[4/4] dotnet build VoltManager.Setup' -ForegroundColor Cyan
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory $distDir | Out-Null }
dotnet build (Join-Path $setupDir 'VoltManager.Setup.csproj') `
    -c Release `
    -p:Platform=x64 `
    -p:Version=$Version `
    -p:AssemblyVersion="$Version.0" `
    -p:FileVersion="$Version.0" `
    -o (Join-Path $distDir 'setup-build')
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }

# Copy final exe to dist/ with versioned name
$builtExe = Join-Path $distDir 'setup-build\VoltManagerSetup.exe'
$finalExe = Join-Path $distDir "VoltManagerSetup-$Version.exe"
if (Test-Path $builtExe) {
    Copy-Item $builtExe $finalExe -Force
    Write-Host ("Installer: " + $finalExe) -ForegroundColor Green
} else {
    throw "Setup executable not found at $builtExe"
}

Write-Host '=== Build OK ===' -ForegroundColor Green
Get-ChildItem $distDir -Filter '*.exe'
