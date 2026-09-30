# Issue 52: refactor evidence

Issue: https://github.com/Albix4563/VoltManager/issues/52
Original baseline: `d9631b33e50f11222d5f5f4377d05277560444a8` on `Dev`.
Generated logs/payloads remain local under ignored `artifacts/issue52`.

## Current verification rules

The user's updated AGENTS.md (2026-09-30) supersedes the original visual gates:
visual/manual checks belong to the user. The agent must not launch the app for
inspection, capture/compare screenshots, use visual harnesses or inspect images.
Child specifications must repeat these rules. Agent acceptance uses appropriate
builds, existing tests, regression tests and synthetic normal/edge/error scenarios.
Existing behavioral assertions must not be skipped or weakened. Structural tests
may follow deliberately changed file/module ownership while retaining their checks.
Regression tests are permanent project-suite tests committed with the code. The
implementation child writes them, covering changed behavior and its real edge
cases without redundant internal checks. Bug regressions must fail before the fix
and pass after it; refactor coverage is completed before the batch push. Future
changes run and update the relevant regression suites without weakening them.
The incomplete screenshot-runner experiment has been removed from the batch and
preserved only in ignored artifacts. No visual equivalence is claimed.

## Phase order and completion scope

| Phase | Scope | State |
| --- | --- | --- |
| 0 | Characterize current settings/widgets/dashboard/advanced/app JS | Verified and committed as a prerequisite |
| 1 | One tokens file, one layer order, root style controller | Verified |
| 2 | Component ownership, absorb patch styles, inline/CSP removal, stylelint CI | Pending |
| 3 | Shared DOM helpers, safe rendering, one frontend namespace, lifecycle | Pending |
| 4 | Split large JS files using characterization coverage | Pending |
| 5 | Split C# responsibilities and audit blocking/async calls | Pending |
| Final | Two review rounds (second delta only), relevant automatic checks | Pending |

Preserve bridge/API and persisted settings formats. No new product features.
Each verified batch is committed and pushed to Dev without force push.

## Baseline evidence

On original production sources: zero-warning Release solution build; 736 app tests,
60 setup tests, 200 Node tests passed, no skipped tests; all frontend JS passed
`node --check`. Original package and Test-PublishPayload passed. A forced genuine
NuGet restore cleared stale NU1900 warnings; no warning/audit policy was disabled.
Original app/widget CSS contains **170 important declarations**, counted by
occurrences; remote CSS adds two, for **172 across all frontend CSS**. Phase 1
retains both counts while consolidating ownership and policy.
These historical logs are in `artifacts/issue52/baseline-*`.

Visual/benchmark work conducted before the rule change is not an acceptance gate
under the new instructions and will not be repeated. The old graphics benchmark
was synthetic WebGL, not application FPS. No visual regression verdict exists.

## Characterization coverage

Five new suites execute the unchanged actual production scripts using a fixture
helper extending the existing DOM harness:

- Settings hydration, control RPC payloads, local mutations, appearance overrides,
  mount/listener idempotency.
- All twelve widget renderers, appearance events and polling visibility lifecycle.
- Dashboard metrics, battery history, visibility pause and manual-override payloads.
- Advanced panel navigation, mount idempotency and RPC failure recovery.
- App startup, reconnect hydration, routing and idempotent remounting.

Independent verification: 16 characterization tests passed; full Node suite 216
passed, 0 failed/skipped. Widget lifecycle asserts one visible poller, zero hidden
pollers/requests and immediate polling resumption. VM fixtures do not replace the
user's real-app checks. Latest batch logs will be recorded after final verification.

## Required end state (not yet proved)

One declared layer order and one tokens.css for app/widgets/remote; themes change
only tokens; each component selector has one owner; style-controller.js exclusively
resolves theme/effects/motion/performance and writes html data-theme/data-effects/
data-motion. Absorb and remove redesign/polish/effects/ui-reorganization/widget-plan-
override patch sheets; fewer than ten justified important declarations. Remove
static/runtime inline styling and style-src unsafe-inline; no sheet injected outside
layers. Actual stylelint CI plus cross-file selector/token checks.

