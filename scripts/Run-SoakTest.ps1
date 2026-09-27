param(
    [Parameter(Mandatory = $true)] [string] $App,
    [string] $Supervisor,
    [string] $Harness = (Join-Path $PSScriptRoot '..\tests\VoltManager.WindowsHarness\bin\Release\net8.0-windows\win-x64\VoltManager.WindowsHarness.exe'),
    [string] $Output = (Join-Path $PSScriptRoot '..\artifacts\resource-validation\soak'),
    [ValidateRange(0.01, 10080)] [double] $DurationMinutes = 240,
    [ValidateRange(1, 3600)] [double] $SampleSeconds = 15,
    [ValidateRange(0, 1440)] [double] $WarmupMinutes = 2
)

$ErrorActionPreference = 'Stop'

function Resolve-RequiredFile([string] $Path, [string] $Name) {
    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "$Name must be a file: $Path"
    }
    return $resolved.Path
}

$App = Resolve-RequiredFile $App 'App'
$Harness = Resolve-RequiredFile $Harness 'Harness'
if (-not [string]::IsNullOrWhiteSpace($Supervisor)) {
    $Supervisor = Resolve-RequiredFile $Supervisor 'Supervisor'
}

$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $Output | Out-Null

$arguments = @(
    '--mode', 'soak',
    '--app', $App,
    '--output', $Output,
    '--duration-minutes', $DurationMinutes.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--sample-seconds', $SampleSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--warmup-minutes', $WarmupMinutes.ToString([Globalization.CultureInfo]::InvariantCulture)
)
if (-not [string]::IsNullOrWhiteSpace($Supervisor)) {
    $arguments += @('--supervisor', $Supervisor)
}

& $Harness @arguments
$exitCode = $LASTEXITCODE

$summary = Join-Path $Output 'windows-harness.md'
if (Test-Path -LiteralPath $summary -PathType Leaf) {
    Write-Host ''
    Get-Content -LiteralPath $summary
}

exit $exitCode
