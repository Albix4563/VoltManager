# Update, install and uninstall recovery

VoltManager keeps network selection, downloading, setup orchestration, and operating-system changes behind separate boundaries so each stage can fail without hiding the diagnostic from the caller.

## Update pipeline ownership

- `UpdateChannelPolicy` owns stable/preview/dev matching, branch selection, and version normalization.
- `GitHubUpdateClient` owns GitHub release/commit HTTP calls. Responses are disposed by the client and caller cancellation is propagated.
- `UpdateDownloadClient` owns the application update download. It writes only the configured update destination, applies an inactivity timeout to each read, propagates caller cancellation, verifies a known content length, and removes a partial destination after any failed download.
- `UpdatePresentation` owns update-facing messages. `UpdateService` remains the compatibility facade used by the application.
- `GitHubPreviewReleaseClient` owns the Setup Preview release HTTP flow. Its `HttpClient`, responses, response streams, and destination stream have explicit lifetimes. A failed Preview asset download removes its partial temporary executable.

## Interrupted update recovery

Before the downloaded setup starts, a cancelled, stalled, offline, or truncated application download leaves the installed application untouched. The partial `%TEMP%\VoltManagerUpdate.exe` is removed and the update can be retried normally.

Once Setup begins replacing an existing installation, the update workflow records the exact failing step in `LastOperationResult`. If replacement is interrupted during extraction or registration, run the same installer again. Setup re-resolves the installation directory, stops owned VoltManager processes, clears the application payload directory, and extracts the complete payload again before re-registering the installation. The updater does not report success until every workflow step completes.

The WebView2 profile is disposable update cache/state. `UpdateInstallCoordinator` clears that profile only after the main process has exited and before payload replacement. Failure to clear it aborts preparation before `InstallEngine.UpdateAsync` runs.

## Paths and artifacts Setup may delete

Deletion is limited to VoltManager-owned locations and the explicit artifact manifest:

- the resolved VoltManager installation directory during uninstall or payload replacement;
- the VoltManager application-data directory during full uninstall;
- VoltManager Start Menu/Desktop shortcuts;
- the VoltManager startup scheduled task;
- VoltManager current and legacy uninstall registry keys;
- allow-listed VoltManager temporary artifacts such as update/setup payload files and cleanup scripts.

The cleanup helpers do not enumerate arbitrary temporary files. The running temporary uninstaller is excluded from synchronous cleanup and schedules its own deletion only when the Setup process exits.

## Uninstall result semantics

`UninstallResult.Failures` contains failures while performing an operation, such as an owned process that could not be stopped or a directory deletion error. `UninstallResult.Residuals` contains artifacts found by the final verification pass after cleanup. `Success` is true only when both collections are empty.

The final verification checks owned processes, install/AppData directories, startup task, shortcuts, uninstall registry keys, and allow-listed temporary artifacts. Re-running uninstall after the owned artifacts are already absent is supported and treated as a successful idempotent operation.

## Controlled Windows tests

`VoltManager.Setup.Tests` targets .NET Framework 4.8 and references the real Setup project. Tests use injected process/system adapters for process-active, cancellation, cache-preparation, and residual/error cases. The only destructive filesystem test operates under a unique directory below `%TEMP%`: it holds one file open to validate the locked-file failure path, then releases the lock and verifies deletion plus a repeated delete. No test launches VoltManager or removes a real installation path.