One dom-utils module for escaping/attributes/safe rendering, with every HTML render
site audited; one frontend namespace/registry and loader-owned load flags. Status
refresh uses shared lifecycle. Split settings/widgets/dashboard/advanced/app after
characterization. Split HeavyAppDetectionService, MainWindow, InstallEngine and App
composition/wiring; inspect the other large classes named by the issue. Audit
SettingsService retries, Result/Wait calls and non-handler async void.

Two gpt-5.6-sol high review rounds covering CSS/JS/C# (second delta only), evaluate
all findings. Final report contains actual automatic results per phase, important
counts, review dispositions and user visual/manual checks; no unsupported screenshot
or performance claims. Final verified commit references Closes #52.

## User checks at completion

1. Panels/modals across themes, reduced motion and effect levels.
2. Widgets and appearance/settings persistence.
3. LAN remote, tray and gaming mode in the real app.

## Preparatory backend audit (read-only)

The existing heavy-app collector/classifier/tracker, WebView coordinators and
setup workflow runner are useful extraction boundaries; phase 5 must complete
their responsibility ownership rather than mechanically split partial files.
The typed AppServiceGraph already exists, but consumers still reach through App.
Confirmed follow-up sites are WidgetWindow.TrySuspendWebView (non-handler async
void), LAN Dispose/Stop completion, and App's bounded dispatcher wait during
process sampling. Settings constructor retries and guarded completed-task Result
reads are bounded synchronous contracts, not automatically deadlock defects.
This is preparation only: no backend implementation or verification is claimed.
The full local evidence is in ignored artifacts/issue52/csharp-map-final.md.

## Verified phase 0 batch

- Release solution build: 0 warnings, 0 errors (`phase0-build.log`).
- Application unit suite: 736 passed, 0 failed/skipped (`phase0-unit.log`).
- Setup unit suite: 60 passed, 0 failed/skipped (`phase0-setup.log`).
- Full Node suite: 216 passed, 0 failed/skipped (`phase0-node.log`).
- Syntax checks: all wwwroot/js JavaScript passed `node --check`.
- No app launch, screenshot, image inspection or visual comparison during this
  verification batch under the updated rules.
- Production sources remain unchanged; CSS important count remains 170.

## Verified phase 1 batch

- `layers.css` declares the order once; `tokens.css` owns design/palette values
  for app, widgets and remote. Transitional legacy sheets use the components
  layer to preserve their existing important-declaration precedence.
- `Volt.style` owns theme/effects/motion/resource policy and root attributes.
  Theme/performance facades and existing consumers delegate to it. Custom palettes
  mutate the declared layered CSSOM rule after validation. Legacy names/attributes
  remain compatibility surfaces until the namespace phase.
- UI reorganization CSS loads statically before theme colors. LAN exposes only
  the two explicit shared stylesheet paths, alongside its existing remote assets.
- Permanent regressions cover all seven complete native palettes and semantic
  aliases, anonymous HTTP asset delivery/security headers/404 boundaries/API auth,
  custom colors, CSSOM/host errors, resource/motion combinations and idempotency.
- Fixed transient host-registration retry: only successful event channels are
  marked bound. Its new regression failed before the fix (7 pass/1 fail) and
  passed after it (8/8), without duplicating successful listeners.
- Latest full Node suite: **224 passed**, 0 failed/skipped
  (`phase1-host-retry-full-node.log`); all **43** frontend JS files pass syntax
  checking. All 16 original characterization cases remain intact.
- Latest application suite: **744 passed**, 0 failed/skipped, through the Release
  test assembly (`phase1-regression-app-suite.log`); setup suite **60 passed**,
  0 failed/skipped (`phase1-finish-setup-tests.log`).
- Latest Release solution build: **0 warnings/errors**
  (`phase1-regression-release-build.log`). `git diff --check` passes.
- `Test-PublishPayload.ps1`, including `build.ps1 -SkipInstaller`, passes
  (`phase1-package.log`). All three new assets match source SHA-256 in both the
  publish folder and a local archive generated with the build's ZipFile method.
- Important declarations remain **170 app/widget, 172 including remote**.
  Patch consolidation, inline/CSP cleanup and stylelint belong to phase 2.
- No GUI app launch, browser, screenshot or image inspection was performed.
  Visual/manual acceptance remains with the user; no visual or FPS claim is made.
