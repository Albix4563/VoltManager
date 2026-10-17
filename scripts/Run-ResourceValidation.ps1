param(
    [Parameter(Mandatory = $true)] [string] $BaselineApp,
    [Parameter(Mandatory = $true)] [string] $CandidateApp,
    [string] $Supervisor,
    [string] $BaselineSupervisor,
    [string] $CandidateSupervisor,
    [string] $Harness = (Join-Path $PSScriptRoot '..\tests\VoltManager.WindowsHarness\bin\Release\net8.0-windows\win-x64\VoltManager.WindowsHarness.exe'),
    [string] $Output = (Join-Path $PSScriptRoot '..\artifacts\resource-validation\benchmark-final'),
    [ValidateRange(1, 3600)] [int] $StabilizationSeconds = 30,
    [ValidateRange(1, 3600)] [int] $MeasurementSeconds = 120,
    [ValidateRange(1, 100)] [int] $Repetitions = 5,
    [ValidateRange(0, 100)] [int] $UnstableExtraRepetitions = 2,
    [switch] $Resume,
    [switch] $StartupComparison,
    [ValidateRange(10, 1000)] [int] $StartupPairs = 10
)

$ErrorActionPreference = 'Stop'

function Resolve-RequiredPath([string] $Path, [string] $Name) {
    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "$Name must be a file: $Path" }
    return $resolved.Path
}

$BaselineApp = Resolve-RequiredPath $BaselineApp 'BaselineApp'
$CandidateApp = Resolve-RequiredPath $CandidateApp 'CandidateApp'
$Harness = Resolve-RequiredPath $Harness 'Harness'

function Resolve-SupervisorPath([string] $Explicit, [string] $Fallback, [string] $App, [string] $Name) {
    $path = if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        $Explicit
    } elseif (-not [string]::IsNullOrWhiteSpace($Fallback)) {
        $Fallback
    } else {
        Join-Path (Split-Path -Parent $App) 'VoltManager.Supervisor.exe'
    }
    return Resolve-RequiredPath $path $Name
}

$BaselineSupervisor = Resolve-SupervisorPath $BaselineSupervisor $Supervisor $BaselineApp 'BaselineSupervisor'
$CandidateSupervisor = Resolve-SupervisorPath $CandidateSupervisor $Supervisor $CandidateApp 'CandidateSupervisor'
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $Output | Out-Null

$scenarios = @('dashboard-active', 'dashboard-inactive', 'tray-no-widgets', 'tray-widget', 'protected-cpu', 'restore')
$runs = [System.Collections.Generic.List[object]]::new()

function Get-AppProductVersion([string] $App) {
    return [Diagnostics.FileVersionInfo]::GetVersionInfo($App).ProductVersion
}

