# VoltManager synthetic smoke test — run ELEVATED.
# Cycle: silent install -> launch -> process/window check -> plan switch check -> uninstall -> restore.
# Results appended to scripts\smoke_test_results.txt
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$log = Join-Path $root 'scripts\smoke_test_results.txt'
$results = New-Object System.Collections.ArrayList

function Step($name, $ok, $detail) {
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    [void]$results.Add(("[{0}] {1} - {2}" -f $mark, $name, $detail))
}

"=== VoltManager smoke test $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" | Set-Content -Encoding utf8 $log

# 0. Preconditions
$setup = Get-ChildItem (Join-Path $root 'dist') -Filter 'VoltManagerSetup-*.exe' | Select-Object -First 1
Step 'Installer exists' ($null -ne $setup) ($setup.FullName)
$originalScheme = (powercfg /getactivescheme) -join ' '
$originalGuid = if ($originalScheme -match '([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})') { $Matches[1] } else { $null }
Step 'Original scheme captured' ($null -ne $originalGuid) $originalGuid

# Kill any running instance
Get-Process VoltManager -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# 1. Silent install
$proc = Start-Process $setup.FullName -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru
Step 'Silent install exit 0' ($proc.ExitCode -eq 0) ("exit=" + $proc.ExitCode)
$installDir = Join-Path ${env:ProgramFiles} 'VoltManager'
$exe = Join-Path $installDir 'VoltManager.exe'
Step 'Installed exe present' (Test-Path $exe) $exe
Step 'wwwroot deployed' (Test-Path (Join-Path $installDir 'wwwroot\index.html')) 'index.html'

# 2. Launch and check process alive
if (Test-Path $exe) {
    Start-Process $exe
    Start-Sleep -Seconds 15
    $p = Get-Process VoltManager -ErrorAction SilentlyContinue
    Step 'Process running' ($null -ne $p) ("pid=" + ($p.Id -join ','))
    Step 'Main window present' ($p -and $p.MainWindowHandle -ne 0) ("title=" + $p.MainWindowTitle)
    # WebView2 child process check (renderer running = UI alive)
    $wv = Get-Process msedgewebview2 -ErrorAction SilentlyContinue
    Step 'WebView2 renderer running' ($null -ne $wv) ("count=" + (@($wv).Count))

    # 3. Plan switch check: app must have valid plan mapping; switch via powercfg as the app would
    $settingsPath = Join-Path $env:APPDATA 'VoltManager\settings.json'
    Step 'Settings file created' (Test-Path $settingsPath) $settingsPath
    $list = (powercfg /list) -join "`n"
    $saver = if ($list -match '(a1841308-3541-4fab-bc81-f71556f20b4a)') { $Matches[1] } else {
        if (Test-Path $settingsPath) { (Get-Content -Raw $settingsPath | ConvertFrom-Json).planGuidMap.PowerSaver } else { $null }
    }
    if ($saver) {
        powercfg /setactive $saver | Out-Null
        Start-Sleep -Seconds 1
        $active = (powercfg /getactivescheme) -join ' '
        Step 'Plan switch to saver works' ($active -match [regex]::Escape($saver)) $saver
    } else {
        Step 'Plan switch to saver works' $false 'no saver guid available'
    }

    # 4. Kill app before uninstall
    Get-Process VoltManager -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# 5. Silent uninstall
$unins = Join-Path $installDir 'uninstall.exe'
if (Test-Path $unins) {
    $proc = Start-Process $unins -ArgumentList '/uninstall','/SILENT' -Wait -PassThru
    Step 'Silent uninstall exit 0' ($proc.ExitCode -eq 0) ("exit=" + $proc.ExitCode)
    Start-Sleep -Seconds 2
    Step 'Install dir removed' (-not (Test-Path $installDir)) $installDir
    $appData = Join-Path $env:APPDATA 'VoltManager'
    Step 'AppData removed' (-not (Test-Path $appData)) $appData
    $arp = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VoltManager' -ErrorAction SilentlyContinue
    Step 'ARP entry removed' ($null -eq $arp) 'Uninstall\VoltManager'
} else {
    Step 'Silent uninstall exit 0' $false 'uninstall.exe missing'
}

# 6. Restore original plan
if ($originalGuid) {
    powercfg /setactive $originalGuid | Out-Null
    $active = (powercfg /getactivescheme) -join ' '
    Step 'Original scheme restored' ($active -match [regex]::Escape($originalGuid)) $originalGuid
}

$results | Add-Content -Encoding utf8 $log
$fails = @($results | Where-Object { $_ -like '[FAIL]*' }).Count
("=== {0} checks, {1} failures ===" -f $results.Count, $fails) | Add-Content -Encoding utf8 $log
Write-Output ("Smoke test done: {0} failures. Log: {1}" -f $fails, $log)
