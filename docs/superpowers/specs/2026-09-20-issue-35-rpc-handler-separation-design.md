# Issue 35 RPC Handler Separation Design

## Goal
Separate VoltManager's WebView2 RPC transport from domain command handling so RPC behavior can be tested without starting WPF/WebView2, while preserving the existing JavaScript contract used by the main window and widgets.

The refactor must keep every shipped RPC method name, camelCase reply/event serialization, payload semantics, result shape, and event name compatible with the current UI. It must also make invalid payloads, unknown methods, handler exceptions, concurrent requests, and bridge teardown deterministic.

## Current state
`HostBridge` currently owns all of these responsibilities:

- WebView2 `WebMessageReceived` attachment and teardown;
- JSON request parsing and reply serialization;
- a switch containing 81 RPC methods;
- direct access to services plus the full `App` object;
- domain actions for settings, power, monitoring, updates, widgets, startup apps, diagnostics, and application lifecycle;
- subscriptions that forward native service/application events to JavaScript.

`BridgeRpc` already centralizes the JSON serializer options and the basic success/failure reply shape. That contract remains authoritative during the refactor.

## Design

### Transport and dispatch
`HostBridge` becomes the WebView2 transport adapter. It owns WebView2 attach/detach, reads incoming message JSON, sends exactly one serialized reply for requests that have an id, and forwards native events to JavaScript. It does not contain domain command implementations.

A new `BridgeRpcDispatcher` is independent of WebView2 and accepts a JSON request or a parsed request envelope plus a cancellation/lifetime token. It is responsible for:

- validating the request envelope (`id`, `method`, optional `payload`);
- resolving a registered handler for the method;
- invoking the handler once;
- converting success or failure into the existing `BridgeRpc` reply contract;
- treating unknown methods and invalid payloads as ordinary RPC failures;
- ensuring a request produces at most one reply even when the handler faults;
- allowing different request ids to execute concurrently without shared dispatcher state.

The dispatcher exposes a testable API that does not reference WPF or WebView2. `HostBridge` supplies the returned JSON to the browser transport.

### Handler contract
Handlers implement a small bridge-owned contract such as `IBridgeRpcHandler`, with a method lookup/registration surface and an asynchronous `HandleAsync` operation receiving the cloned `JsonElement` payload and cancellation token. Registration is immutable after construction so concurrent dispatch does not mutate routing state.

Payload extraction/validation helpers live in the bridge layer and provide consistent required-string, required-boolean, integer, enum, object-deserialization, and optional-value handling. They throw argument/validation exceptions with the existing localized messages where the current contract already provides one. Handlers do not serialize replies themselves.

### Domain handlers
The 81 current RPC methods are distributed by responsibility. The exact names below are compatibility requirements.

`SettingsRpcHandler`:

- `getSettings`, `setThemeColor`, `saveSettings`, `setLanguage`;
- `setStartWithWindows`, `setCloseToTray`;
- `setAutoUpdateChecks`, `setSilentAutoUpdates`, `setUpdateChannel`, `snoozeUpdate`, `skipUpdateVersion`;
- `getStandbyAutoCleanSettings`, `setStandbyAutoCleanSettings`;
- `exportSettings`, `importSettings`.

`EnergyRpcHandler`:

- `getBatteryHealth`, `getBatteryPower`, `getBatteryHistory`, `exportBatteryHistory`;
- `checkDefaultPlans`, `restoreDefaultPlans`, `getActivePlan`, `getActivePlanReason`;
- `getPlanHistory`, `clearPlanHistory`, `listPowerPlans`;
- `getKeepAwakeState`, `setKeepAwake`, `setKeepAwakeSafety`;
- `getCpuAutomationState`, `setManualOverride`, `clearManualOverride`;
- `getPowerSourcePlanState`, `setPowerSourcePlanSwitch`;
- `getThermalGuardState`, `setThermalGuardEnabled`, `setThermalGuardSettings`;
- `getIdlePowerGuardState`, `setIdlePowerGuardEnabled`, `setIdlePowerGuardSettings`;
- `getScheduledPowerAction`, `executePowerAction`, `schedulePowerAction`, `cancelScheduledPowerAction`;
- `getPlanTimeouts`, `getPlanParameters`, `setPlanParameter`.

`MonitoringRpcHandler`:

- `getSystemInfo`;
- `getHeavyAppStatus`, `refreshHeavyAppDetection`, `getAppPowerProfileStatus`;
- `pickAppPowerProfileExecutable`, `getTopProcesses`;
- `getMemoryStatus`, `purgeStandbyList`;
- `openLogFolder`, `exportDiagnostics`.

`UpdateRpcHandler`:

- `checkForUpdates`, `getReleaseHistory`, `downloadUpdate`.

`WidgetRpcHandler`:

- `beginWidgetDrag`, `setWidgetTopmost`, `closeWidget`;
- `getWidgetsState`, `setWidgetEnabled`, `setWidgetsMaster`, `setWidgetPinned`;
- `setWidgetSize`, `setWidgetPlacement`, `resetWidgetPosition`.

`ApplicationRpcHandler` contains the remaining application-shell operations:

- `getGamingMode`, `setGamingMode`;
- `getStartupApps`, `pickStartupExecutable`, `addStartupApp`, `setStartupAppEnabled`, `removeStartupApp`;
- `logError`, `openExternal`, `exitApp`, `minimizeToTray`.