if ($StartupComparison) {
    if ($Resume) {
        throw 'Startup comparison cannot be resumed: reused-profile cohorts must have a controlled warm-up.'
    }

    # Every invocation gets private, test-owned WebView2 profiles. Never touch a real user-data folder.
    $sessionName = 'session-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'), [guid]::NewGuid().ToString('N').Substring(0, 8)
    $startupOutput = Join-Path (Join-Path $Output 'startup-comparison') $sessionName
    New-Item -ItemType Directory -Force -Path $startupOutput | Out-Null
    $startupRuns = [System.Collections.Generic.List[object]]::new()
    $startupMetrics = @('StartupLatencyMs', 'FirstFrameLatencyMs', 'TrayLatencyMs')

    function Get-StartupMetric($Run, [string] $Name) {
        if ($null -eq $Run -or $null -eq $Run.$Name) { return $null }
        try {
            $value = [double]$Run.$Name
            if ([double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -lt 0) { return $null }
            return $value
        }
        catch { return $null }
    }

    function Invoke-StartupRun([string] $Label, [string] $ProfileMode, [int] $Iteration, [bool] $Warmup = $false) {
        $phase = if ($Warmup) { 'warmup' } else { 'runs' }
        $runDir = Join-Path $startupOutput ('{0}\{1}\{2:D2}\{3}' -f $phase, $ProfileMode, $Iteration, $Label)
        $profileDir = if ($ProfileMode -eq 'reused') {
            Join-Path $startupOutput ('profiles\reused\{0}' -f $Label)
        } else {
            Join-Path $startupOutput ('profiles\fresh\{0}\{1:D2}' -f $Label, $Iteration)
        }
        New-Item -ItemType Directory -Force -Path $runDir, $profileDir | Out-Null
        $app = if ($Label -eq 'baseline') { $BaselineApp } else { $CandidateApp }
        $supervisorPath = if ($Label -eq 'baseline') { $BaselineSupervisor } else { $CandidateSupervisor }
        $jsonPath = Join-Path $runDir 'startup-run.json'
        $csvPath = Join-Path $runDir 'startup-run.csv'
        $exitCode = -1
        $launchFailure = $null
        try {
            & $Harness --mode startup-benchmark --app $app --supervisor $supervisorPath `
                --label $Label --iteration $Iteration --renderer hardware --profile-mode $ProfileMode `
                --profile-root $profileDir --output $runDir
            $exitCode = [int]$LASTEXITCODE
        }
        catch {
            $launchFailure = $_.Exception.Message
        }

        $raw = $null
        if (Test-Path -LiteralPath $jsonPath -PathType Leaf) {
            try { $raw = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json }
            catch { $launchFailure = "Invalid startup-run.json: $($_.Exception.Message)" }
        }
        if ($null -eq $raw -and -not $launchFailure) { $launchFailure = 'Harness did not write startup-run.json.' }

        $startupMs = Get-StartupMetric $raw 'StartupLatencyMs'
        $status = 'passed'
        $reason = $null
        if ($null -ne $raw -and $raw.FailureReason) { $reason = [string]$raw.FailureReason }
        if ($exitCode -ne 0 -or $launchFailure -or $null -eq $raw -or
            $raw.Status -notin @('passed', 'success', 'ok') -or $null -eq $startupMs) {
            $status = 'failed'
            if ($launchFailure) { $reason = $launchFailure }
            elseif (-not $reason -and $exitCode -ne 0) { $reason = "Harness exited with code $exitCode." }
            elseif (-not $reason -and $null -eq $startupMs) { $reason = 'StartupLatencyMs is missing or invalid.' }
            elseif (-not $reason) { $reason = "Harness reported status '$($raw.Status)'." }
        }
        if ($status -eq 'passed' -and $null -ne $raw) {
            if (($raw.Label -and $raw.Label -ne $Label) -or
                ($raw.ProfileMode -and $raw.ProfileMode -ne $ProfileMode) -or
                ($raw.Renderer -and $raw.Renderer -ne 'hardware') -or
                ($null -ne $raw.Iteration -and [int]$raw.Iteration -ne $Iteration)) {
                $status = 'failed'
                $reason = 'Harness output does not match the requested label, iteration, renderer or profile mode.'
            }
        }

        $startupRuns.Add([pscustomobject]@{
            ProfileMode = $ProfileMode
            Label = $Label
            Iteration = $Iteration
            Warmup = $Warmup
            Renderer = 'hardware'
            Status = $status
            FailureReason = $reason
            ExitCode = $exitCode
            StartupLatencyMs = $startupMs
            FirstFrameLatencyMs = Get-StartupMetric $raw 'FirstFrameLatencyMs'
            TrayLatencyMs = Get-StartupMetric $raw 'TrayLatencyMs'
            ProfileRoot = $profileDir
            JsonPath = $jsonPath
            CsvPath = if (Test-Path -LiteralPath $csvPath -PathType Leaf) { $csvPath } else { $null }
            Raw = $raw
        })
        Write-Host ('Startup {0}: {1}/{2}/iteration {3} ({4})' -f $status, $ProfileMode, $Label, $Iteration, $phase)
    }

    function Get-StartupStatistics($Values) {
        $ordered = @($Values | Sort-Object)
        if ($ordered.Count -eq 0) { return $null }
        $count = $ordered.Count
        $median = if (($count % 2) -eq 0) {
            ([double]$ordered[($count / 2) - 1] + [double]$ordered[$count / 2]) / 2
        } else {
            [double]$ordered[[int][Math]::Floor($count / 2)]
        }
        $minimum = [double]$ordered[0]
        $maximum = [double]$ordered[$count - 1]
        return [pscustomobject]@{
            Count = $count
            MedianMs = $median
            P95Ms = [double]$ordered[[int][Math]::Ceiling($count * 0.95) - 1]
            MinMs = $minimum
            MaxMs = $maximum
            SpreadMs = $maximum - $minimum
            SpreadPercent = if ($median -gt 0) { 100 * ($maximum - $minimum) / $median } else { $null }
        }
    }

    function Get-StartupPercentChange($BaselineValue, $CandidateValue) {
        if ($null -eq $BaselineValue -or $null -eq $CandidateValue -or $BaselineValue -le 0) { return $null }
        return 100 * ([double]$CandidateValue / [double]$BaselineValue - 1)
    }

    foreach ($profileMode in @('fresh', 'reused')) {
        # Reused profiles start warmed: the initial creation is excluded from the measured cohort.
        if ($profileMode -eq 'reused') {
            $warmupIteration = $StartupPairs + 1
            Invoke-StartupRun 'baseline' $profileMode $warmupIteration $true
            Invoke-StartupRun 'candidate' $profileMode $warmupIteration $true
        }
        for ($iteration = 1; $iteration -le $StartupPairs; $iteration++) {
            $order = if (($iteration % 2) -eq 1) { @('baseline', 'candidate') } else { @('candidate', 'baseline') }
            foreach ($label in $order) { Invoke-StartupRun $label $profileMode $iteration }
        }
    }

    $startupRuns | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 (Join-Path $startupOutput 'startup-runs.json')
    $startupRuns | Select-Object ProfileMode, Label, Iteration, Warmup, Renderer, Status, FailureReason, ExitCode,
        StartupLatencyMs, FirstFrameLatencyMs, TrayLatencyMs, ProfileRoot, JsonPath, CsvPath |
        Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $startupOutput 'startup-runs.csv')

    $startupComparisons = [System.Collections.Generic.List[object]]::new()
    $metricRows = [System.Collections.Generic.List[object]]::new()
    foreach ($profileMode in @('fresh', 'reused')) {
        $cohort = @($startupRuns | Where-Object { $_.ProfileMode -eq $profileMode -and -not $_.Warmup })
        $baselineFailures = @($cohort | Where-Object { $_.Label -eq 'baseline' -and $_.Status -ne 'passed' })
        $candidateFailures = @($cohort | Where-Object { $_.Label -eq 'candidate' -and $_.Status -ne 'passed' })
        $paired = [System.Collections.Generic.List[object]]::new()
        for ($iteration = 1; $iteration -le $StartupPairs; $iteration++) {
            $baseline = @($cohort | Where-Object { $_.Label -eq 'baseline' -and $_.Iteration -eq $iteration })[0]
            $candidate = @($cohort | Where-Object { $_.Label -eq 'candidate' -and $_.Iteration -eq $iteration })[0]
            if ($baseline.Status -eq 'passed' -and $candidate.Status -eq 'passed') {
                $paired.Add([pscustomobject]@{ Baseline = $baseline; Candidate = $candidate })
            }
        }

        $metrics = [System.Collections.Generic.List[object]]::new()
        foreach ($metric in $startupMetrics) {
            $complete = @($paired | Where-Object { $null -ne $_.Baseline.$metric -and $null -ne $_.Candidate.$metric })
            $baselineStats = Get-StartupStatistics @($complete | ForEach-Object { $_.Baseline.$metric })
            $candidateStats = Get-StartupStatistics @($complete | ForEach-Object { $_.Candidate.$metric })
            $row = [pscustomobject]@{
                ProfileMode = $profileMode
                Metric = $metric
                MatchedPairs = $complete.Count
                BaselineMedianMs = if ($baselineStats) { $baselineStats.MedianMs } else { $null }
                CandidateMedianMs = if ($candidateStats) { $candidateStats.MedianMs } else { $null }
                MedianDeltaMs = if ($baselineStats -and $candidateStats) { $candidateStats.MedianMs - $baselineStats.MedianMs } else { $null }
                MedianDeltaPercent = if ($baselineStats -and $candidateStats) { Get-StartupPercentChange $baselineStats.MedianMs $candidateStats.MedianMs } else { $null }
                BaselineP95Ms = if ($baselineStats) { $baselineStats.P95Ms } else { $null }
                CandidateP95Ms = if ($candidateStats) { $candidateStats.P95Ms } else { $null }
                P95DeltaPercent = if ($baselineStats -and $candidateStats) { Get-StartupPercentChange $baselineStats.P95Ms $candidateStats.P95Ms } else { $null }
                BaselineSpreadMs = if ($baselineStats) { $baselineStats.SpreadMs } else { $null }
                CandidateSpreadMs = if ($candidateStats) { $candidateStats.SpreadMs } else { $null }
                BaselineSpreadPercent = if ($baselineStats) { $baselineStats.SpreadPercent } else { $null }
                CandidateSpreadPercent = if ($candidateStats) { $candidateStats.SpreadPercent } else { $null }
            }
            $metrics.Add($row)
            $metricRows.Add($row)
        }

        $startupComparisons.Add([pscustomobject]@{
            ProfileMode = $profileMode
            RequestedPairs = $StartupPairs
            SuccessfulPairs = $paired.Count
            BaselineFailures = $baselineFailures.Count
            CandidateFailures = $candidateFailures.Count
            WarmupFailures = @($startupRuns | Where-Object {
                $_.ProfileMode -eq $profileMode -and $_.Warmup -and $_.Status -ne 'passed'
            }).Count
            Metrics = $metrics
        })
    }
    $metricRows | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $startupOutput 'startup-comparison.csv')

    $failures = @($startupRuns | Where-Object Status -ne 'passed')
    $startupSummary = [ordered]@{
        Status = if ($failures.Count -gt 0 -or @($startupComparisons | Where-Object { $_.SuccessfulPairs -lt $StartupPairs }).Count -gt 0) { 'failed' } else { 'passed' }
        GeneratedAtUtc = [DateTime]::UtcNow.ToString('o')
        Renderer = 'hardware'
        AlternatingAB = $true
        PairedRepetitionsPerCohort = $StartupPairs
        FreshProfilesAreUniquePerSample = $true
        ReusedProfilesAreSeparatePerLabel = $true
        ReusedProfilesWarmedBeforeMeasurement = $true
        BaselineCommit = Get-AppProductVersion $BaselineApp
        CandidateCommit = Get-AppProductVersion $CandidateApp
        Comparisons = $startupComparisons
        Failures = @($failures | Select-Object ProfileMode, Label, Iteration, Warmup, FailureReason, ExitCode, JsonPath)
    }
    $startupSummary | ConvertTo-Json -Depth 15 | Set-Content -Encoding UTF8 (Join-Path $startupOutput 'startup-summary.json')
    Write-Host "Startup comparison $($startupSummary.Status): $startupOutput"
    if ($startupSummary.Status -ne 'passed') { exit 1 }
    exit 0
}

