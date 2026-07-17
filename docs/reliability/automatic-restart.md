# Automatic crash restart

## Scope

VoltManager is a Windows desktop application, not a Windows service. Restart is therefore delegated to the external `VoltManager.Supervisor.exe` process shipped beside `VoltManager.exe`. The application never attempts to restart itself after a crash.

Existing shortcuts, the installer, updates and the `VoltManagerAutostart` scheduled task continue to launch `VoltManager.exe`. On the first launch the small bootstrap starts the supervisor and exits. The supervisor then starts the application with `--supervised`.

## Failure model

The supervisor restarts the child when it exits with any non-zero code, including:

- startup failure (`1`);
- fatal unhandled UI exception (`11`);
- other unhandled CLR/native failures;
- forced process termination, whose Windows status is exposed as a non-zero exit code;
- failure to create the child process (`30`, internal supervisor classification).

Exit code `0` is treated as an intentional shutdown and is never restarted.

An unobserved task exception is recorded but is not automatically classified as fatal because it may belong to an abandoned background operation and does not prove that process state is corrupt.

## Restart policy

Default policy:

| Parameter | Value |
|---|---:|
| Initial delay | 2 seconds |
| Exponential cap | 60 seconds |
| Jitter | ±20% |
| Maximum automatic restarts | 5 |
| Attempt window | 10 minutes |
| Stable-period reset | 5 minutes |
| Fatal application cleanup budget | 8 seconds |
| Supervisor-to-child shutdown grace | 8 seconds |

After the sixth abnormal termination inside the 10-minute window, the supervisor opens the crash-loop breaker and exits. The block remains until the attempt window expires. The persisted state is reset automatically when the child binary version changes or after a child run lasting at least five minutes.

A user launch while the supervisor is sleeping signals `VoltManager_Supervisor_Wake_Event`, which interrupts the delay. It does not create a second application instance.

## Single-instance and data-safety guarantees

- The existing `VoltManager_SingleInstance_Mutex` remains authoritative for the application.
- `VoltManager_Supervisor_Mutex` prevents concurrent supervisors.
- Restart never modifies `settings.json`, battery history, jobs or queued actions.
- The supervisor passes the original command-line arguments only to the child and does not log them.
- Application cleanup attempts to dispose timers, monitor subscriptions, remote events, widgets, the keep-awake handle and the application mutex before fatal exit.
- Cleanup steps have bounded execution time; a blocked resource cannot prevent process termination indefinitely.
- Supervisor state is written through a temporary file and atomic replacement. Invalid JSON is renamed to `state.json.corrupt.<timestamp>` instead of being deleted.

The existing application logic remains responsible for idempotency of scheduled actions and power-plan changes. The supervisor only starts one child at a time and never replays application operations itself.

## Diagnostics and monitoring

Paths:

- application log: `%APPDATA%\VoltManager\logs\voltmanager.log`;
- structured supervisor events: `%APPDATA%\VoltManager\logs\supervisor-events.jsonl`;
- restart budget state: `%APPDATA%\VoltManager\supervisor\state.json`;
- application crash reports: `%APPDATA%\VoltManager\crashes\crash-*.json`.

Important supervisor events:

- `child_started`;
- `child_exited_abnormally`;
- `restart_scheduled`;
- `restart_counter_reset_after_stable_run`;
- `restart_budget_exhausted`;
- `crash_loop_blocked`;
- `state_corrupt_quarantined`.

Crash JSON intentionally omits exception messages and command-line arguments. It retains exception type, HRESULT, source, stack trace, inner exception types, process uptime, application version and OS version. The existing text log remains the detailed diagnostic source.

## Crash-loop recovery

1. Inspect the latest `crash-*.json`, `voltmanager.log` and `supervisor-events.jsonl`.
2. Correct or roll back the fault before clearing the breaker.
3. Wait for the 10-minute window to expire, install a newer build, or explicitly run:

   ```powershell
   .\VoltManager.Supervisor.exe --reset-state --child .\VoltManager.exe
   ```

4. Start `VoltManager.exe` normally and verify a `child_started` event followed by stable operation.

Do not delete crash reports during incident response.

## Safe mode

No safe mode is introduced. VoltManager has no verified read-only or reduced subsystem that can safely run while core initialization, settings, automation or WebView state is suspected to be corrupt. Automatically suppressing features after a crash would risk presenting an apparently healthy process and hiding the root cause.

## Verification

Automated Windows verification:

```powershell
dotnet restore VoltManager.sln
dotnet build VoltManager.sln -c Release --no-restore
dotnet test tests\VoltManager.Tests\VoltManager.Tests.csproj -c Release --no-build
```

The deterministic suite covers unhandled-exception exit, explicit error exit, forced termination status, startup crash, rapid repeated crashes, exponential backoff, restart-budget exhaustion, stable reset, supervisor mutex exclusion, bounded cleanup, file-lock release, atomic state persistence and corrupt-state quarantine.

Limitations:

- tests simulate process lifetime and time; they do not intentionally crash the GitHub Actions runner desktop session;
- a manual Windows smoke test is still required to verify UAC inheritance, tray shutdown, installer payload and Task Scheduler behavior on a real machine;
- forced OS logoff and machine power loss cannot guarantee application cleanup, but persisted supervisor state uses atomic replacement and is recoverable.

## Rollback

Rollback is file-level and does not require a data migration:

1. revert the reliability PR;
2. rebuild installer and portable artifacts;
3. deploy the previous version.

The supervisor state and event files are additive. Older VoltManager versions ignore them. They may be retained for diagnostics or removed after rollback while the application is stopped.
