# Issue 34 Behavioral Test Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close issue #34 by replacing fragile critical-path source checks with executed behavioral coverage and deterministic test isolation.

**Architecture:** Reuse Node's built-in vm/test runner with a shared lightweight DOM harness; isolate .NET SettingsService paths through a test helper; extend the existing Windows harness with a software-rendered UI smoke mode and wire it into CI.

**Tech Stack:** Node.js node:test + vm, C#/.NET 8 xUnit, WPF WebView2, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-19-issue-34-behavioral-tests-design.md`

## Global Constraints
- No new npm dependency.
- Ordinary tests must not read/write real VoltManager user settings or mutate the machine power plan.
- WebView2 CI smoke must not depend on a physical/specific GPU.
- Retain static assertions only for structural contracts.

## Review Focus
- Duplicate listener installation must be observable through repeated lifecycle events.
- Out-of-order RPC responses must resolve the correct pending request.
- Save debounce must persist the latest state once without leaking timers.
- Navigation/subview keyboard activation must update ARIA/state and emit the expected event.
- WebView2 smoke must fail cleanly if browser navigation or JavaScript execution fails while remaining hardware-independent.

---

### Task 1: Deterministic .NET settings fixture
**Files:** Create `tests/VoltManager.Tests/TestSettings.cs`; modify `PowerPlanServiceTests.cs` and `PlanHistoryServiceTests.cs`.
**Interfaces:** Produces `TestSettings.Create()` returning a SettingsService bound to a unique temp path.
- [ ] Add a test proving the helper path is outside the production AppData path and SettingsService reads/writes only that file.
- [ ] Run the focused test and observe RED before the helper exists.
- [ ] Implement TestSettings and migrate every parameterless SettingsService in the named tests.
- [ ] Run VoltManager.Tests and verify GREEN.

### Task 2: Behavioral UI and bridge regression coverage
**Files:** Create `tests/helpers/dom-harness.mjs`; modify `ui-redesign.test.mjs`, `global-search.test.mjs`, `settings-bootstrap.test.mjs`, `bridge-timeout.test.mjs`, `widget-clock-timer.test.mjs`, and `lazy-feature-loading.test.mjs` as needed.
**Interfaces:** Produces reusable fake DOM/event primitives used by action/state tests.
- [ ] Add failing behavior tests for view/subview navigation, keyboard routing/listener duplication, save debounce, search open/close/navigation, theme application, bridge out-of-order/error handling, visibility cleanup, lazy-load retry/concurrency, and widget listener/timer lifecycle.
- [ ] Run focused Node tests and confirm failures are due to missing harness/coverage hooks.
- [ ] Implement only the reusable test harness or minimal product fixes revealed by those tests.
- [ ] Remove redundant regex assertions covered by behavior tests while retaining true structural-contract checks.
- [ ] Run the full Node suite GREEN.

### Task 3: WebView2 UI smoke mode and CI wiring
**Files:** Modify `tests/VoltManager.WindowsHarness/Program.cs`, `.github/workflows/ci.yml`.
**Interfaces:** Adds harness mode `ui-smoke` producing the existing windows-harness report and using ValidationEnvironment + SwiftShader.
- [ ] Add deterministic harness coverage/guard for ui-smoke configuration and make the focused run fail before implementation.
- [ ] Implement the ui-smoke mode: isolated validation root, suppressed power changes, SwiftShader environment, real WebView2 navigation and DOM/script assertion.
- [ ] Add CI step invoking ui-smoke.
- [ ] Build Release and run deterministic + ui-smoke locally.

### Task 4: Completion audit
**Files:** issue #34 and all changed files.
- [ ] Run full Node tests, full .NET tests, Release build, deterministic harness, and ui-smoke.
- [ ] Search ordinary tests for parameterless SettingsService and direct powercfg mutation paths.
- [ ] Review remaining regex assertions and confirm each protects a structural contract rather than user interaction.
- [ ] Review the full diff against every issue completion criterion.
- [ ] Commit/push to `Dev`, then close issue #34 with verification evidence.
