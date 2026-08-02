# Game Detection Evidence Engine Design

**Status:** approved in conversation on 2026-08-02

## Goal

Introduce the first independently testable slice of VoltManager's multi-signal game detection architecture without changing the existing power-plan activation behavior.

## Scope

This increment adds:

- a process graph built from the existing shared process snapshot;
- structured detection evidence grouped by identity, provenance, runtime, history, and penalty;
- a capped confidence score from 0 to 100;
- confidence levels `ignored`, `unknown`, `probable`, and `confirmed`;
- launcher-ancestry evidence for detected game/heavy-app processes;
- JSON-visible confidence and evidence on `DetectedHeavyApp`;
- automated unit tests for graph traversal, scoring, and current-signal assessment.

The existing `Active` decision and power-plan behavior remain unchanged. Confidence operates in shadow mode so that later releases can calibrate thresholds before using them for automation.

## Architecture

`ProcessSnapshotProvider` remains the single system-wide collector. A pure `ProcessGraph` indexes its `ProcessSample` values by PID and traverses parent links with a bounded depth and cycle protection.

`GameDetectionEvidence` represents one explainable signal. `GameConfidenceScorer` caps correlated positive evidence per group, applies all penalties, clamps the result to 0–100, and assigns a confidence level. `HeavyAppDetectionService` remains the compatibility facade: it keeps the existing primary `Reason`, collects every applicable current signal, adds runtime duration and launcher ancestry, scores the evidence, and publishes it in `DetectedHeavyApp`.

## Evidence model

The first increment supports these codes:

| Code | Group | Weight | Source |
|---|---|---:|---|
| `windowsGpuPreference` | `identity` | 25 | Windows high-performance GPU preference for exact path |
| `gameInstallPath` | `provenance` | 30 | Known game installation root marker |
| `gameBinaryLayout` | `identity` | 20 | Common game-engine binary layout |
| `resourceHeuristic` | `runtime` | 5 | Existing working-set fallback |
| `launcherAncestry` | `provenance` | 15 | Known launcher within three parent levels |
| `duration15s` | `runtime` | 4 | Process alive for at least 15 seconds |
| `duration2m` | `runtime` | 3 | Process alive for at least two minutes |

Group caps are identity 50, provenance 35, runtime 25, and history 25. Negative evidence is not capped. Score thresholds are `<40 ignored`, `40–59 unknown`, `60–74 probable`, and `>=75 confirmed`.

## Compatibility and safety

- Existing reason priority remains `windowsGpuPreference`, `gameInstallPath`, `gameBinaryLayout`, then `resourceHeuristic`.
- Existing launcher/helper exclusions run before evidence creation.
- Resource-only sticky behavior remains unchanged.
- No process injection, process-memory reads, manifest parsing, hashing, signature verification, or UI changes are introduced in this increment.
- Graph traversal is bounded to three ancestors during detection and cannot loop on malformed snapshots.
- Missing parent information, including the managed enumeration fallback, simply produces no ancestry evidence.

## Testing

Tests directly exercise real production types:

- `ProcessGraph` returns ancestors in nearest-first order, honors maximum depth, and terminates on cycles;
- `GameConfidenceScorer` caps correlated groups, applies penalties, clamps values, and maps thresholds;
- `HeavyAppDetectionService.AssessProcess` preserves current classification priority while returning all applicable evidence.

The full solution test suite and Release build must pass before commit and push.
