# Issue 35 RPC Handler Separation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the 81-method WebView2 RPC switch out of `HostBridge` into testable domain handlers behind a transport-independent dispatcher while preserving every existing browser contract and lifecycle behavior.

**Architecture:** `HostBridge` remains the WebView2 transport/lifecycle adapter. A new `BridgeRpcDispatcher` owns envelope parsing, immutable method routing, one-reply error policy, and per-request concurrency state. Six handler classes own settings, energy, monitoring, updates, widgets, and application-shell operations; they receive concrete services where those services are already independently usable and narrow delegates/interfaces for behavior currently reachable only through `App` or WPF.

**Tech Stack:** C# 12 / .NET 8 WPF, System.Text.Json, Microsoft.Web.WebView2, xUnit, Node.js built-in test runner.

**Spec:** `docs/superpowers/specs/2026-09-20-issue-35-rpc-handler-separation-design.md`

## Global Constraints

- Preserve all 81 current RPC method names exactly.
- Preserve request `{ id, method, payload }`, success `{ id, ok: true, result }`, failure `{ id, ok: false, error }`, and event `{ event, data }` shapes.
- Continue serializing RPC replies/events with `BridgeRpc.JsonOpts` so camelCase and enum casing remain unchanged.
- No frontend RPC rename and no protocol redesign in `wwwroot/js/bridge.js`.
- Domain handlers and `BridgeRpcDispatcher` must not store the full `App` object and must be constructible without WPF/WebView2.
- `HostBridge.Attach()` and `Dispose()` remain idempotent; disposal cancels bridge lifetime and suppresses late replies/events.
- Do not redesign domain services or settings/power behavior beyond narrow adapters needed for handler extraction.
- Follow TDD for every production change and keep commits focused by task.
- Work on `Dev` and push completed working batches to `origin/Dev` per local repository instructions.

## Review Focus

- A request with a valid `id` but missing/non-string `method` must produce exactly one failure reply for that id.
- A request with malformed JSON or no usable `id` must be logged and produce no uncorrelatable reply.
- Missing/wrong-type payload fields must fail through the same dispatcher path and must never emit both a handler reply and a dispatcher reply.
- Two concurrent requests that complete out of order must preserve their own ids/results with no shared mutable request state.
- Disposing the bridge while a request is in flight must suppress its late publication and must detach each native subscription exactly once.

---

### Task 1: Transport-independent dispatcher and payload validation

**Files:**
- Create: `src/VoltManager/Bridge/Rpc/IBridgeRpcHandler.cs`
- Create: `src/VoltManager/Bridge/Rpc/BridgeRpcDispatcher.cs`
- Create: `src/VoltManager/Bridge/Rpc/BridgePayload.cs`
- Create: `tests/VoltManager.Tests/BridgeRpcDispatcherTests.cs`
- Modify: `src/VoltManager/Bridge/BridgeRpc.cs`
- Modify: `tests/VoltManager.Tests/BridgeRpcTests.cs`

**Interfaces:**
- Produces: `IBridgeRpcHandler.Methods : IReadOnlyCollection<string>`.
- Produces: `IBridgeRpcHandler.HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken) : Task<object?>`.
- Produces: `BridgeRpcDispatcher.DispatchAsync(string json, CancellationToken cancellationToken) : Task<BridgeRpcDispatchResult>`.
- Produces: `BridgeRpcDispatchResult(string? Id, string? ReplyJson, string? LogMessage, Exception? Exception)` with `ShouldReply => ReplyJson != null`.
- Produces: `BridgePayload.RequiredString`, `RequiredBoolean`, `RequiredInt32`, `OptionalInt32`, and `Deserialize<T>` helpers.
- Produces: `BridgeRpc.FormatEvent(string name, object data)` so event serialization uses the same options as replies.
- Consumes: existing `BridgeRpc.FormatSuccess`, `BridgeRpc.FormatFailure`, `BridgeRpc.OnDispatchException`, and `BridgeRpc.JsonOpts`.

- [ ] **Step 1: Add failing dispatcher contract tests**

Create tests with a fake handler that exposes named methods and records invocation count. Cover success, thrown handler, unknown method, malformed JSON, valid id with missing method, wrong payload type, duplicate handler registration, and cancellation. The core shape is:

```csharp
private sealed class FakeHandler : IBridgeRpcHandler
{
    private readonly Func<string, JsonElement, CancellationToken, Task<object?>> _handle;
    public IReadOnlyCollection<string> Methods { get; }

    public FakeHandler(IEnumerable<string> methods,
        Func<string, JsonElement, CancellationToken, Task<object?>> handle)
    {
        Methods = methods.ToArray();
        _handle = handle;
    }

    public Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
        => _handle(method, payload, cancellationToken);
}

[Fact]
public async Task DispatchAsync_handler_exception_returns_one_failure_for_same_id()
{
    var dispatcher = new BridgeRpcDispatcher(
        [new FakeHandler(["boom"], (_, _, _) => throw new InvalidOperationException("failure"))],
        method => $"unknown: {method}");

    BridgeRpcDispatchResult result = await dispatcher.DispatchAsync(
        """{"id":"r1","method":"boom","payload":{}}""", CancellationToken.None);

    Assert.Equal("r1", result.Id);
    Assert.NotNull(result.ReplyJson);
    using var doc = JsonDocument.Parse(result.ReplyJson!);
    Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    Assert.Equal("failure", doc.RootElement.GetProperty("error").GetString());
}
```

Add a concurrency test where `slow` awaits a `TaskCompletionSource`, `fast` completes immediately, and both returned JSON payloads keep their original ids.

Extend `BridgeRpcTests` with an event-format regression that asserts `BridgeRpc.FormatEvent("activePlanChanged", new { planId = "x" })` serializes exactly the top-level `event`/`data` shape and camelCase data property expected by the JavaScript bridge.

