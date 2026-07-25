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

An unobserved task exception is logged by the existing global handler but is not automatically classified as fatal because it may belong to an abandoned background operation and does not prove that process state is corrupt.

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
- WPF windows and the application mutex are released on their owning UI thread; a hard timer still bounds the complete shutdown.
- Worker-safe cleanup steps have per-step and total time limits; a blocked resource cannot prevent process termination indefinitely.
- Supervisor state is written through a temporary file and atomic replacement. Invalid JSON is renamed to `state.json.corrupt.<timestamp>` instead of being deleted.
- If supervisor-state persistence fails, the in-memory restart budget remains active and `state_save_failed` is emitted instead of crashing the supervisor.
- Battery history replaces the previous file only after the new JSON is ready, avoiding the former delete-before-move gap.

The existing scheduled-power service uses an at-most-once safety policy: it commits the disabled/triggered state before invoking shutdown, restart or sleep. A process restart therefore cannot duplicate the operation. The unavoidable trade-off is a narrow loss window if the process dies after that commit but before the operating-system action begins. Retrying automatically would be unsafe because the supervisor cannot distinguish “not executed” from “already accepted by Windows”.

Power-plan changes are naturally idempotent, and the supervisor never replays application operations itself. It only starts one child at a time.

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
- `state_corrupt_quarantined`;
- `state_save_failed`.

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
.\build.ps1 -SkipInstaller
```

The deterministic suite covers unhandled-exception exit, explicit error exit, forced termination status, startup crash, rapid repeated crashes, exponential backoff, restart-budget exhaustion, stable reset, supervisor mutex exclusion, bounded cleanup, file-lock release, atomic supervisor state persistence, corrupt-state quarantine, secret-safe crash diagnostics and battery-history persistence.

Limitations:

- forced OS logoff and machine power loss cannot guarantee application cleanup, but persisted supervisor state and application data files use recoverable replacement strategies;
- no distributed exactly-once protocol exists for the Windows shutdown/restart/sleep side effects; the application intentionally chooses at-most-once execution.

## Rollback

Rollback is file-level and does not require a data migration:

1. revert the reliability PR;
2. rebuild installer and portable artifacts;
3. deploy the previous version.

The supervisor state and event files are additive. Older VoltManager versions ignore them. They may be retained for diagnostics or removed after rollback while the application is stopped.
