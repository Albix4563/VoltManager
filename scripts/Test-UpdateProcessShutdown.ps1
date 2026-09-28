$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$setupProject = Join-Path $root 'src\VoltManager.Setup\VoltManager.Setup.csproj'
$setupOutput = Join-Path $root 'src\VoltManager.Setup\bin\Release\net48\VoltManagerSetup.exe'
$directory = Join-Path ([IO.Path]::GetTempPath()) ("VoltManager-UpdateProcessTest-" + [Guid]::NewGuid().ToString('N'))
$otherDirectory = Join-Path ([IO.Path]::GetTempPath()) ("VoltManager-OtherInstallTest-" + [Guid]::NewGuid().ToString('N'))
$supervisorPath = Join-Path $directory 'VoltManager.Supervisor.exe'
$hardwareServicePath = Join-Path $directory 'VoltManager.HardwareService.exe'
$otherSupervisorPath = Join-Path $otherDirectory 'VoltManager.Supervisor.exe'
$otherHardwareServicePath = Join-Path $otherDirectory 'VoltManager.HardwareService.exe'
$processes = @()

try {
    dotnet build $setupProject -c Release -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }

    New-Item -ItemType Directory -Path $directory | Out-Null
    New-Item -ItemType Directory -Path $otherDirectory | Out-Null
    $probeExecutable = Join-Path $env:WINDIR 'System32\ping.exe'
    if (-not (Test-Path $probeExecutable -PathType Leaf)) {
        throw "Process probe executable not found: $probeExecutable"
    }

    foreach ($path in @($supervisorPath, $hardwareServicePath, $otherSupervisorPath, $otherHardwareServicePath)) {
        Copy-Item $probeExecutable $path
    }

    foreach ($path in @($supervisorPath, $hardwareServicePath, $otherSupervisorPath, $otherHardwareServicePath)) {
        $processes += Start-Process $path -ArgumentList @('127.0.0.1', '-t') -WindowStyle Hidden -PassThru
    }
    Start-Sleep -Milliseconds 350
    foreach ($process in $processes) {
        $process.Refresh()
        if ($process.HasExited) { throw "Process probe exited before updater shutdown test: $($process.StartInfo.FileName)" }
    }

    $targetSupervisor = $processes[0]
    $targetHardware = $processes[1]
    $otherSupervisor = $processes[2]
    $otherHardware = $processes[3]
    if ($targetSupervisor.ProcessName -ne 'VoltManager.Supervisor' -or
        $targetHardware.ProcessName -ne 'VoltManager.HardwareService' -or
        $otherSupervisor.ProcessName -ne 'VoltManager.Supervisor' -or
        $otherHardware.ProcessName -ne 'VoltManager.HardwareService') {
        throw 'One or more updater process probes have unexpected process names.'
    }

    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($setupOutput))
    $type = $assembly.GetType('VoltManager.Setup.Engine.InstallEngine', $true)
    $flags = [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic
    $method = $type.GetMethod('StopRunningInstalledProcesses', $flags)
    if ($null -eq $method) {
        throw 'InstallEngine.StopRunningInstalledProcesses is missing.'
    }

    $arguments = New-Object 'object[]' 1
    $arguments[0] = [string]$directory
    $stopped = [bool]$method.Invoke($null, $arguments)
    foreach ($process in $processes) { $process.Refresh() }

    if (-not $stopped -or -not $targetSupervisor.HasExited -or -not $targetHardware.HasExited) {
        throw "Update did not stop target helper processes. stopped=$stopped, supervisorExited=$($targetSupervisor.HasExited), hardwareExited=$($targetHardware.HasExited)"
    }
    if ($otherSupervisor.HasExited -or $otherHardware.HasExited) {
        throw 'Updater stopped a helper process from a different installation directory.'
    }

    Write-Host 'PASS: updater stops only supervisor + hardware service from the target payload.' -ForegroundColor Green
}
finally {
    foreach ($runningProcess in $processes) {
        if ($null -ne $runningProcess) {
            try {
                $runningProcess.Refresh()
                if (-not $runningProcess.HasExited) { Stop-Process -Id $runningProcess.Id -Force }
            }
            catch { }
            $runningProcess.Dispose()
        }
    }
    foreach ($path in @($directory, $otherDirectory)) {
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force
        }
    }
}