- [ ] **Step 2: Run dispatcher tests and verify RED**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~BridgeRpcDispatcherTests -v:minimal`

Expected: FAIL because `IBridgeRpcHandler`, `BridgeRpcDispatcher`, `BridgeRpcDispatchResult`, and `BridgePayload` do not exist.

- [ ] **Step 3: Implement immutable routing and centralized envelope/error handling**

Implement the handler interface:

```csharp
namespace VoltManager.Bridge.Rpc;

internal interface IBridgeRpcHandler
{
    IReadOnlyCollection<string> Methods { get; }
    Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken);
}
```

Build a case-sensitive immutable dictionary in the dispatcher constructor. Reject empty names and duplicate registrations with `ArgumentException`. `DispatchAsync` must keep `id` local to the call, clone the payload, and return one result object instead of posting anything itself.

Envelope algorithm:

```csharp
string? id = null;
try
{
    using JsonDocument doc = JsonDocument.Parse(json);
    JsonElement root = doc.RootElement;
    id = root.GetProperty("id").GetString();
    if (string.IsNullOrWhiteSpace(id))
        throw new ArgumentException("Missing RPC id");

    string method = root.GetProperty("method").GetString()
        ?? throw new ArgumentException("Missing RPC method");
    JsonElement payload = root.TryGetProperty("payload", out JsonElement raw)
        ? raw.Clone()
        : default;

    if (!_handlers.TryGetValue(method, out IBridgeRpcHandler? handler))
        throw new ArgumentException(_unknownMethodMessage(method));

    object? result = await handler.HandleAsync(method, payload, cancellationToken);
    cancellationToken.ThrowIfCancellationRequested();
    return BridgeRpcDispatchResult.Success(id, BridgeRpc.FormatSuccess(id, result));
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    return BridgeRpcDispatchResult.Cancelled(id);
}
catch (Exception ex)
{
    BridgeRpc.DispatchFailure failure = BridgeRpc.OnDispatchException(id, ex);
    return failure.ShouldReply
        ? BridgeRpcDispatchResult.Failure(failure.Id!, BridgeRpc.FormatFailure(failure.Id!, failure.ErrorMessage), failure.LogMessage, ex)
        : BridgeRpcDispatchResult.UncorrelatedFailure(failure.LogMessage, ex);
}
```

`BridgePayload` must validate `ValueKind == Object` before reading properties and throw `ArgumentException` with the caller-supplied message for required values. Do not catch handler exceptions in handlers.

Add `BridgeRpc.FormatEvent` as:

```csharp
public static string FormatEvent(string name, object data)
    => JsonSerializer.Serialize(new { @event = name, data }, JsonOpts);
```

- [ ] **Step 4: Run dispatcher tests and verify GREEN**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter "FullyQualifiedName~BridgeRpcDispatcherTests|FullyQualifiedName~BridgeRpcTests" -v:minimal`

Expected: PASS with zero failures.

- [ ] **Step 5: Commit dispatcher foundation**

```powershell
git add src/VoltManager/Bridge/BridgeRpc.cs src/VoltManager/Bridge/Rpc tests/VoltManager.Tests/BridgeRpcDispatcherTests.cs tests/VoltManager.Tests/BridgeRpcTests.cs
git commit -m "refactor: add rpc dispatcher foundation"
git push origin Dev
```

### Task 2: Settings handler and UI file-dialog boundary

**Files:**
- Create: `src/VoltManager/Bridge/Rpc/IBridgeFileDialogService.cs`
- Create: `src/VoltManager/Bridge/Handlers/SettingsRpcHandler.cs`
- Create: `tests/VoltManager.Tests/SettingsRpcHandlerTests.cs`
- Modify: `src/VoltManager/Bridge/HostBridge.cs` only to expose/reuse existing static settings-preservation helpers if required by the handler during extraction; do not rewire dispatch yet.

**Interfaces:**
- Consumes: `IBridgeRpcHandler`, `BridgePayload`, `BridgeRpc.JsonOpts` from Task 1.
- Produces: `IBridgeFileDialogService.SaveFileAsync(BridgeSaveFileRequest request, CancellationToken) : Task<string?>`.
- Produces: `IBridgeFileDialogService.OpenFileAsync(BridgeOpenFileRequest request, CancellationToken) : Task<string?>`.
- Produces: `SettingsRpcActions` containing `Func<object> GetWebTheme`, `Func<object> GetWebThemeCatalog`, `Action RefreshAppPowerProfiles`, `Action RefreshHeavyAppDetection`, `Action RebuildJumpList`, `Func<bool> IsStartWithWindowsEnabled`, `Func<bool, bool> SetStartWithWindows`, and `Func<DateTime> UtcNow`.
- Produces: `SettingsRpcHandler` registering exactly 15 methods: `getSettings`, `setThemeColor`, `saveSettings`, `setLanguage`, `setStartWithWindows`, `setCloseToTray`, `setAutoUpdateChecks`, `setSilentAutoUpdates`, `setUpdateChannel`, `snoozeUpdate`, `skipUpdateVersion`, `getStandbyAutoCleanSettings`, `setStandbyAutoCleanSettings`, `exportSettings`, `importSettings`.

- [ ] **Step 1: Write failing settings handler tests**

Use `TestSettings.CreateService()` for an isolated `SettingsService`. Inject fake theme objects, fake refresh counters, fake autostart delegates, a fixed UTC clock, and a fake file-dialog service returning temp paths. Assert:

```csharp
[Theory]
[InlineData("getSettings")]
[InlineData("setThemeColor")]
[InlineData("saveSettings")]
[InlineData("setLanguage")]
[InlineData("setStartWithWindows")]
[InlineData("setCloseToTray")]
[InlineData("setAutoUpdateChecks")]
[InlineData("setSilentAutoUpdates")]
[InlineData("setUpdateChannel")]
[InlineData("snoozeUpdate")]
[InlineData("skipUpdateVersion")]
[InlineData("getStandbyAutoCleanSettings")]
[InlineData("setStandbyAutoCleanSettings")]
[InlineData("exportSettings")]
[InlineData("importSettings")]
public void Methods_contains_settings_contract(string method)
    => Assert.Contains(method, _handler.Methods);
```