function Try-LoadExistingAppRun(
    [string] $JsonPath,
    [string] $Label,
    [string] $App,
    [string] $SupervisorPath,
    [string] $Scenario,
    [int] $Iteration
) {
    if (-not $Resume -or -not (Test-Path -LiteralPath $JsonPath -PathType Leaf)) { return $null }
    try {
        $run = Get-Content -LiteralPath $JsonPath -Raw | ConvertFrom-Json
        $expectedCommit = Get-AppProductVersion $App
        $expectedSupervisorCommit = Get-AppProductVersion $SupervisorPath
        $minimumSettledSeconds = [Math]::Max(1, $StabilizationSeconds * 0.95)
        $minimumMeasuredSeconds = [Math]::Max(1, $MeasurementSeconds * 0.95)
        if ($run.Label -ne $Label -or $run.Scenario -ne $Scenario -or [int]$run.Iteration -ne $Iteration) { return $null }
        if ($run.Renderer -ne 'swiftshader' -or $run.Commit -ne $expectedCommit) { return $null }
        if ($run.SupervisorCommit -ne $expectedSupervisorCommit) { return $null }
        if ([double]$run.SettledSeconds -lt $minimumSettledSeconds) { return $null }
        if ([double]$run.MeasuredSeconds -lt $minimumMeasuredSeconds) { return $null }
        return $run
    }
    catch {
        return $null
    }
}

