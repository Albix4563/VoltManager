# CPU and Memory Efficiency Optimization Design

**Status:** approved in conversation on 2026-08-23

## Goal

Reduce VoltManager CPU time, private memory, managed allocations, WebView2 overhead, polling, IPC, timers, DOM work, and unnecessary background processing without changing functionality, responsiveness, animations, effects, or graphics.

The work is limited to five measured optimization attempts. A preparation-only change needed to run the benchmark does not consume an attempt; each product-code change does.

## Scope and branch safety

All work starts from `Dev` and remains on `perf/cpu-memory-efficiency`. The final pull request targets `Dev`; neither `Dev` nor `main` is modified directly.

Potential optimization areas are selected from measured runtime evidence, with initial attention to:

- hardware and sensor sampling;
- background timers and polling;
- WebView2 host-to-renderer IPC and serialization;
- DOM mutations, observers, listeners, and frontend timers;
- caches, resource lifetime, and process working sets.

No dependency, subsystem, visual simplification, disabled effect, reduced animation, or reduced user-visible update rate is introduced merely to improve a metric.

## Measurement scenario

Release builds are measured with an isolated, fixed application-data profile and no pre-existing VoltManager instance. Baseline and candidate builds use the same machine state, profile, phase order, phase duration, and collection interval.

Each run contains these phases:

1. launch and 60-second initialization settle;
2. visible Home/dashboard use;
3. visible Monitoring use, exercising metrics, sensors, and process data;
4. tray state held beyond the existing WebView2 teardown and working-set trim delays;
5. restore to the same visible view.

The baseline and final build are each run at least three times. A focused attempt may use a shorter version of the same relevant phase for diagnosis, but its keep/revert decision must be confirmed against the canonical scenario.

Measurements aggregate the VoltManager host, `VoltManager.HardwareService`, and associated WebView2 processes. Per phase, the benchmark records:

- normalized CPU time;
- private working set and private bytes, including median and peak;
- process count and lifecycle;
- managed allocation and GC counters where available;
- a targeted explanatory counter or trace for the suspected bottleneck, such as timer callbacks, IPC events, DOM observer callbacks, hardware queries, or allocation stacks.

External Windows counters and WPR traces are preferred over permanent product instrumentation. Temporary diagnostic instrumentation must not be included in the final product unless it becomes a small, maintained regression check.

## Attempt loop

For each of at most five attempts:

1. inspect the latest measurements and choose the highest-impact supported bottleneck;
2. record the evidence linking the bottleneck to a process, phase, and cost;
3. make one focused, minimal implementation change;
4. run the same before/after scenario;
5. keep the change only when the improvement is reproducible and no meaningful regression appears;
6. otherwise revert the attempt completely before choosing another candidate.

Initial code observations are hypotheses, not preselected changes. They include the body-wide legacy-panel `MutationObserver`, frontend timer/polling lifetime, and host events serialized and dispatched while the main WebView is parked. They are attempted only if measurements rank them above other costs.

## Acceptance and regression criteria

A change is kept only when all of the following hold:

- the intended metric improves in at least two of three comparable runs;
- the improvement exceeds baseline run-to-run variability in the affected phase;
- no meaningful CPU, memory, allocation, startup, tray, restore, IPC, hardware-monitoring, or UI responsiveness regression appears elsewhere;
- user-visible state remains current and behavior remains functionally equivalent;
- animations, effects, graphics, and their fidelity are unchanged;
- all required build, test, and JavaScript syntax checks pass.

A failed, noisy, neutral, or regressive product-code attempt counts toward the five-attempt maximum and is reported as reverted. The work stops early if no remaining candidate is supported by evidence.

## Validation

Every attempt runs:

```text
dotnet build VoltManager.sln -c Release
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release
node --test tests/*.test.mjs
Get-ChildItem src/VoltManager/wwwroot -Recurse -Filter *.js | ForEach-Object { node --check $_.FullName; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
```

Relevant focused tests or runnable self-checks are added for non-trivial changed behavior. Frontend changes also receive a real-app check of affected visible state, animation/effect continuity, tray behavior, and restore behavior.

Before completion, the retained diff receives simplification, code review, and a fresh verification pass. Only retained changes are committed in the final implementation history.

## Delivery

The final pull request targets `Dev` and reports:

- retained optimizations and their evidence;
- reverted attempts and why they failed acceptance;
- baseline versus final CPU and RAM measurements, with run variability;
- allocation, WebView2, IPC, polling, timer, or DOM measurements used in decisions;
- all tests and real-app checks performed;
- remaining performance opportunities and the evidence still needed to pursue them.