Add meaningful behavior tests for `getSettings`, `saveSettings` preserving runtime-owned settings and invoking both refresh callbacks, invalid `skipUpdateVersion`, cancelled export/import, and successful export/import using temp files.

- [ ] **Step 2: Run settings tests and verify RED**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~SettingsRpcHandlerTests -v:minimal`

Expected: FAIL because settings handler and file-dialog contracts do not exist.

- [ ] **Step 3: Extract settings methods without changing result shapes**

Move the current switch bodies verbatim in behavior, replacing `_app.Theme`, refresh calls, jump-list dispatch, `StartupService` calls, `DateTime.UtcNow`, and WebView file dialogs with `SettingsRpcActions` and `IBridgeFileDialogService`. Keep `BackupJsonOpts` equivalent to current PascalCase/enum backup format. Move `PreserveRuntimeOwnedSettings` and `SaveStandbyAutoCleanSettings` to internal static helpers on `SettingsRpcHandler` so existing tests can target the new owner.

For `HandleAsync`, use an explicit switch limited to the handler's own 15 registered names; its `default` throws `ArgumentException` because the dispatcher should never route an unregistered name to it.

```csharp
public IReadOnlyCollection<string> Methods { get; } =
[
    "getSettings", "setThemeColor", "saveSettings", "setLanguage",
    "setStartWithWindows", "setCloseToTray", "setAutoUpdateChecks",
    "setSilentAutoUpdates", "setUpdateChannel", "snoozeUpdate",
    "skipUpdateVersion", "getStandbyAutoCleanSettings",
    "setStandbyAutoCleanSettings", "exportSettings", "importSettings",
];

public Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken ct)
    => method switch
    {
        "getSettings" => Task.FromResult<object?>(GetSettings()),
        "setThemeColor" => Task.FromResult<object?>(SetThemeColor(payload)),
        "saveSettings" => Task.FromResult<object?>(SaveSettings(payload)),
        "setLanguage" => Task.FromResult<object?>(SetLanguage(payload)),
        "setStartWithWindows" => SetStartWithWindowsAsync(payload, ct),
        "setCloseToTray" => Task.FromResult<object?>(SetCloseToTray(payload)),
        "setAutoUpdateChecks" => Task.FromResult<object?>(SetAutoUpdateChecks(payload)),
        "setSilentAutoUpdates" => Task.FromResult<object?>(SetSilentAutoUpdates(payload)),
        "setUpdateChannel" => Task.FromResult<object?>(SetUpdateChannel(payload)),
        "snoozeUpdate" => Task.FromResult<object?>(SnoozeUpdate(payload)),
        "skipUpdateVersion" => Task.FromResult<object?>(SkipUpdateVersion(payload)),
        "getStandbyAutoCleanSettings" => Task.FromResult<object?>(_settings.Current.StandbyAutoCleaner),
        "setStandbyAutoCleanSettings" => Task.FromResult<object?>(SetStandbyAutoCleanSettings(payload)),
        "exportSettings" => ExportSettingsAsync(ct),
        "importSettings" => ImportSettingsAsync(ct),
        _ => throw new ArgumentException($"Handler cannot process RPC method '{method}'."),
    };
```

Each named private method contains the corresponding current `HostBridge.DispatchAsync` case body, replacing only the dependencies called out above. This preserves the existing payload/result shape while moving ownership to the handler.

- [ ] **Step 4: Run settings tests and existing settings contracts**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsRpcHandlerTests|FullyQualifiedName~SettingsServiceContractTests|FullyQualifiedName~TestSettings" -v:minimal`

Expected: PASS with zero failures.

- [ ] **Step 5: Commit settings extraction**

```powershell
git add src/VoltManager/Bridge/Rpc/IBridgeFileDialogService.cs src/VoltManager/Bridge/Handlers/SettingsRpcHandler.cs src/VoltManager/Bridge/HostBridge.cs tests/VoltManager.Tests/SettingsRpcHandlerTests.cs
git commit -m "refactor: extract settings rpc handler"
git push origin Dev
```

### Task 3: Widget handler with narrow callbacks

**Files:**
- Create: `src/VoltManager/Bridge/Handlers/WidgetRpcHandler.cs`
- Create: `tests/VoltManager.Tests/WidgetRpcHandlerTests.cs`

**Interfaces:**
- Consumes: `IBridgeRpcHandler`, `BridgePayload`.
- Produces: `WidgetRpcActions` with `Func<object> GetState`, `Func<string,bool,object> SetEnabled`, `Func<bool,object> SetMasterEnabled`, `Func<string,bool,object> SetPinned`, `Func<string,string,object> SetSize`, `Func<string,string,string,object> SetPlacement`, `Func<string,object> ResetPosition`, `Action BeginDrag`, `Action<bool> SetTopmost`, `Action Close`.
- Produces: `WidgetRpcHandler` registering exactly 10 methods: `beginWidgetDrag`, `setWidgetTopmost`, `closeWidget`, `getWidgetsState`, `setWidgetEnabled`, `setWidgetsMaster`, `setWidgetPinned`, `setWidgetSize`, `setWidgetPlacement`, `resetWidgetPosition`.

- [ ] **Step 1: Write failing widget tests**

Test all 10 names via a theory and exercise each callback family. Include invalid/missing `type`, `enabled`, `topmost`, `size`, `monitorId`, and `anchor` payloads. Verify callbacks are not invoked when validation fails.