This grouping is a responsibility boundary, not permission to change behavior. If implementation reveals one operation cannot be moved without introducing a broad dependency, introduce a narrow callback/interface for that operation rather than passing the complete `App` instance to the handler.

### Dependencies
Each handler receives only the services or narrow bridge-facing contracts needed by its methods. Existing concrete services may be injected directly when they are already independently usable and testable. Operations currently available only through `App` are exposed through small purpose-specific contracts/callbacks owned by the bridge composition layer.

The full `App` object is allowed only in the composition root that constructs bridge dependencies; it is not stored by `BridgeRpcDispatcher` or domain handlers. UI-only interactions such as file dialogs and native widget drag/topmost/close requests are supplied as narrow asynchronous/synchronous delegates so handlers remain instantiable in tests without WPF.

`MainWindow` and `WidgetWindow` remain responsible for wiring window-specific callbacks/events into the bridge. The existing public `HostBridge` events may remain where they provide that boundary, but their invocation is delegated to the appropriate handler callback rather than embedded in the transport switch.

### Event forwarding and lifecycle
Event names and payload shapes remain unchanged. The compatibility baseline includes the events currently emitted by `HostBridge`, `MainWindow`, and `WidgetWindow`, including metrics, theme/language/font changes, power-plan/override/keep-awake changes, update events, resource/guard activity, widget state, and plan-history/conflict notifications.

`HostBridge.Attach()` is idempotent. It attaches `WebMessageReceived` and global native event subscriptions once for the active WebView2 core. `Dispose()` is also idempotent, detaches every subscription installed by `Attach()`, cancels the bridge lifetime token, releases references used by in-flight dispatch, and prevents later replies/events from being posted to a torn-down WebView.

In-flight handlers may finish their own underlying work after teardown if the service cannot cancel it, but the dispatcher/transport must not retain the WebView or publish a late response after the bridge lifetime is cancelled.

### Errors, invalid payloads, and concurrency
All request failures use the existing `{ id, ok: false, error }` JSON shape. Successful requests continue using `{ id, ok: true, result }`. Serialization continues through `BridgeRpc.JsonOpts` so casing and enum behavior do not drift.

Malformed JSON or a request without a usable id cannot be correlated and is logged without a reply. Once a usable id is known, missing/invalid `method`, invalid payload fields, unknown methods, and handler exceptions return one failure reply for that id. A handler must never call the transport directly, which prevents a second reply from racing with the dispatcher's error path.

The dispatcher contains no mutable per-request fields. Each dispatch keeps its id, payload, and completion state local to the call. Concurrent requests therefore cannot overwrite each other's id/result/error state, and replies may complete out of order as already supported by `wwwroot/js/bridge.js`.

## Compatibility inventory
The implementation must preserve all 81 method names listed in the domain-handler sections. Before deleting the current switch, contract tests compare the dispatcher registry against that complete inventory so an RPC cannot disappear silently.

The browser contract remains:

- request: `{ id, method, payload }`;
- success: `{ id, ok: true, result }`;
- failure: `{ id, ok: false, error }`;
- event: `{ event, data }`.

No frontend API rename or `bridge.js` protocol change is part of issue #35.

## Testing strategy
Tests are added before production extraction for each behavior being moved.

`BridgeRpcTests` (or focused bridge test files) cover transport-independent dispatch behavior:

- the complete 81-name registry;
- success and exception reply shapes;
- unknown method handling;
- malformed/missing required payload fields;
- one-reply semantics after handler failure;
- concurrent requests with different ids and out-of-order completion;
- cancellation/teardown suppressing late transport publication.

Each domain handler has contract tests that instantiate it without WPF/WebView2, inject fakes/delegates for its narrow dependencies, invoke representative read/write/error paths, and assert serialized result compatibility where casing/shape is externally observable. Contract coverage must include every extracted RPC method through either a direct handler test or a parameterized registry/dispatch test with a meaningful payload fixture.

Lifecycle tests exercise the extracted subscription/lifetime logic without a real browser where possible, proving repeated `Attach()` does not duplicate subscriptions, repeated `Dispose()` is safe, all registered subscriptions are removed, and pending dispatch cannot publish after teardown.

The existing Node bridge timeout/order tests remain green. No change to `bridge.js` is expected unless a failing compatibility test demonstrates that a transport-side adjustment is required.

## Implementation boundaries
This issue does not redesign domain services, alter settings storage, change power-management behavior, rename frontend APIs, or restructure general `App` orchestration beyond the narrow contracts required to remove `App` from handlers. Unrelated refactoring is excluded.

The extraction should be incremental: establish dispatcher/handler tests first, move one responsibility group at a time while keeping the full suite green, then reduce `HostBridge` to transport/lifecycle composition after all methods are registered through handlers.

## Completion evidence
Issue #35 is complete only when all of the following are demonstrated from the current `Dev` state:

- `HostBridge` no longer contains domain RPC implementations or the 81-case dispatch switch;
- settings, energy, monitoring, update, widget, and application handlers can be instantiated and tested without WPF/WebView2;
- handlers/dispatcher do not store the full `App` object;
- all 81 RPC names and existing reply/event serialization contracts are covered by compatibility tests;
- invalid payloads, unknown methods, handler exceptions, concurrent requests, and teardown behavior have executable regression tests;
- repeated attach/dispose does not duplicate or leak subscriptions, and late replies/events are suppressed after disposal;
- the existing JavaScript bridge tests and full .NET test suite pass;
- the application builds successfully in the repository's normal release configuration.