function Invoke-AppRun([string] $Label, [string] $App, [string] $Scenario, [int] $Iteration) {
    $dir = Join-Path $Output ("app-{0}-{1}-{2:D2}" -f $Scenario, $Label, $Iteration)
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $jsonPath = Join-Path $dir 'benchmark-run.json'
    $supervisorPath = if ($Label -eq 'baseline') { $BaselineSupervisor } else { $CandidateSupervisor }
    $existing = Try-LoadExistingAppRun $jsonPath $Label $App $supervisorPath $Scenario $Iteration
    if ($null -ne $existing) {
        Write-Host "Reusing completed benchmark: $Label/$Scenario/$Iteration"
        $runs.Add($existing)
        return
    }
    & $Harness --mode app-benchmark --app $App --supervisor $supervisorPath --scenario $Scenario `
        --label $Label --iteration $Iteration --settle-seconds $StabilizationSeconds `
        --measure-seconds $MeasurementSeconds --renderer swiftshader --output $dir
    if ($LASTEXITCODE -ne 0) { throw "App benchmark failed: $Label/$Scenario/$Iteration" }
    $json = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    $runs.Add($json)
}

function Invoke-Pair([string] $Scenario, [int] $Iteration) {
    if (($Iteration % 2) -eq 1) {
        Invoke-AppRun 'baseline' $BaselineApp $Scenario $Iteration
        Invoke-AppRun 'candidate' $CandidateApp $Scenario $Iteration
    } else {
        Invoke-AppRun 'candidate' $CandidateApp $Scenario $Iteration
        Invoke-AppRun 'baseline' $BaselineApp $Scenario $Iteration
    }
}

function Get-Mean($values) {
    $array = @($values | Where-Object { $null -ne $_ })
    if ($array.Count -eq 0) { return $null }
    return [double](($array | Measure-Object -Average).Average)
}

function Test-Unstable($group) {
    if ($group.Count -lt 2) { return $false }
    $cpu = @($group | ForEach-Object { [double]$_.CpuAveragePercent })
    $cpuMean = Get-Mean $cpu
    $cpuSpread = ($cpu | Measure-Object -Maximum).Maximum - ($cpu | Measure-Object -Minimum).Minimum
    if ($cpuSpread -gt [Math]::Max($cpuMean * 0.20, 0.2)) { return $true }

    $mem = @($group | ForEach-Object { [double]$_.PrivateBytesAverage })
    $memMean = Get-Mean $mem
    $memSpread = ($mem | Measure-Object -Maximum).Maximum - ($mem | Measure-Object -Minimum).Minimum
    if ($memSpread -gt [Math]::Max($memMean * 0.20, 20MB)) { return $true }

    foreach ($metric in @('SyntheticOperationsPerSecond', 'DrawsPerSecond')) {
        $throughput = @($group | Where-Object { $null -ne $_.$metric } | ForEach-Object { [double]$_.$metric })
        if ($throughput.Count -ge 2) {
            $mean = Get-Mean $throughput
            $spread = ($throughput | Measure-Object -Maximum).Maximum - ($throughput | Measure-Object -Minimum).Minimum
            if ($spread -gt $mean * 0.10) { return $true }
        }
    }
    return $false
}

foreach ($scenario in $scenarios) {
    1..$Repetitions | ForEach-Object { Invoke-Pair $scenario $_ }
    $scenarioRuns = @($runs | Where-Object { $_.Scenario -eq $scenario })
    $unstable = (Test-Unstable @($scenarioRuns | Where-Object Label -eq 'baseline')) -or
        (Test-Unstable @($scenarioRuns | Where-Object Label -eq 'candidate'))
    if ($unstable -and $UnstableExtraRepetitions -gt 0) {
        1..$UnstableExtraRepetitions | ForEach-Object { Invoke-Pair $scenario ($Repetitions + $_) }
    }
}

