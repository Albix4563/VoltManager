# Issue 36 Application Lifecycle and Window Orchestration Design

## Goal

Resolve issue #36 by separating VoltManager's application orchestration, power-request arbitration, update scheduling, and dashboard tray/WebView2 lifecycle into explicit coordinators with clear ownership and idempotent lifecycle semantics.

The refactor must preserve externally observable behavior: normal startup and close-to-tray, minimized startup without eager Chromium creation when no visible WebView surface requires it, tray hide/reopen/suspend/resume, renderer recovery, protected-workload update deferral, manual override and automation precedence, graceful uninstall shutdown, bounded fatal shutdown, and the repository resource-validation guarantees.

This is an ownership and lifecycle refactor. It does not redesign power policies, update UX, widget UX, RPC contracts, resource-pressure thresholds, or persistence formats.

## Current state

App is simultaneously the WPF application host, composition root, service locator, power-policy orchestrator, owner of timers/listeners, single-instance endpoint, remote-command endpoint, and shutdown coordinator.

StartupCore constructs and starts the service graph directly. App also owns monitor sampling, plan polling, battery history, SystemEvents.PowerModeChanged, settings/monitor subscriptions, manual overrides, the energy arbitration chain, deferred-update state, remote commands, and normal cleanup.

App.Reliability maintains a second service cleanup sequence for fatal shutdown. This duplicates ownership knowledge from ExitApp, so the two teardown paths can drift.

MainWindow correctly owns UI adaptation, but also owns non-visual lifecycle policy: the periodic update timer, update overlap/defer state, tray park timer, WebView suspend generation state, renderer retry/recovery state, and protected-workload/update coordination.

WidgetManager and WidgetWindow still consume broad App access, leaving ownership and test boundaries less explicit after issue #35 removed App from RPC handlers.

The current power arbitration order is authoritative:

1. power-source policy;
2. thermal guard;
3. idle power guard;
4. app power profile;
5. heavy-app/protected-workload policy;
6. CPU automation only when no higher-priority policy handled the sample.

Manual override behavior and PowerPlanGuardService expectations remain authoritative.

## Lifecycle rules

Every timer, listener, wait handle, cancellation source, and long-lived callback has exactly one owner.

Extracted coordinators use the same lifecycle contract:

- Start while already started is a no-op.
- Stop while not started is a no-op.
- Stop releases every resource owned by that start epoch.
- Start after Stop creates a fresh epoch with fresh cancellation/timer/registration state.
- Dispose invokes the stop path once, is idempotent, and is terminal.
- Start after Dispose throws ObjectDisposedException.
- Late async work may finish internally if the underlying API is not cancellable, but it cannot publish through a stopped/disposed coordinator.

WPF windows remain responsible for concrete UI operations. Coordinators own state and decisions, and interact with windows through narrow interfaces/delegates that can be faked in tests.

App.WebViewEnvironment remains lazy. Core composition, power orchestration, and update scheduling must not evaluate it. On minimized startup with no enabled widget needing WebView, Chromium remains uncreated.

## Application service graph

Introduce AppServiceGraph (or an equivalent name consistent with implementation) as the explicit composition object created by App.

Construction order is explicit:

1. settings, localization, and theme;
2. power and awake services;
3. deferred hardware access and monitor;
4. update, startup, and automation services;
5. heavy-app detection and fullscreen coverage;
6. app-profile, source-plan, thermal, and idle guards;
7. standby cleaner, power-flow, and battery history;
8. scheduled power actions and widget manager.

Constructors create dependencies only. Timers/listeners start through lifecycle methods.

Existing App service properties may temporarily forward to the graph for compatibility. New coordinators receive the graph or focused dependencies directly rather than reaching through App.

## ApplicationLifecycleCoordinator

ApplicationLifecycleCoordinator owns application-level runtime resources:

- Monitor.MetricsUpdated subscription that feeds power arbitration;
- Settings.SettingsChanged subscription used to refresh sampling demand;
- SystemEvents.PowerModeChanged;
- monitor start/stop coordination;
- delayed starts for heavy-app detection, app profiles, fullscreen coverage, and standby cleanup;
- plan-poll timer;
- battery-history timer;
- scheduled-power-action service start/stop;
- remote-command subscription/start/stop.

