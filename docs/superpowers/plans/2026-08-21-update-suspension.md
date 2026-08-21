# Automatic update startup check and suspension

**Goal:** Make automatic update checks start immediately with VoltManager, run every 15 minutes, and let users suspend automatic updates for 1, 5, 7, or 12 days without removing the existing short "snooze" flow.

## Current behavior verified on `Dev`

- `MainWindow` owns the automatic update timer.
- The periodic timer currently starts only when WebView2 is initialized, so tray-only startup can postpone the updater lifecycle.
- The default configured interval is 30 minutes; the 10-second value in `UpdateService` is the HTTP timeout, not the polling interval.
- `AutoUpdateSettings.SnoozedUntilUtc` already persists a deadline and the periodic updater already honors it.
- The update modal already supports short snoozes (15/30/60/120 minutes).

## Design

1. Centralize update timing rules in a small `UpdateSchedulePolicy` service:
   - automatic interval: 15 minutes;
   - automatic checks are disabled while updates are disabled or a snooze/suspension deadline is still active;
   - long suspension maximum: 12 days.
2. Start the updater lifecycle from `MainWindow` construction rather than WebView initialization so minimized/tray startup still checks immediately.
3. Keep the WebView update modal for foreground sessions, but fall back to the native update prompt when the WebView bridge is not ready yet.
4. Reuse `SnoozedUntilUtc` as the persisted automatic-update pause deadline. Short popup snooze and long settings suspension share the same suppression primitive but keep separate UI affordances.
5. Extend the settings UI with a compact, accessible suspension control offering exactly 1, 5, 7, and 12 days plus "Resume now".
6. Manual "Check for updates" remains available during suspension.

## Regression risks

- Duplicate startup checks if the old WebView-bound startup hook is not removed.
- A suspended app accidentally performing the network request before checking the deadline.
- Existing 30-minute persisted values preventing the new 15-minute cadence.
- Long suspensions being truncated by the existing 24-hour backend clamp.
- Settings UI state becoming stale after suspend/resume or language change.

## Verification

- Unit tests for interval, enabled/suspended policy, expiration, and 12-day clamp.
- JavaScript regression test for 15-minute copy and 1/5/7/12-day controls.
- Full repository CI: restore, Release build/type check, .NET tests, JavaScript tests, JS syntax check, portable package smoke test.