$graphicsRuns = [System.Collections.Generic.List[object]]::new()
function Try-LoadExistingGraphicsRun([string] $JsonPath, [string] $Renderer, [int] $Iteration) {
    if (-not $Resume -or -not (Test-Path -LiteralPath $JsonPath -PathType Leaf)) { return $null }
    try {
        $run = Get-Content -LiteralPath $JsonPath -Raw | ConvertFrom-Json
        $minimumSettledSeconds = [Math]::Max(1, $StabilizationSeconds * 0.95)
        $minimumMeasuredSeconds = [Math]::Max(1, $MeasurementSeconds * 0.95)
        if ($run.Renderer -ne $Renderer -or [int]$run.Iteration -ne $Iteration) { return $null }
        if ([double]$run.SettledSeconds -lt $minimumSettledSeconds) { return $null }
        if ([double]$run.MeasuredSeconds -lt $minimumMeasuredSeconds) { return $null }
        return $run
    }
    catch {
        return $null
    }
}

function Invoke-GraphicsRun([string] $Renderer, [int] $Iteration) {
    $dir = Join-Path $Output ("graphics-{0}-{1:D2}" -f $Renderer, $Iteration)
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $jsonPath = Join-Path $dir 'graphics-benchmark.json'
    $existing = Try-LoadExistingGraphicsRun $jsonPath $Renderer $Iteration
    if ($null -ne $existing) {
        Write-Host "Reusing completed graphics benchmark: $Renderer/$Iteration"
        $graphicsRuns.Add($existing)
        return
    }
    & $Harness --mode graphics-benchmark --renderer $Renderer --label renderer --iteration $Iteration `
        --settle-seconds $StabilizationSeconds --measure-seconds $MeasurementSeconds --output $dir
    if ($LASTEXITCODE -ne 0) { throw "Graphics benchmark failed: $Renderer/$Iteration" }
    $graphicsRuns.Add((Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json))
}

function Invoke-GraphicsPair([int] $Iteration) {
    if (($Iteration % 2) -eq 1) {
        Invoke-GraphicsRun 'swiftshader' $Iteration
        Invoke-GraphicsRun 'hardware' $Iteration
    } else {
        Invoke-GraphicsRun 'hardware' $Iteration
        Invoke-GraphicsRun 'swiftshader' $Iteration
    }
}

1..$Repetitions | ForEach-Object { Invoke-GraphicsPair $_ }
$graphicsUnstable = (Test-Unstable @($graphicsRuns | Where-Object Renderer -eq 'swiftshader')) -or
    (Test-Unstable @($graphicsRuns | Where-Object Renderer -eq 'hardware'))
if ($graphicsUnstable -and $UnstableExtraRepetitions -gt 0) {
    1..$UnstableExtraRepetitions | ForEach-Object { Invoke-GraphicsPair ($Repetitions + $_) }
}

$appCsv = Join-Path $Output 'app-benchmark-all.csv'
$runs | Select-Object Label, Scenario, Iteration, Renderer, Commit, SupervisorPath, SupervisorCommit, Machine, Os, Runtime, WebViewRuntime,
    CpuAveragePercent, CpuP95Percent, GpuAveragePercent, GpuP95Percent, GpuMeasurementStatus,
    VramPressurePercent, VramMeasurementStatus, PrivateBytesAverage, PrivateBytesMax, WorkingSetAverage,
    PrivateWorkingSetAverage, MaxProcessCount, SampleCount, SettledSeconds, MeasuredSeconds, RestoreLatencyMs,
    FreshDataLatencyMs, SyntheticOperationsPerSecond, ProtectedWorkloadObserved | Export-Csv -NoTypeInformation -Encoding UTF8 $appCsv

$graphicsCsv = Join-Path $Output 'graphics-benchmark-all.csv'
$graphicsRuns | Export-Csv -NoTypeInformation -Encoding UTF8 $graphicsCsv

$comparisons = [System.Collections.Generic.List[object]]::new()
foreach ($scenario in $scenarios) {
    $baseline = @($runs | Where-Object { $_.Scenario -eq $scenario -and $_.Label -eq 'baseline' })
    $candidate = @($runs | Where-Object { $_.Scenario -eq $scenario -and $_.Label -eq 'candidate' })
    $bCpu = Get-Mean ($baseline | ForEach-Object CpuAveragePercent)
    $cCpu = Get-Mean ($candidate | ForEach-Object CpuAveragePercent)
    $bMem = Get-Mean ($baseline | ForEach-Object PrivateBytesAverage)
    $cMem = Get-Mean ($candidate | ForEach-Object PrivateBytesAverage)
    $bThroughput = Get-Mean ($baseline | ForEach-Object SyntheticOperationsPerSecond)
    $cThroughput = Get-Mean ($candidate | ForEach-Object SyntheticOperationsPerSecond)
    $freshData = @($candidate | Where-Object { $null -ne $_.FreshDataLatencyMs } | ForEach-Object { [double]$_.FreshDataLatencyMs })
    $protectedObserved = @($candidate | Where-Object { $null -ne $_.ProtectedWorkloadObserved } | ForEach-Object { [bool]$_.ProtectedWorkloadObserved })
    $cpuRegressionLimit = [Math]::Max($bCpu * 0.10, 0.2)
    $memRegressionLimit = [Math]::Max($bMem * 0.10, 20MB)
    $persistentInstability = (Test-Unstable $baseline) -or (Test-Unstable $candidate)
    $comparisons.Add([pscustomobject]@{
        Scenario = $scenario
        BaselineCpuAvg = $bCpu
        CandidateCpuAvg = $cCpu
        CpuDelta = $cCpu - $bCpu
        CpuRegression = (($cCpu - $bCpu) -gt $cpuRegressionLimit)
        BaselinePrivateBytes = [long]$bMem
        CandidatePrivateBytes = [long]$cMem
        PrivateBytesDelta = [long]($cMem - $bMem)
        MemoryRegression = (($cMem - $bMem) -gt $memRegressionLimit)
        BaselineSyntheticOpsPerSec = $bThroughput
        CandidateSyntheticOpsPerSec = $cThroughput
        ThroughputRegression = ($null -ne $bThroughput -and $null -ne $cThroughput -and $cThroughput -lt $bThroughput * 0.95)
        CandidateFreshDataMaxMs = if ($scenario -eq 'restore' -and $freshData.Count -gt 0) { ($freshData | Measure-Object -Maximum).Maximum } else { $null }
        FreshDataWithin2s = if ($scenario -eq 'restore') { $freshData.Count -eq $candidate.Count -and $freshData.Count -gt 0 -and (($freshData | Measure-Object -Maximum).Maximum -le 2000) } else { $null }
        ProtectedWorkloadObserved = if ($scenario -eq 'protected-cpu') { $protectedObserved.Count -eq $candidate.Count -and $protectedObserved.Count -gt 0 -and -not ($protectedObserved -contains $false) } else { $null }
        Inconclusive = $persistentInstability
        Repetitions = [Math]::Min($baseline.Count, $candidate.Count)
    })
}

$swift = @($graphicsRuns | Where-Object Renderer -eq 'swiftshader')
$hardware = @($graphicsRuns | Where-Object Renderer -eq 'hardware')
$swiftCpu = Get-Mean ($swift | ForEach-Object CpuAveragePercent)
$hardwareCpu = Get-Mean ($hardware | ForEach-Object CpuAveragePercent)
$swiftDraws = Get-Mean ($swift | ForEach-Object DrawsPerSecond)
$hardwareDraws = Get-Mean ($hardware | ForEach-Object DrawsPerSecond)
$swiftPrivate = Get-Mean ($swift | ForEach-Object PrivateBytesAverage)
$hardwarePrivate = Get-Mean ($hardware | ForEach-Object PrivateBytesAverage)
$swiftVramMax = if (@($swift | Where-Object { $null -ne $_.VramBytesMax }).Count -gt 0) { [double](($swift | Measure-Object VramBytesMax -Maximum).Maximum) } else { $null }
$hardwareVramMax = if (@($hardware | Where-Object { $null -ne $_.VramBytesMax }).Count -gt 0) { [double](($hardware | Measure-Object VramBytesMax -Maximum).Maximum) } else { $null }
$hardwareVerified = -not ($hardware.Status -contains 'not_verified') -and -not ($hardware.Status -contains 'failed')
$vramVerified = -not ($swift.VramStatus -contains 'not_verified') -and -not ($hardware.VramStatus -contains 'not_verified') -and $null -ne $swiftVramMax -and $null -ne $hardwareVramMax
$rendererBenefit = (($hardwareCpu -le $swiftCpu * 0.90) -or ($hardwareDraws -ge $swiftDraws * 1.10))
$rendererPrivateLimit = [Math]::Max($swiftPrivate * 0.10, 20MB)
$rendererMemoryOk = (($hardwarePrivate - $swiftPrivate) -le $rendererPrivateLimit)
$rendererVramLimit = if ($null -ne $swiftVramMax) { [Math]::Max($swiftVramMax * 0.10, 32MB) } else { 32MB }
$rendererVramOk = $vramVerified -and (($hardwareVramMax - $swiftVramMax) -le $rendererVramLimit)
$rendererInconclusive = (Test-Unstable $swift) -or (Test-Unstable $hardware)
$protocolQualifies = $StabilizationSeconds -ge 30 -and $MeasurementSeconds -ge 120 -and $Repetitions -ge 5
$rendererCpuOk = ($hardwareCpu - $swiftCpu) -le [Math]::Max($swiftCpu * 0.10, 0.2)
$rendererThroughputOk = $hardwareDraws -ge $swiftDraws * 0.95
$appRegressions = @($comparisons | Where-Object { $_.CpuRegression -or $_.MemoryRegression -or $_.ThroughputRegression -or $_.FreshDataWithin2s -eq $false -or $_.ProtectedWorkloadObserved -eq $false })
$appInconclusive = @($comparisons | Where-Object Inconclusive).Count -gt 0
$rendererDecision = if ($protocolQualifies -and $hardwareVerified -and $vramVerified -and $rendererBenefit -and $rendererCpuOk -and $rendererThroughputOk -and $rendererMemoryOk -and $rendererVramOk -and -not $rendererInconclusive -and -not $appInconclusive -and $appRegressions.Count -eq 0) { 'use_hardware' } else { 'keep_swiftshader' }
$rendererReason = if (-not $protocolQualifies) {
    'Benchmark protocol is shorter than the 30s stabilization / 120s measurement / 5-repetition acceptance protocol.'
} elseif (-not $hardwareVerified) {
    'Hardware backend was not verified in every run.'
} elseif (-not $vramVerified) {
    'Per-process VRAM was not verified for every renderer; evidence is incomplete.'
} elseif ($rendererInconclusive) {
    'Renderer measurements remained unstable after the configured repetitions.'
} elseif ($appRegressions.Count -gt 0 -or $appInconclusive) {
    'Application scenarios contain regressions or inconclusive results.'
} elseif (-not $rendererCpuOk -or -not $rendererThroughputOk) {
    'Hardware regressed CPU use or synthetic throughput beyond the acceptance limit.'
} elseif (-not $rendererBenefit) {
    'Hardware did not meet the required >=10% CPU or synthetic-throughput benefit.'
} elseif (-not $rendererMemoryOk) {
    'Hardware exceeded the allowed private-memory regression threshold.'
} elseif (-not $rendererVramOk) {
    'Hardware exceeded the allowed VRAM increase threshold.'
} else {
    'Hardware met the backend, performance, stability, private-memory and VRAM acceptance criteria.'
}

$hardwareInfo = [ordered]@{}
try { $hardwareInfo.Cpu = (Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name) -join '; ' } catch { $hardwareInfo.Cpu = 'unavailable' }
try { $hardwareInfo.Gpu = (Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name) -join '; ' } catch { $hardwareInfo.Gpu = 'unavailable' }
try { $hardwareInfo.MemoryBytes = [long](Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory } catch { $hardwareInfo.MemoryBytes = 0 }
$firstRun = @($runs | Select-Object -First 1)[0]
$hardwareInfo.WebViewRuntime = if ($firstRun.PSObject.Properties.Name -contains 'WebViewRuntime') { $firstRun.WebViewRuntime } else { 'unavailable' }
$hardwareInfo.DotNetRuntime = if ($firstRun.PSObject.Properties.Name -contains 'Runtime') { $firstRun.Runtime } else { [Environment]::Version.ToString() }
$hardwareInfo.Os = if ($firstRun.PSObject.Properties.Name -contains 'Os') { $firstRun.Os } else { [Environment]::OSVersion.VersionString }

$providerSummary = [System.Collections.Generic.List[object]]::new()
foreach ($scenario in $scenarios) {
    foreach ($label in @('baseline','candidate')) {
        $group = @($runs | Where-Object { $_.Scenario -eq $scenario -and $_.Label -eq $label })
        foreach ($name in @('MonitorTicks','ProcessSnapshots','GpuSamples','VramSamples','HardwareRpcReads','UiMetricPublications')) {
            $providerSummary.Add([pscustomobject]@{
                Scenario = $scenario
                Label = $label
                Provider = $name
                MeanCount = Get-Mean ($group | ForEach-Object { $_.ProviderActivity.$name })
            })
        }
    }
}
$providerSummary | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $Output 'provider-activity.csv')

$summary = [ordered]@{
    status = if ($appRegressions.Count -gt 0) { 'failed' } elseif (-not $protocolQualifies -or $appInconclusive -or $rendererInconclusive) { 'inconclusive' } else { 'passed' }
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    protocol = [ordered]@{ stabilizationSeconds=$StabilizationSeconds; measurementSeconds=$MeasurementSeconds; baseRepetitions=$Repetitions; unstableExtraRepetitions=$UnstableExtraRepetitions; alternating=$true; qualifiesForAcceptance=$protocolQualifies }
    baselineCommit = @($runs | Where-Object Label -eq 'baseline' | Select-Object -First 1 -ExpandProperty Commit)[0]
    candidateCommit = @($runs | Where-Object Label -eq 'candidate' | Select-Object -First 1 -ExpandProperty Commit)[0]
    baselineSupervisorCommit = @($runs | Where-Object Label -eq 'baseline' | Select-Object -First 1 -ExpandProperty SupervisorCommit)[0]
    candidateSupervisorCommit = @($runs | Where-Object Label -eq 'candidate' | Select-Object -First 1 -ExpandProperty SupervisorCommit)[0]
    hardware = $hardwareInfo
    comparisons = $comparisons
    renderer = [ordered]@{
        decision = $rendererDecision
        reason = $rendererReason
        swiftShaderCpuAvg = $swiftCpu
        hardwareCpuAvg = $hardwareCpu
        swiftShaderDrawsPerSec = $swiftDraws
        hardwareDrawsPerSec = $hardwareDraws
        hardwareBackendVerified = $hardwareVerified
        swiftShaderPrivateBytesAvg = $swiftPrivate
        hardwarePrivateBytesAvg = $hardwarePrivate
        swiftShaderVramBytesMax = $swiftVramMax
        hardwareVramBytesMax = $hardwareVramMax
        vramIncreaseLimitBytes = $rendererVramLimit
        vramValidation = if ($vramVerified) { 'measured' } else { 'not_verified' }
        inconclusive = $rendererInconclusive
    }
    vramValidation = [ordered]@{ status=if ($vramVerified) { 'measured' } else { 'not_verified' }; reason=if ($vramVerified) { 'GPU Adapter Memory/DXGI and GPU Process Memory samples were available.' } else { 'GPU Adapter Memory/DXGI or per-process GPU memory samples were unavailable.' } }
}
$summary | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 (Join-Path $Output 'benchmark-summary.json')

$md = [System.Collections.Generic.List[string]]::new()
$md.Add('# VoltManager resource benchmark')
$md.Add('')
$md.Add("Baseline: ``$($summary.baselineCommit)``  ")
$md.Add("Candidate: ``$($summary.candidateCommit)``  ")
$md.Add("Baseline supervisor: ``$($summary.baselineSupervisorCommit)``  ")
$md.Add("Candidate supervisor: ``$($summary.candidateSupervisorCommit)``  ")
$md.Add("Protocol: $StabilizationSeconds s stabilization + $MeasurementSeconds s measurement, $Repetitions alternating repetitions; unstable series receive $UnstableExtraRepetitions extra repetitions.")
$md.Add('')
$md.Add('## Hardware')
$md.Add('')
$md.Add("- CPU: $($hardwareInfo.Cpu)")
$md.Add("- GPU: $($hardwareInfo.Gpu)")
$md.Add("- RAM: $([Math]::Round($hardwareInfo.MemoryBytes / 1GB, 1)) GiB")
$md.Add("- WebView2: $($hardwareInfo.WebViewRuntime)")
$md.Add("- .NET: $($hardwareInfo.DotNetRuntime)")
$md.Add('')
$md.Add('## Baseline vs candidate')
$md.Add('')
$md.Add('| Scenario | CPU baseline | CPU candidate | Private baseline MiB | Private candidate MiB | Synthetic throughput change | Status |')
$md.Add('|---|---:|---:|---:|---:|---:|---|')
foreach ($row in $comparisons) {
    $throughput = if ($null -ne $row.BaselineSyntheticOpsPerSec -and $row.BaselineSyntheticOpsPerSec -gt 0) { '{0:+0.0;-0.0;0.0}%' -f (($row.CandidateSyntheticOpsPerSec / $row.BaselineSyntheticOpsPerSec - 1) * 100) } else { 'n/a' }
    $status = if ($row.CpuRegression -or $row.MemoryRegression -or $row.ThroughputRegression -or $row.FreshDataWithin2s -eq $false -or $row.ProtectedWorkloadObserved -eq $false) { 'REGRESSION' } elseif (-not $protocolQualifies -or $row.Inconclusive) { 'INCONCLUSIVE' } else { 'PASS' }
    $md.Add("| $($row.Scenario) | $([Math]::Round($row.BaselineCpuAvg,3))% | $([Math]::Round($row.CandidateCpuAvg,3))% | $([Math]::Round($row.BaselinePrivateBytes/1MB,1)) | $([Math]::Round($row.CandidatePrivateBytes/1MB,1)) | $throughput | $status |")
}
$md.Add('')
$md.Add('## Renderer decision')
$md.Add('')
$md.Add("**$($rendererDecision.ToUpperInvariant()).** $rendererReason")
$md.Add("SwiftShader: CPU $([Math]::Round($swiftCpu,3))%, $([Math]::Round($swiftDraws,0)) draws/s. Hardware: CPU $([Math]::Round($hardwareCpu,3))%, $([Math]::Round($hardwareDraws,0)) draws/s.")
$md.Add("VRAM max: SwiftShader $([Math]::Round($swiftVramMax/1MB,1)) MiB; hardware $([Math]::Round($hardwareVramMax/1MB,1)) MiB; allowed increase $([Math]::Round($rendererVramLimit/1MB,1)) MiB.")
$md.Add('')
$md.Add('## Hardware validation gaps')
$md.Add('')
$md.Add($(if ($vramVerified) { '- **VRAM: MEASURED.** Adapter/global and per-process GPU memory counters were available.' } else { '- **VRAM: NOT VERIFIED.** Required counters were unavailable; this is not counted as a pass.' }))
$md.Add('- Real multi-monitor coverage is reported separately by the Windows harness and is not inferred from deterministic geometry tests.')
$md | Set-Content -Encoding UTF8 (Join-Path $Output 'benchmark-summary.md')

Write-Host "Resource benchmark complete: $Output"
if ($summary.status -eq 'failed') { exit 1 }
if ($summary.status -eq 'inconclusive') { exit 2 }