It receives narrow callbacks for host-only actions such as handling a remote command or hardware resume invalidation.

There is one authoritative normal stop sequence. App.ExitApp asks this coordinator to stop, releases host-owned resources, then calls WPF Shutdown.

Fatal shutdown reuses the same ownership path instead of maintaining a second service inventory. App.Reliability keeps crash diagnostics and the hard shutdown deadline, invoking a bounded stop mode over the same owned resources.

The uninstall signal remains a host concern and routes into the same ExitApp/lifecycle stop path.

## PowerRequestCoordinator

PowerRequestCoordinator owns the mutable energy state and arbitration currently embedded in App.

Focused dependencies include SettingsService, PowerPlanService, AutomationEngine, PowerPlanGuardService, PowerSourcePlanService, ThermalGuardService, IdlePowerGuardService, AppPowerProfileService, HeavyAppDetectionService, PowerAwakeService, and protected-workload/resource-pressure state where needed.

It owns:

- automation overlap protection;
- active-plan and active-reason state;
- manual override transitions;
- app-profile/heavy-app plan-session state;
- app-profile keep-awake requests;
- manual override expiration;
- plan reassertion/conflict handling;
- per-metrics arbitration.

The precedence is encoded and tested as one ordered contract:

PowerSource -> Thermal -> Idle -> AppProfile -> HeavyApp -> CpuAutomation.

Existing explicit manual/user actions remain the only path allowed to bypass automatic arbitration.

The coordinator publishes the events currently surfaced by App: ActivePlanChanged, ManualOverrideChanged, CpuAutomationStateChanged, ActivePlanReasonChanged, and conflict notifications. App may temporarily forward these for compatibility, but it is no longer their state owner.

SetManualOverride, SetAutomaticMode, and ClearManualOverride move into the coordinator without changing settings writes, automation resets, guard expectations, plan-history context, or event semantics.

## UpdateCoordinator

UpdateCoordinator owns update scheduling and update-work state now split between MainWindow and App.

It owns:

- automatic-update timer;
- automatic-check in-flight guard;
- lifecycle cancellation;
- deferred-check state for protected workloads;
- deferred installer URL/state;
- non-visual snooze/skip scheduling policy;
- resumption of deferred work when protection clears.

It depends on UpdateService, SettingsService, a protected-workload query/event source, and narrow shell callbacks/events.

It does not display WPF dialogs or manipulate window controls. It emits decisions such as update available, prompt required, or install ready. MainWindow remains responsible for UpdatePromptWindow and bridge-visible UI behavior.

Installer launch remains a shell operation. The final protected-workload check before installation is preserved so a workload starting during download still blocks restart.

Repeated Start cannot create duplicate update timers or workload listeners. Stop prevents late check results from publishing into a closed window.

## WebViewTrayCoordinator

WebViewTrayCoordinator owns dashboard tray/WebView2 lifecycle policy without owning WPF controls.

Its state covers visible, hidden/suspending, tray parked, renderer/browser recovery, and stopping/disposed.

It owns:

- tray teardown timer;
- suspend in-flight guard and generation;
- renderer/browser retry count;
- recovery in-flight guard;
- lifecycle cancellation;
- lifecycle attachment state for the WebView callbacks assigned to it.

MainWindow supplies a narrow dashboard-surface adapter for hide/show/taskbar/activate, EnsureWebView, visibility, suspend/resume, app-document/about:blank navigation, browser-core recreation/rewiring, and publishing fresh state after resume.

Required invariants:

- hide-to-tray marks the dashboard non-visible and requests suspension immediately;
- delayed tray teardown blanks only while still hidden and not exiting;
- reopen cancels teardown before ensuring/resuming the WebView;
- reopen wins over an older in-flight suspend;
- renderer failures retain the current bounded reload retry behavior;
- browser-process exit performs at most one recovery at a time;
- recovery does not stack app-level listeners;
- stop/dispose prevents queued teardown/recovery work from touching a closed window.

