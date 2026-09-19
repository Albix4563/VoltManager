# Issue #32 Resource Validation Runtime Crash

## Goal

Close issue #32 by making the deterministic resource-validation test reliable on Windows, preserving actionable diagnostics for support-process failures, and keeping benchmark exit-code, status, and renderer assertions strict for all six deterministic scenarios.

## Root-cause evidence

The failure is outside VoltManager and outside the deterministic benchmark semantics:

- Local host: Windows 10.0.19044.
- PowerShell 7.6.6 runs on .NET 10.0.12, CoreCLR 10.0.1226.41902.
- Failing child exit: 3221225477 (0xC0000005) with stderr Internal CLR error. (0x80131506).
- Windows Application log records .NET Runtime event 1023 for pwsh.exe, stating that CoreCLR terminated due to an internal runtime error with 0x80131506.
- The original isolated Node test reproduced intermittently across different scenarios: one five-run sample produced four passes and one CLR crash; earlier runs crashed in both app-regression and cpu-regression.
- Removing the script CIM hardware collection did not remove the failure: a ten-run sample still produced two CLR crashes, including ok short-protocol and app-regression. Therefore Get-CimInstance is not the trigger.
- Running the same six-scenario test through Windows PowerShell 5.1 completed five consecutive full runs without a crash.

The reproducible constraint is therefore the PowerShell 7.6.6/.NET 10.0.12 support-process runtime on this Windows host under repeated deterministic test execution. The test must not retry or reinterpret that crash as benchmark instability.

## Required behavior

1. On Windows, the deterministic Node resource-validation test uses Windows PowerShell 5.1 by default. A test-only environment override may select another PowerShell host for diagnosis.
2. Benchmark exit codes 0, 1, and 2 remain benchmark-domain results. Every scenario must assert the expected exit code, benchmark-summary.json status, and renderer decision.
3. Timeouts, native crash exit codes, spawn failures, and other non-benchmark exit codes are support-process failures. They must fail the test before benchmark summaries are interpreted.
4. A support-process failure writes durable diagnostics under TestResults/resource-validation-process-failures/, including scenario, expected/actual exit data, stdout, stderr, PowerShell/runtime probe information, and any partial benchmark output.
5. No automatic retry is permitted.
6. The existing CI diagnostics upload already includes TestResults/**; no additional artifact channel is required.

## Deterministic scenarios

| Scenario | Short protocol | Exit | Summary status | Renderer decision |
|---|---:|---:|---|---|
| ok | false | 0 | passed | use_hardware |
| cpu-regression | false | 0 | passed | keep_swiftshader |
| throughput-regression | false | 0 | passed | keep_swiftshader |
| app-regression | false | 1 | failed | keep_swiftshader |
| unstable | false | 2 | inconclusive | keep_swiftshader |
| ok | true | 2 | inconclusive | keep_swiftshader |

## Completion evidence

- Unit tests prove native CLR crash 0xC0000005 and timeout/spawn failures cannot be classified as valid benchmark outcomes.
- Unit tests prove durable failure diagnostics contain the process/runtime evidence and copied partial output.
- The isolated resource-validation test passes repeatedly on Windows.
- The full JavaScript test suite passes repeatedly locally.
- CI on Dev completes successfully after the change is pushed.