- [ ] **Step 2: Run widget tests and verify RED**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~WidgetRpcHandlerTests -v:minimal`

Expected: FAIL because `WidgetRpcHandler` does not exist.

- [ ] **Step 3: Implement widget handler using only `WidgetRpcActions`**

Return the same anonymous-object shapes currently returned by `HostBridge`: `{ success = true }` for drag/close, `{ success = true, topmost }` for topmost, and delegate-returned widget snapshots for state mutations. Do not reference `App`, `WidgetManager`, `Window`, or WebView2 in the handler file.

```csharp
public Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken ct)
{
    object? result = method switch
    {
        "beginWidgetDrag" => Invoke(_actions.BeginDrag),
        "setWidgetTopmost" => SetTopmost(payload),
        "closeWidget" => Invoke(_actions.Close),
        "getWidgetsState" => _actions.GetState(),
        "setWidgetEnabled" => _actions.SetEnabled(
            BridgePayload.RequiredString(payload, "type", "Missing widget type"),
            BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled")),
        "setWidgetsMaster" => _actions.SetMasterEnabled(
            BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled")),
        "setWidgetPinned" => _actions.SetPinned(
            BridgePayload.RequiredString(payload, "type", "Missing widget type"),
            BridgePayload.RequiredBoolean(payload, "pinned", "Missing pinned")),
        "setWidgetSize" => _actions.SetSize(
            BridgePayload.RequiredString(payload, "type", "Missing widget type"),
            BridgePayload.RequiredString(payload, "size", "Missing widget size")),
        "setWidgetPlacement" => _actions.SetPlacement(
            BridgePayload.RequiredString(payload, "type", "Missing widget type"),
            BridgePayload.RequiredString(payload, "monitorId", "Missing monitor id"),
            BridgePayload.RequiredString(payload, "anchor", "Missing anchor")),
        "resetWidgetPosition" => _actions.ResetPosition(
            BridgePayload.RequiredString(payload, "type", "Missing widget type")),
        _ => throw new ArgumentException($"Handler cannot process RPC method '{method}'."),
    };
    return Task.FromResult(result);
}
```

`Invoke(Action)` executes the callback and returns `new { success = true }`; `SetTopmost` validates `topmost`, calls `SetTopmost`, and returns `new { success = true, topmost }`.

- [ ] **Step 4: Run widget tests and verify GREEN**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~WidgetRpcHandlerTests -v:minimal`

Expected: PASS.

- [ ] **Step 5: Commit widget extraction**

```powershell
git add src/VoltManager/Bridge/Handlers/WidgetRpcHandler.cs tests/VoltManager.Tests/WidgetRpcHandlerTests.cs
git commit -m "refactor: extract widget rpc handler"
git push origin Dev
```

### Task 4: Energy handler

**Files:**
- Create: `src/VoltManager/Bridge/Handlers/EnergyRpcHandler.cs`
- Create: `src/VoltManager/Bridge/Handlers/EnergyRpcActions.cs`
- Create: `tests/VoltManager.Tests/EnergyRpcHandlerTests.cs`

**Interfaces:**
- Consumes: `SettingsService`, `LocalizationService`, `IBridgeFileDialogService`, `BridgePayload`, `BridgeRpc.JsonOpts`.
- Produces: `EnergyRpcActions` with narrow delegates for battery health/power/history, default-plan check/restore, active-plan lookup/reason/history, plan listing/parameters, keep-awake state/update, CPU automation state, manual override set/clear, power-source state/update, thermal guard state/update, idle guard state/update, scheduled-power-action state/execute/schedule/cancel, standby purge, battery-history CSV conversion/write inputs, and the clock (`Func<DateTime> UtcNow`). No delegate may expose `App` or WebView2.
- Produces: `EnergyRpcHandler` registering exactly 32 methods: `getBatteryHealth`, `getBatteryPower`, `getBatteryHistory`, `exportBatteryHistory`, `checkDefaultPlans`, `restoreDefaultPlans`, `getActivePlan`, `getActivePlanReason`, `getPlanHistory`, `clearPlanHistory`, `listPowerPlans`, `getKeepAwakeState`, `setKeepAwake`, `setKeepAwakeSafety`, `getCpuAutomationState`, `setManualOverride`, `clearManualOverride`, `getPowerSourcePlanState`, `setPowerSourcePlanSwitch`, `getThermalGuardState`, `setThermalGuardEnabled`, `setThermalGuardSettings`, `getIdlePowerGuardState`, `setIdlePowerGuardEnabled`, `setIdlePowerGuardSettings`, `getScheduledPowerAction`, `executePowerAction`, `schedulePowerAction`, `cancelScheduledPowerAction`, `getPlanTimeouts`, `getPlanParameters`, `setPlanParameter`.

- [ ] **Step 1: Write failing energy method-inventory and representative contract tests**

Assert the exact 32-name set. Add tests for:

- `getBatteryHistory` defaulting to 48 hours and honoring explicit `hours`;
- `setKeepAwakeSafety` retaining omitted safety fields;
- invalid manual-override plan returns an argument error;
- scheduled action accepts `relative` and `daily` and rejects invalid action/mode/time;
- `setPlanParameter` validates required ids/values before invoking the service delegate;
- battery-history export cancellation and UTF-8 CSV write;
- thermal/idle settings deserialize through `BridgeRpc.JsonOpts` and invoke the matching delegate once.

- [ ] **Step 2: Run energy tests and verify RED**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~EnergyRpcHandlerTests -v:minimal`

Expected: FAIL because energy handler/actions do not exist.

- [ ] **Step 3: Extract energy RPC bodies**

Move the existing implementations for the full energy method set. Keep `Task.Run` around blocking operations where the current bridge uses it, but invoke only `EnergyRpcActions`/safe service methods from the handler. Build production delegates from `PowerPlanService`, `PowerPlanParameterService`, battery services, and app-owned power components in the `HostBridge` composition root. The handler must never access `App`, WPF, WebView2, `powercfg`, or WMI directly.

Use `EnergyRpcActions.UtcNow` anywhere the current code calls `DateTime.UtcNow` so time-dependent tests are deterministic.

```csharp
public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken ct)
{
    return method switch
    {
        "getBatteryHealth" => await Task.Run(_actions.GetBatteryHealth, ct),
        "getBatteryPower" => await GetBatteryPowerAsync(ct),
        "getBatteryHistory" => await GetBatteryHistoryAsync(payload, ct),
        "exportBatteryHistory" => await ExportBatteryHistoryAsync(ct),
        "checkDefaultPlans" => await CheckDefaultPlansAsync(ct),
        "restoreDefaultPlans" => new { success = await Task.Run(_actions.RestoreDefaultPlans, ct) },
        "getActivePlan" => await Task.Run(_actions.GetActivePlan, ct),
        "getActivePlanReason" => _actions.GetActivePlanReason(),
        "getPlanHistory" => _actions.GetPlanHistory(),
        "clearPlanHistory" => new { revision = _actions.ClearPlanHistory() },
        "listPowerPlans" => await Task.Run(_actions.ListPowerPlans, ct),
        "getKeepAwakeState" => _actions.GetKeepAwakeState(),
        "setKeepAwake" => _actions.SetKeepAwake(BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled")),
        "setKeepAwakeSafety" => await SetKeepAwakeSafetyAsync(payload, ct),
        "getCpuAutomationState" => _actions.GetCpuAutomationState(),
        "setManualOverride" => await SetManualOverrideAsync(payload, ct),
        "clearManualOverride" => await ClearManualOverrideAsync(ct),
        "getPowerSourcePlanState" => await Task.Run(_actions.GetPowerSourcePlanState, ct),
        "setPowerSourcePlanSwitch" => await Task.Run(() => _actions.SetPowerSourcePlanSwitch(
            BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled")), ct),
        "getThermalGuardState" => await Task.Run(_actions.GetThermalGuardState, ct),
        "setThermalGuardEnabled" => await Task.Run(() => _actions.SetThermalGuardEnabled(
            BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled")), ct),
        "setThermalGuardSettings" => await SetThermalGuardSettingsAsync(payload, ct),
        "getIdlePowerGuardState" => await Task.Run(_actions.GetIdlePowerGuardState, ct),
        "setIdlePowerGuardEnabled" => await Task.Run(() => _actions.SetIdlePowerGuardEnabled(
            BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled")), ct),
        "setIdlePowerGuardSettings" => await SetIdlePowerGuardSettingsAsync(payload, ct),
        "getScheduledPowerAction" => _actions.GetScheduledPowerAction(),
        "executePowerAction" => ExecutePowerAction(payload),
        "schedulePowerAction" => SchedulePowerAction(payload),
        "cancelScheduledPowerAction" => _actions.CancelScheduledPowerAction(),
        "getPlanTimeouts" => await Task.Run(() => _actions.GetPlanTimeouts(OptionalString(payload, "planGuid")), ct),
        "getPlanParameters" => await Task.Run(() => _actions.GetPlanParameters(OptionalString(payload, "planGuid")), ct),
        "setPlanParameter" => await SetPlanParameterAsync(payload, ct),
        _ => throw new ArgumentException($"Handler cannot process RPC method '{method}'."),
    };
}
```

The private helpers above contain the current per-case parsing/result logic: battery history defaults to 48 hours; manual override parses `PlanId` and optional hours; scheduled actions parse `Shutdown|Restart`, `relative|daily`, and `HH:mm`; thermal/idle settings use `BridgePayload.Deserialize<T>`; plan-parameter writes require `planGuid`, `settingKey`, `acValue`, and `dcValue`.

- [ ] **Step 4: Run energy and power-domain tests**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter "FullyQualifiedName~EnergyRpcHandlerTests|FullyQualifiedName~PowerPlan|FullyQualifiedName~PlanHistory" -v:minimal`

Expected: PASS.

- [ ] **Step 5: Commit energy extraction**

```powershell
git add src/VoltManager/Bridge/Handlers/EnergyRpcHandler.cs src/VoltManager/Bridge/Handlers/EnergyRpcActions.cs tests/VoltManager.Tests/EnergyRpcHandlerTests.cs
git commit -m "refactor: extract energy rpc handler"
git push origin Dev
```

### Task 5: Monitoring handler

**Files:**
- Create: `src/VoltManager/Bridge/Handlers/MonitoringRpcHandler.cs`
- Create: `src/VoltManager/Bridge/Handlers/MonitoringRpcActions.cs`
- Create: `tests/VoltManager.Tests/MonitoringRpcHandlerTests.cs`

**Interfaces:**
- Consumes: `SettingsService`, `IBridgeFileDialogService`, `BridgePayload`.
- Produces: `MonitoringRpcActions` with delegates for system information, heavy-app state/refresh, app-power-profile state, app-profile executable picker, top-process lookup, memory status, standby purge, log-folder opening, diagnostics report construction, and any snapshot suppliers required by diagnostics. Production composition may use `HardwareInfoService`, `MonitorService`, `PowerPlanService`, and app-owned state, but the handler receives only these delegates.
- Produces: `MonitoringRpcHandler` registering exactly 10 methods: `getSystemInfo`, `getHeavyAppStatus`, `refreshHeavyAppDetection`, `getAppPowerProfileStatus`, `pickAppPowerProfileExecutable`, `getTopProcesses`, `getMemoryStatus`, `purgeStandbyList`, `openLogFolder`, `exportDiagnostics`.

- [ ] **Step 1: Write failing monitoring tests**

Assert exact method inventory. Test default and explicit top-process count, app-profile picker cancellation/success, standby purge result shape, log-folder result shape via an injected delegate, diagnostics export cancellation, and diagnostics export writing the report returned by an injected `Func<string>` report builder. Keep WMI/hardware APIs out of unit tests by injecting the exact functions used by each RPC.

- [ ] **Step 2: Run monitoring tests and verify RED**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~MonitoringRpcHandlerTests -v:minimal`

Expected: FAIL because monitoring handler/actions do not exist.

- [ ] **Step 3: Extract monitoring methods and diagnostics assembly**

Preserve current `getTopProcesses` default of `8`, current diagnostics text shape, current log-folder `{ success, path, error }` result, and current cancellation results `{ success = false, cancelled = true }`. Keep `DiagnosticsReportService.BuildReport` and sanitization behavior unchanged in the production delegate that assembles the report; the handler only requests the report and writes it to the selected path.

```csharp
public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken ct)
{
    return method switch
    {
        "getSystemInfo" => await Task.Run(_actions.GetSystemInfo, ct),
        "getHeavyAppStatus" => await Task.Run(_actions.GetHeavyAppStatus, ct),
        "refreshHeavyAppDetection" => await Task.Run(_actions.RefreshHeavyAppDetection, ct),
        "getAppPowerProfileStatus" => await Task.Run(_actions.GetAppPowerProfileStatus, ct),
        "pickAppPowerProfileExecutable" => new { path = await _actions.PickAppPowerProfileExecutable(ct) },
        "getTopProcesses" => await Task.Run(() => _actions.GetTopProcesses(
            BridgePayload.OptionalInt32(payload, "count", 8)), ct),
        "getMemoryStatus" => await Task.Run(_actions.GetMemoryStatus, ct),
        "purgeStandbyList" => await PurgeStandbyListAsync(ct),
        "openLogFolder" => _actions.OpenLogFolder(),
        "exportDiagnostics" => await ExportDiagnosticsAsync(ct),
        _ => throw new ArgumentException($"Handler cannot process RPC method '{method}'."),
    };
}
```

`PurgeStandbyListAsync` returns `new { success = purged, memory = status }`. `ExportDiagnosticsAsync` asks the file-dialog abstraction for the diagnostics path, returns the existing cancellation shape when null, requests the report from `BuildDiagnosticsReport`, writes it, and returns `new { success = true, path, bytes = report.Length }`.

- [ ] **Step 4: Run monitoring tests and diagnostics/resource regressions**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter "FullyQualifiedName~MonitoringRpcHandlerTests|FullyQualifiedName~Resource|FullyQualifiedName~Hardware" -v:minimal`

Expected: PASS.

- [ ] **Step 5: Commit monitoring extraction**

```powershell
git add src/VoltManager/Bridge/Handlers/MonitoringRpcHandler.cs src/VoltManager/Bridge/Handlers/MonitoringRpcActions.cs tests/VoltManager.Tests/MonitoringRpcHandlerTests.cs
git commit -m "refactor: extract monitoring rpc handler"
git push origin Dev
```

### Task 6: Update and application-shell handlers

**Files:**
- Create: `src/VoltManager/Bridge/Handlers/UpdateRpcHandler.cs`
- Create: `src/VoltManager/Bridge/Handlers/UpdateRpcActions.cs`
- Create: `src/VoltManager/Bridge/Handlers/ApplicationRpcHandler.cs`
- Create: `src/VoltManager/Bridge/Handlers/ApplicationRpcActions.cs`
- Create: `tests/VoltManager.Tests/UpdateRpcHandlerTests.cs`
- Create: `tests/VoltManager.Tests/ApplicationRpcHandlerTests.cs`

**Interfaces:**
- Consumes: `LocalizationService`, `BridgePayload`, `BridgeRpc.HandleLogError`.
- Produces: `UpdateRpcActions` with `Func<Task<UpdateInfo>> CheckForUpdates`, `Func<Task<ReleaseHistory>> GetReleaseHistory`, `Func<string, Task<string>> DownloadUpdate`, `Func<bool> IsHeavyAppSessionActive`, `Action<string> DeferUpdateUntilGameEnds`, `Action<string,string> LaunchInstaller`, and `Action RequestExit`.
- Produces: `UpdateRpcHandler` registering `checkForUpdates`, `getReleaseHistory`, `downloadUpdate`.
- Produces: `ApplicationRpcActions` for gaming mode get/set, startup-app snapshot/add/enable/remove, startup executable picker, external URL launch, exit, minimize, and logger. The handler receives no `StartupAppsService`; production composition binds the delegates to that service.
- Produces: `ApplicationRpcHandler` registering exactly 11 methods: `getGamingMode`, `setGamingMode`, `getStartupApps`, `pickStartupExecutable`, `addStartupApp`, `setStartupAppEnabled`, `removeStartupApp`, `logError`, `openExternal`, `exitApp`, `minimizeToTray`.

- [ ] **Step 1: Write failing update/application tests**

Update tests cover the exact 3-name inventory, heavy-app deferral before download, heavy-app deferral after download, successful installer launch arguments, and missing URL. Application tests cover exact inventory, gaming-mode unavailable error, startup-app required path/id validation, picker cancellation, `logError` swallowing logger failures through `BridgeRpc.HandleLogError`, `openExternal` launching only `http://`/`https://`, and exit/minimize callbacks.

- [ ] **Step 2: Run both new suites and verify RED**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter "FullyQualifiedName~UpdateRpcHandlerTests|FullyQualifiedName~ApplicationRpcHandlerTests" -v:minimal`

Expected: FAIL because the handlers/actions do not exist.

- [ ] **Step 3: Extract update and application-shell methods**

Keep the current update deferral payload exactly:

```csharp
new
{
    success = false,
    deferred = true,
    reason = "heavyAppActive",
    message = _loc.T("Dialog_UpdateDeferredGame"),
}
```

The handler passes downloaded installer path plus the existing `/update --pid {Environment.ProcessId} --lang {language}` argument string to the injected launch action. Application handler uses `ApplicationRpcActions` only and contains no WebView2/WPF types. `UpdateRpcHandler` invokes the update delegates rather than storing `UpdateService`, so its tests never perform network I/O.

```csharp
// UpdateRpcHandler
public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken ct)
    => method switch
    {
        "checkForUpdates" => await _actions.CheckForUpdates(),
        "getReleaseHistory" => await _actions.GetReleaseHistory(),
        "downloadUpdate" => await DownloadUpdateAsync(
            BridgePayload.RequiredString(payload, "url", "URL mancante"), ct),
        _ => throw new ArgumentException($"Handler cannot process RPC method '{method}'."),
    };

// ApplicationRpcHandler
public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken ct)
{
    return method switch
    {
        "getGamingMode" => _actions.GetGamingMode(),
        "setGamingMode" => await _actions.SetGamingMode(
            BridgePayload.RequiredBoolean(payload, "enabled", _loc.T("Error_GamingControlUnavailable"))),
        "getStartupApps" => await Task.Run(_actions.GetStartupApps, ct),
        "pickStartupExecutable" => new { path = await _actions.PickStartupExecutable(ct) },
        "addStartupApp" => await AddStartupAppAsync(payload, ct),
        "setStartupAppEnabled" => await SetStartupAppEnabledAsync(payload, ct),
        "removeStartupApp" => await RemoveStartupAppAsync(payload, ct),
        "logError" => LogError(payload),
        "openExternal" => OpenExternal(payload),
        "exitApp" => Invoke(_actions.RequestExit),
        "minimizeToTray" => Invoke(_actions.RequestMinimize),
        _ => throw new ArgumentException($"Handler cannot process RPC method '{method}'."),
    };
}
```

`DownloadUpdateAsync` checks `IsHeavyAppSessionActive` both before and after download. `AddStartupAppAsync`, `SetStartupAppEnabledAsync`, and `RemoveStartupAppAsync` preserve the current required-field messages and result shapes. `OpenExternal` invokes the launch delegate only for HTTP(S) URLs and still returns `{ success = true }`.

- [ ] **Step 4: Run handler and update-service tests**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter "FullyQualifiedName~UpdateRpcHandlerTests|FullyQualifiedName~ApplicationRpcHandlerTests|FullyQualifiedName~UpdateServiceTests" -v:minimal`

Expected: PASS.

- [ ] **Step 5: Commit update/application extraction**

```powershell
git add src/VoltManager/Bridge/Handlers/UpdateRpcHandler.cs src/VoltManager/Bridge/Handlers/UpdateRpcActions.cs src/VoltManager/Bridge/Handlers/ApplicationRpcHandler.cs src/VoltManager/Bridge/Handlers/ApplicationRpcActions.cs tests/VoltManager.Tests/UpdateRpcHandlerTests.cs tests/VoltManager.Tests/ApplicationRpcHandlerTests.cs
git commit -m "refactor: extract update and app rpc handlers"
git push origin Dev
```

### Task 7: Rewire `HostBridge` as transport/lifecycle composition root

**Files:**
- Modify: `src/VoltManager/Bridge/HostBridge.cs`
- Modify: `src/VoltManager/MainWindow.xaml.cs`
- Modify: `src/VoltManager/WidgetWindow.xaml.cs`
- Create: `src/VoltManager/Bridge/BridgeEventNames.cs`
- Create: `src/VoltManager/Bridge/Rpc/WebViewBridgeFileDialogService.cs`
- Create: `src/VoltManager/Bridge/Rpc/BridgeLifetime.cs`
- Create: `tests/VoltManager.Tests/BridgeLifetimeTests.cs`
- Create: `tests/VoltManager.Tests/BridgeContractInventoryTests.cs`
- Modify: `tests/VoltManager.Tests/BridgeRpcTests.cs`

**Interfaces:**
- Consumes: all six handlers and `BridgeRpcDispatcher`.
- Produces: `BridgeLifetime.Token : CancellationToken`, `TryBeginAttach() : bool`, `RegisterDetach(Action)`, `Dispose()` with idempotent cancellation/detach.
- Produces: `WebViewBridgeFileDialogService` implementing the Task 2 file-dialog interface through `_webView.Dispatcher.InvokeAsync`.
- Produces: `BridgeEventNames` constants plus `All`, the exact 27-name event compatibility inventory used by `HostBridge`, `MainWindow`, and `WidgetWindow`.
- `HostBridge.HandleMessageAsync` consumes only `BridgeRpcDispatcher.DispatchAsync` and posts `ReplyJson` when the lifetime is still active.

- [ ] **Step 1: Write failing lifecycle and complete-registry tests**

`BridgeLifetimeTests` must prove:

```csharp
[Fact]
public void Attach_gate_and_dispose_are_idempotent()
{
    int detached = 0;
    using var lifetime = new BridgeLifetime();
    Assert.True(lifetime.TryBeginAttach());
    Assert.False(lifetime.TryBeginAttach());
    lifetime.RegisterDetach(() => detached++);
    lifetime.Dispose();
    lifetime.Dispose();
    Assert.Equal(1, detached);
    Assert.True(lifetime.Token.IsCancellationRequested);
}
```

Add a registry test that constructs all six handlers with fakes and asserts the dispatcher's method set exactly equals the 81 names captured from the old switch. Add a late-reply test: start a dispatcher call, dispose/cancel lifetime before its fake handler completes, release the handler, and assert the transport callback was never invoked.

Add an event inventory test asserting `BridgeEventNames.All` equals this exact set:

```csharp
[
    "activePlanChanged", "activePlanReasonChanged", "appPowerProfileActivityChanged",
    "appUpdated", "automationStateChanged", "cpuAutomationStateChanged", "fontChanged",
    "gamingModeChanged", "globalHotkeysChanged", "heavyAppActivityChanged",
    "idlePowerGuardChanged", "keepAwakeChanged", "languageChanged",
    "manualOverrideChanged", "metrics", "planHistoryChanged", "powerPlanConflictDetected",
    "powerSourcePlanChanged", "resourceProfileChanged", "scheduledPowerActionChanged",
    "standbyAutoCleaned", "themeChanged", "thermalGuardChanged", "updateAvailable",
    "updateDownloadProgress", "widgetsStateChanged", "widgetTopmostChanged",
]
```

- [ ] **Step 2: Run bridge/lifecycle tests and verify RED**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter "FullyQualifiedName~BridgeLifetimeTests|FullyQualifiedName~BridgeRpc" -v:minimal`

Expected: FAIL because `BridgeLifetime` and full dispatcher composition are not wired.

- [ ] **Step 3: Build handler composition in `HostBridge` and delete the 81-case switch**

Construct targeted action/dependency records from the constructor's existing services and `_app` once, then construct the six handlers and dispatcher. Do not pass `_app` into any handler or dispatcher constructor. `HostBridge` may retain `_app` only for its existing global event subscriptions and composition callbacks. Bind `SettingsRpcActions` autostart delegates to `_startup`, `UpdateRpcActions` network delegates to `_updates`, application startup delegates to `_startupApps`, and energy/monitoring delegates to the corresponding services/app-owned components.

Replace `HandleMessageAsync` with:

```csharp
private async Task HandleMessageAsync(string json)
{
    BridgeRpcDispatchResult result = await _dispatcher.DispatchAsync(json, _lifetime.Token);
    if (result.Exception != null && result.LogMessage != null)
        Logger.Error(result.LogMessage, result.Exception);

    if (!_lifetime.Token.IsCancellationRequested && result.ReplyJson != null)
        PostReplyJson(result.ReplyJson);
}
```

Use `BridgeLifetime.TryBeginAttach()` in `Attach()`. Every event subscription performed there immediately registers its inverse with `BridgeLifetime.RegisterDetach`. `Dispose()` disposes the lifetime before nulling WebView references. `PushEvent`/`PostEvent`/`PostReplyJson` check lifetime cancellation and `_disposed` before scheduling and again inside dispatcher callbacks before posting. `PostEvent` serializes through `BridgeRpc.FormatEvent` rather than an inline anonymous object.

Replace every bridge event literal in `HostBridge`, `MainWindow`, and `WidgetWindow` with the matching `BridgeEventNames` constant. `BridgeEventNames.All` is a distinct immutable collection of those 27 constants and is the source for the compatibility-inventory test.

Delete `DispatchAsync` and the settings/helper bodies that moved to handlers. Keep only WebView2 transport, event forwarding, composition, and lifecycle code in `HostBridge`.

- [ ] **Step 4: Run bridge/lifecycle tests and verify GREEN**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter "FullyQualifiedName~BridgeLifetimeTests|FullyQualifiedName~BridgeRpc|FullyQualifiedName~RpcHandlerTests" -v:minimal`

Expected: PASS. If the filter does not match all handler classes because xUnit uses their full names, run the full `VoltManager.Tests` project instead and record that ruling in the execution ledger.

- [ ] **Step 5: Verify structural completion before commit**

Run:

```powershell
(Select-String -Path src\VoltManager\Bridge\HostBridge.cs -Pattern 'case "').Count
rg -n "\bApp\b|WebView2|CoreWebView2" src/VoltManager/Bridge/Handlers src/VoltManager/Bridge/Rpc/BridgeRpcDispatcher.cs
```

Expected: first command prints `0`; second command has no handler/dispatcher references to `App`, `WebView2`, or `CoreWebView2`.

- [ ] **Step 6: Commit transport rewiring**

```powershell
git add src/VoltManager/Bridge src/VoltManager/MainWindow.xaml.cs src/VoltManager/WidgetWindow.xaml.cs tests/VoltManager.Tests/BridgeLifetimeTests.cs tests/VoltManager.Tests/BridgeContractInventoryTests.cs tests/VoltManager.Tests/BridgeRpcTests.cs
git commit -m "refactor: separate rpc transport from domain handlers"
git push origin Dev
```

### Task 8: Contract audit, full regression verification, and issue closure

**Files:**
- Modify: `tests/VoltManager.Tests/BridgeRpcTests.cs` if a final 81-name/casing fixture belongs there rather than Task 7's test file.
- Modify: `tests/bridge-timeout.test.mjs` only if a missing out-of-order/error regression is discovered; no production `bridge.js` edit is planned.
- Modify: `docs/superpowers/plans/2026-09-20-issue-35-rpc-handler-separation.md` only for checked task boxes if the execution workflow records them in-plan; the authoritative progress ledger remains under `.superpowers/sdd/`.

**Interfaces:**
- Consumes: final dispatcher/handlers/transport from Tasks 1-7.
- Produces: executable evidence for every issue #35 completion criterion and closes GitHub issue #35 only after all evidence is green.

- [ ] **Step 1: Add any missing contract assertions discovered by the requirement audit**

Audit the issue and spec requirement-by-requirement. The final test set must explicitly prove all 81 names, camelCase reply serialization, handler testability without WebView2, one-failure behavior, concurrent id isolation, attach/dispose idempotence, and late-publication suppression. If any proof is missing, first add a test that fails against the current implementation, then make the smallest implementation change required to pass it.

- [ ] **Step 2: Run the complete .NET suite**

Run: `dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release -v:minimal`

Expected: PASS with zero failed tests.

- [ ] **Step 3: Run the complete Node suite**

Run: `node --test tests/*.test.mjs`

Expected: PASS with zero failed tests, including `bridge-timeout.test.mjs`.

- [ ] **Step 4: Build the full solution in Release**

Run: `dotnet build VoltManager.sln -c Release -v:minimal`

Expected: exit code 0 with no build errors.

- [ ] **Step 5: Re-run structural contract checks**

Run:

```powershell
$rpcCount = (Select-String -Path src\VoltManager\Bridge\HostBridge.cs -Pattern 'case "').Count
$forbidden = rg -n "\bApp\b|WebView2|CoreWebView2" src/VoltManager/Bridge/Handlers src/VoltManager/Bridge/Rpc/BridgeRpcDispatcher.cs
Write-Output "hostbridge_case_count=$rpcCount"
if ($forbidden) { $forbidden; exit 1 }
```

Expected: `hostbridge_case_count=0` and exit code 0.

- [ ] **Step 6: Commit any final test-only audit additions and push**

If Step 1 changed files:

```powershell
git add tests src/VoltManager/Bridge
git commit -m "test: lock rpc handler contracts for issue 35"
git push origin Dev
```

If there were no file changes, do not create an empty commit.

- [ ] **Step 7: Perform final whole-branch review**

Use `superpowers:requesting-code-review` through the execution workflow's review package. Re-grade findings by user impact. Fix Critical/Important findings in one TDD pass, re-run the complete .NET suite, Node suite, and Release build, then commit/push the fixes. Ledger Minor findings without changing code.

- [ ] **Step 8: Close issue #35 only after verification remains green on pushed `Dev`**

Confirm `git status --short --branch` shows `Dev...origin/Dev` with no changes and `git rev-parse HEAD` equals `git rev-parse origin/Dev`. Then close with a concise evidence comment:

```powershell
gh issue close 35 --comment "Implemented on Dev: HostBridge now delegates all 81 RPC methods through transport-independent domain handlers and a centralized dispatcher. Contract/lifecycle/concurrency regressions, full .NET tests, Node bridge tests, and Release build are green."
```

Expected: GitHub reports issue #35 closed.