MainWindow still owns close-to-tray UX decisions and requests lifecycle transitions from the coordinator.

## Widget dependency narrowing

WidgetManager no longer stores the full App reference.

It receives settings, localization, theme, WebView environment factory, hardware-sampling-demand refresh callback, and a narrow widget-window factory/runtime context.

WidgetWindow receives only the services/event sources required by widget rendering. It must not gain a replacement general-purpose service locator.

Widget placement, bridge behavior, rendering, and process-sharing policy remain unchanged.

## Startup sequence

The resulting startup order is explicit:

1. initialize logging/reliability handlers;
2. acquire/check single-instance host resources;
3. call base WPF startup;
4. construct AppServiceGraph;
5. construct power/update/application lifecycle coordinators;
6. initialize manual-override and guard expectation state;
7. start ApplicationLifecycleCoordinator;
8. construct MainWindow and its WebViewTrayCoordinator;
9. show MainWindow only when startup is not minimized;
10. show enabled widgets best-effort;
11. process startup command and deferred jump-list/autostart work.

No earlier step may force WebViewEnvironment.

Startup failure retains the current localized failure reporting and AppExitCodes.StartupFailure behavior.

## Shutdown sequence

Normal shutdown has one authoritative sequence:

1. atomically enter stopping state;
2. stop window-level coordinators so no new UI/update/WebView work is queued;
3. stop application timers and detach listeners;
4. stop/dispose services in dependency-safe order;
5. dispose widgets and native wait registrations;
6. release single-instance host resources;
7. call WPF Shutdown.

Each normal cleanup step remains best-effort: failure is logged and later cleanup still executes.

Fatal shutdown uses the same ownership boundaries under the existing bounded deadline. It must not maintain a second independent service list.

Uninstall shutdown routes to the same graceful path. Repeated close/uninstall/fatal requests converge on idempotent stop state.

## Ownership inventory

| Resource | Owner |
| --- | --- |
| plan poll timer | ApplicationLifecycleCoordinator |
| battery history timer | ApplicationLifecycleCoordinator |
| monitor metrics -> power arbitration | ApplicationLifecycleCoordinator |
| settings -> sampling refresh | ApplicationLifecycleCoordinator |
| system power-mode listener | ApplicationLifecycleCoordinator |
| remote-command listener | ApplicationLifecycleCoordinator |
| automatic-update timer/check cancellation | UpdateCoordinator |
| protected-workload -> deferred update resume | UpdateCoordinator |
| tray teardown timer | WebViewTrayCoordinator |
| suspend/recovery operation state | WebViewTrayCoordinator |
| WebView process-failure lifecycle attachment | WebViewTrayCoordinator/dashboard adapter boundary |
| widget settings/theme subscriptions | WidgetManager |
| widget WebView/service subscriptions | each WidgetWindow |
| uninstall native event/wait | App.UninstallLifecycle host boundary |
| single-instance mutex/show event | App host boundary |

Any additional resource discovered during implementation receives one explicit owner before completion.

## Error handling and cancellation

Lifecycle methods do not hold locks while invoking arbitrary service/UI callbacks.

Timer callbacks and event handlers check the active lifecycle epoch/cancellation state. Dispatcher-posted work checks state again when it executes, so queued work cannot revive a stopped coordinator.

Expected disposal races are absorbed by idempotent state. Unexpected exceptions are logged and do not skip later cleanup.

Update and WebView recovery preserve existing user-facing error behavior; background lifecycle failures do not introduce new modal dialogs.

## Testing strategy

Tests are written before each extraction and keep the tree runnable after every coordinator moves.

### Lifecycle contract tests

Verify:

- Start twice attaches/creates each listener/timer once;
- Stop twice detaches/disposes once without throwing;
- Start -> Stop -> Start leaves exactly one fresh active epoch;
- Dispose twice is safe;
- Start after Dispose fails deterministically;
- callbacks from an old epoch cannot publish after stop/restart.

### Power arbitration tests

With focused fakes/delegates, prove:

