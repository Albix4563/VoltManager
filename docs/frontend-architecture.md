# Frontend architecture

VoltManager keeps the WebView2 UI framework-free. The frontend is split into small route-owned modules coordinated by `js/view-lifecycle.js`; legacy DOM ids remain contractual because the C# host, tests, and older navigation paths still address them.

## Load order and dependencies

`index.html` loads shared CSS and `css/power-features.css` first, then the base JavaScript services. `i18n.js` and `view-lifecycle.js` are available before route consumers such as `dashboard.js` and `settings.js`. `settings.js` exposes the small `VoltSettingsCore` helper surface before `settings-preferences-tools.js` registers its lifecycle owner. Power feature factory files (`power-app-profiles.js`, `power-detection.js`, `power-keep-awake.js`, `power-protections.js`) are loaded before `power.js`, which supplies their shared settings/save helpers and instantiates them. `power-history.js` is independent and owns its history bridge subscription.

`advanced.js` remains deferred by `app.js` through `deferredPowerScripts`; opening power plans, automations, system tools, or settings triggers the shared lazy loader. Energy Tips and Guided Tour keep their separate first-use loaders (`tips.feature.js` and `tour.feature.js`). A failed deferred script can be retried and concurrent requests share the same in-flight load.

## View lifecycle contract

Each route-owned module may implement `matches(route)`, `init()`, `activate(route)`, `deactivate(route)`, and `dispose()`. `init` runs at most once before first activation. A transition to the same effective route does not activate a module twice. When ownership changes, the old module is deactivated before the new owner activates. `dispose` releases listeners, timers, subscriptions, and pending local timers and runs once. The registry is disposed on WebView unload.

Modules should attach long-lived listeners in `init` only when they can also remove them in `dispose`. Polling belongs in `activate`/`deactivate`; repeated navigation must never create parallel intervals or duplicate bridge requests. Visibility and resource-profile events may pause/resume an already active owner, but they do not decide route ownership.

## State ownership and route mapping

| Module | Owned state/resources | Legacy route | Reorganized route |
| --- | --- | --- | --- |
| `power-app-profiles.js` | application profiles and profile bridge state | `power` / profiles | `automations` / profiles |
| `power-detection.js` | heavy/game detection and rules | `power` / detection | `automations` / gaming |
| `power-keep-awake.js` | keep-awake state/listeners | `power` / keep-awake | `power-plans` / keep-awake |
| `power-protections.js` | thermal and idle protections | power protection panels | `automations` / protections |
| `power-history.js` | plan-history snapshot, revision, filters, bridge subscription | `power` / history | `power-plans` / history |
| `advanced.js` | timeout editor, advanced parameters, RAM cleaner polling | `power` / advanced or ram | `power-plans` / source or advanced; `system-tools` / memory |
| `dashboard.js` | process polling and battery flow/history polling | `home` | `monitoring` / processes or battery |
| `settings-preferences-tools.js` | autostart/tray controls, settings import/export, diagnostics/log actions | `settings` | `settings` subviews |

The reorganized router remains responsible for visual view/subview state, hash restoration, search targets, ARIA tab state, and compatibility `viewchange` events. It no longer simulates legacy Advanced/RAM clicks or centrally reparents the extracted power feature panels. Feature modules select their reorganized mount first and retain the old mount id as a compatibility fallback.

## DOM and navigation rules

Contractual ids must remain unique. A feature that owns dynamically mounted UI owns any necessary move to its preferred mount; the central router should not repeatedly move feature DOM. Search/navigation may activate a main view and then a persisted subview; lifecycle transitions are idempotent so restoring the same route does not duplicate work. New subviews must preserve keyboard tab semantics (`role=tab`, `aria-selected`, `tabindex`) and responsive behavior.

## CSS ownership

Shared design/motion tokens live in the existing theme and motion sheets. Extracted power feature, accordion, Keep Awake, detection/profile, protection, and history styles live in `css/power-features.css`; feature JavaScript does not inject that stylesheet at runtime. `ui-reorganization.css` owns the reorganized shell/layout, while `motion.css` owns reduced-motion overrides and common timings. New feature CSS should consume existing `--vm-*` tokens instead of defining competing theme values.

`advanced.js` keeps its lazy feature-specific style block together with its deferred implementation so Advanced/RAM remains fully lazy; those rules still consume the shared theme/motion tokens. If that feature is made eager later, its styles should move to a dedicated sheet instead of another global runtime injector.

## Verification expectations

Changes to routing or lifecycle should exercise repeated route transitions, listener/timer cleanup, legacy and reorganized navigation, search/subview restoration, keyboard semantics, responsive CSS, themes and reduced motion. Widget lifecycle, WebView2 resource behavior, lazy loading, and resource-pressure tests are regression gates. The desktop executable is not required for these synthetic checks; host behavior is exercised through the existing bridge/.NET tests.