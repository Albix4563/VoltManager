param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
    $versionPrefix = [string]$props.Project.PropertyGroup.VoltManagerVersionPrefix
    if ([string]::IsNullOrWhiteSpace($versionPrefix)) {
        throw 'VoltManagerVersionPrefix not found in Directory.Build.props.'
    }
    $Version = "$versionPrefix.0"
}
$referenceDir = Join-Path ([IO.Path]::GetTempPath()) ("VoltManager-PublishTest-" + [Guid]::NewGuid().ToString('N'))

try {
    dotnet publish (Join-Path $root 'src\VoltManager\VoltManager.csproj') `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=true `
        -p:Version=$Version `
        -p:AssemblyVersion="$Version.0" `
        -p:FileVersion="$Version.0" `
        -o $referenceDir
    if ($LASTEXITCODE -ne 0) { throw 'Reference application publish failed.' }

    $expectedRuntime = Join-Path $referenceDir 'WindowsBase.dll'
    $expectedHash = (Get-FileHash $expectedRuntime -Algorithm SHA256).Hash

    & (Join-Path $root 'build.ps1') -SkipInstaller -Version $Version
    if ($LASTEXITCODE -ne 0) { throw 'Application package build failed.' }

    $publishDir = Join-Path $root 'publish'
    $actualRuntime = Join-Path $publishDir 'WindowsBase.dll'
    $actualHash = (Get-FileHash $actualRuntime -Algorithm SHA256).Hash

    if ($actualHash -ne $expectedHash) {
        throw "Application runtime was overwritten while adding the supervisor. Expected WindowsBase.dll hash $expectedHash, got $actualHash."
    }

    $supervisor = Join-Path $publishDir 'VoltManager.Supervisor.exe'
    if (-not (Test-Path $supervisor -PathType Leaf)) {
        throw "Required supervisor artifact not found: $supervisor"
    }

    $hardwareService = Join-Path $publishDir 'VoltManager.HardwareService.exe'
    if (-not (Test-Path $hardwareService -PathType Leaf)) {
        throw "Required hardware service artifact not found: $hardwareService"
    }
    foreach ($frameworkDependentArtifact in @(
        'VoltManager.HardwareService.dll',
        'VoltManager.HardwareService.deps.json',
        'VoltManager.HardwareService.runtimeconfig.json'
    )) {
        $unexpected = Join-Path $publishDir $frameworkDependentArtifact
        if (Test-Path $unexpected -PathType Leaf) {
            throw "Framework-dependent hardware service artifact should not remain in final payload: $unexpected"
        }
    }

    Write-Host 'PASS: application runtime preserved and supervisor + isolated hardware service included.' -ForegroundColor Green
}
finally {
    if (Test-Path $referenceDir) {
        Remove-Item $referenceDir -Recurse -Force
    }
}
