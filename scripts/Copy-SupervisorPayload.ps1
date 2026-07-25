param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$executablePath = Join-Path $Source 'VoltManager.Supervisor.exe'
if (-not (Test-Path $executablePath -PathType Leaf)) {
    throw "Required supervisor artifact not found: $executablePath"
}

Copy-Item $executablePath $Destination -Force

$pdbPath = Join-Path $Source 'VoltManager.Supervisor.pdb'
if (Test-Path $pdbPath -PathType Leaf) {
    Copy-Item $pdbPath $Destination -Force
}
