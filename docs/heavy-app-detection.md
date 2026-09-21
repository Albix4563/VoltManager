# Heavy app detection pipeline

`HeavyAppDetectionService` coordinates one periodic scan. The scan is split into three explicit stages:

1. `HeavyAppEvidenceCollector` acquires the shared `ProcessSnapshot`, resolves process paths, captures foreground/fullscreen state, reads optional GPU 3D counters, and owns the 30-second Windows GPU-preference cache. The collector accepts clock and external-source delegates for deterministic tests.
2. `HeavyAppClassifier` is pure over the captured evidence plus `HeavyAppDetectionSettings`. It performs no registry, process, window, or GPU reads.
3. `HeavyAppTracker` owns sticky temporal state and delegates the existing PID/start-time-safe merge rules.

The ordering and thresholds are intentionally unchanged by the refactor. Explicit `PriorityApplicationPaths` still work when automatic detection is disabled. When automatic detection is enabled, the existing assessment/classification order remains authoritative: confidence scores at or above `GameConfidenceScorer.GameThreshold` (`60`) classify as `game`; strong Windows GPU preference/install-path/binary-layout evidence can classify as `heavyApp` without live GPU counters; weaker runtime evidence requires the resource gate. The default resource gate is `MinWorkingSetMb = 1536`, normalized by settings to the existing allowed range.

Only detections classified as `game` become sticky. Sticky identity is `(PID, start time)` when start time is available; a reused PID with a different start time is dropped. An alt-tabbed/minimized game remains tracked while the same process identity is alive even if its working set falls below the threshold. `heavyApp` and `priorityApp` detections are non-sticky and disappear when they stop qualifying. Existing install-root handoff rules continue to transfer a sticky game session from a bootstrap process to a valid peer binary while excluding helpers and storefront shells.

The collector performs one `ProcessSnapshotProvider.Get` per heavy-app scan. That snapshot remains shared with other scanners through the provider's existing max-age cache, so this refactor does not add a second process enumeration. Overlapping service scans remain prevented by the service's existing interlocked scan guard.
