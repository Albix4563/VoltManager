# Issue 34 Behavioral Test Hardening Design

## Goal
Strengthen VoltManager's automated tests so critical UI and bridge behavior is validated through executed actions and observable state, while ordinary tests remain isolated from real user settings, IPC names, GPU requirements, and power-plan mutations.

## Scope
- Keep static assertions only where they protect structural contracts such as required DOM ids, script inclusion, accessibility markup, localization-key coverage, or CSS policy.
- Add behavior coverage for main-view/subview navigation, settings persistence scheduling, global search/modal interaction, lazy loading, theme application, widgets, bridge failures/out-of-order replies, visibility changes, and listener/timer cleanup.
- Replace implicit SettingsService paths in ordinary .NET tests with deterministic temporary paths shared through a test helper.
- Add a focused WebView2 smoke mode that runs with VoltManager's validation root, suppresses power changes, uses SwiftShader, loads a real browser surface, and verifies basic DOM/script execution without hardware-specific GPU assumptions.
- Run the WebView2 smoke in Windows CI.

## Design
JavaScript behavior tests continue using Node's built-in test runner and vm contexts, avoiding a new npm dependency. A small reusable DOM/event helper supplies classList, attributes, listeners, focus, query registration, and dispatched-event capture so tests assert state transitions rather than source spelling. Existing source-string tests remain only for contracts that cannot be exercised cheaply in Node.

.NET tests use a shared TestSettings factory that always constructs SettingsService with a unique temp JSON path. Power-plan tests continue injecting native-read and command delegates, so no real powercfg mutation is possible.

The Windows harness gains a ui-smoke mode. It initializes validation isolation before creating WebView2, forces SwiftShader, opens an isolated WebView surface, executes a compact DOM/state probe, and reports pass/fail through the existing HarnessReport format. CI runs this mode after deterministic validation.

## Completion evidence
- Node suite contains action/state tests for navigation, subviews, save debounce, search dialog/navigation, lazy loading, theme application, widgets, bridge ordering/error paths, visibility, and cleanup.
- No ordinary .NET test constructs SettingsService without an explicit isolated path.
- Power-plan tests use injected command delegates; Windows harness sets validation power suppression.
- WebView2 smoke runs in CI with software rendering.
- Full Node and .NET suites, release build, deterministic harness, and WebView2 smoke pass.
