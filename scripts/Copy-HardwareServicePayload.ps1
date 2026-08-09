param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$executablePath = Join-Path $Source 'VoltManager.HardwareService.exe'
if (-not (Test-Path $executablePath -PathType Leaf)) {
    throw "Required hardware service artifact not found: $executablePath"
}

# The hardware service is published self-contained/single-file. Remove the
# framework-dependent project-reference payload that dotnet publish may have
# copied beside the main app, then install the isolated executable.
foreach ($name in @(
    'VoltManager.HardwareService.exe',
    'VoltManager.HardwareService.dll',
    'VoltManager.HardwareService.deps.json',
    'VoltManager.HardwareService.runtimeconfig.json',
    'VoltManager.HardwareService.pdb'
)) {
    $existing = Join-Path $Destination $name
    if (Test-Path $existing -PathType Leaf) { Remove-Item $existing -Force }
}

Copy-Item $executablePath $Destination -Force

$pdbPath = Join-Path $Source 'VoltManager.HardwareService.pdb'
if (Test-Path $pdbPath -PathType Leaf) {
    Copy-Item $pdbPath $Destination -Force
}