- power source suppresses all lower-priority policies;
- thermal suppresses idle/profile/heavy/CPU;
- idle suppresses profile/heavy/CPU;
- app profile suppresses heavy/CPU;
- heavy/protected workload suppresses CPU;
- CPU automation runs only when no higher-priority policy handles the sample;
- manual override guard behavior remains unchanged;
- clearing/expiring overrides restores the same automation/event behavior.

### Update lifecycle tests

Verify:

- one timer/listener across repeated starts;
- concurrent ticks collapse to one update check;
- protected workload defers checks/install;
- one deferred resume occurs when protection clears;
- stop suppresses late result publication;
- UpdateSchedulePolicy snooze/skip/channel semantics remain compatible.

### Tray/WebView lifecycle tests

Using a fake dashboard surface, cover:

- visible startup;
- minimized startup without requesting WebView creation;
- hide -> suspend -> delayed blanking;
- reopen before teardown cancels blanking and resumes;
- reopen racing with suspend leaves the surface visible/resumed;
- renderer reload and retry cap;
- browser-process exit single-flight recovery;
- stop/dispose cancellation of teardown/recovery;
- repeated lifecycle cycles do not duplicate process-failure/navigation listeners.

### Shutdown/uninstall tests

Prove:

- normal close and uninstall use the same lifecycle stop path;
- repeated shutdown requests are harmless;
- cleanup failure does not skip later owners;
- fatal shutdown uses shared ownership under BoundedCleanup;
- uninstall event/wait registration is released.

## Resource regression protocol

Unit tests are insufficient for the final issue criterion.

The deterministic resource-validation and resource-pressure tests must remain green.

For the final candidate, run scripts/Run-ResourceValidation.ps1 against a baseline build from the pre-#36 Dev commit and the candidate build across its six scenarios:

- dashboard-active;
- dashboard-inactive;
- tray-no-widgets;
- tray-widget;
- protected-cpu;
- restore.

Use the real protocol defaults: 30-second stabilization, 120-second measurement, five repetitions, plus additional repetitions selected by the script when results are unstable.

The resulting benchmark-summary.json/Markdown decision is authoritative for the no-regression criterion. A shortened deterministic test protocol is useful for regression testing but is not accepted as final benchmark evidence.

## Implementation sequence

1. Add lifecycle primitives/tests and explicit service composition.
2. Extract PowerRequestCoordinator with precedence regression tests.
3. Extract ApplicationLifecycleCoordinator and unify normal/fatal service ownership.
4. Extract UpdateCoordinator.
5. Extract WebViewTrayCoordinator.
6. Narrow WidgetManager/WidgetWindow dependencies.
7. Remove obsolete forwarding/state from App/MainWindow.
8. Run complete tests/build and the real baseline-vs-candidate resource benchmark.

Each extraction must leave relevant tests green before the next responsibility moves.

## Compatibility boundaries

Issue #36 must not change:

- RPC names, event names, payload casing, or reply shapes established by issue #35;
- settings schema unless separately justified and tested;
- power policy thresholds or precedence;
- manual override semantics;
- resource-pressure thresholds/hysteresis;
- WebView renderer command-line strategy;
- update channels, interval, snooze/skip semantics, or prompt text;
- widget types, placement, size, pinning, or persistence;
- installer/uninstaller command-line contracts.

## Completion evidence

Issue #36 is complete only when current Dev evidence proves all of the following:

- service construction and runtime start/stop order are explicit in dedicated composition/lifecycle code;
- normal startup, minimized/tray startup, reopen, renderer/browser recovery, normal close, fatal close, and uninstall shutdown are covered by executable tests or the Windows harness where native behavior requires it;
- extracted coordinators have idempotent Start, Stop, and Dispose semantics;
- repeated lifecycle cycles do not duplicate timers/listeners;
- power arbitration/manual override precedence are executable invariants and unchanged;
- App and windows no longer own state/timers assigned to extracted coordinators;
- WidgetManager no longer retains the full App object;
- minimized startup does not eagerly create Chromium when no enabled widget requires it;
- protected workloads still prevent disruptive update/resource behavior and deferred update work resumes correctly;
- full .NET and JavaScript/Node suites pass;
- the application builds in the repository release configuration;
- the real resource-validation benchmark reports no regression against the pre-#36 Dev baseline.
