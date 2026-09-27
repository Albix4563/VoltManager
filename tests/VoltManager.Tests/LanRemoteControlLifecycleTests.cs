using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using VoltManager.Models;
using VoltManager.Services;
using VoltManager.Services.LanRemote;

namespace VoltManager.Tests;

public sealed class LanRemoteControlLifecycleTests
{
    [Fact]
    public async Task Start_returns_while_listener_start_is_still_in_flight()
    {
        using var fixture = new LanFixture();
        var entered = NewSignal();
        var release = NewSignal();
        using var service = fixture.Create(
            beforeStart: async _ =>
            {
                entered.TrySetResult();
                await release.Task;
            },
            addresses: []);

        service.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(service.GetState().Running);

        release.TrySetResult();
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Start_then_stop_race_ends_stopped()
    {
        using var fixture = new LanFixture();
        var entered = NewSignal();
        var release = NewSignal();
        using var service = fixture.Create(
            beforeStart: async _ =>
            {
                entered.TrySetResult();
                await release.Task;
            },
            addresses: []);

        Task start = service.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task stop = service.StopAsync();
        release.TrySetResult();
        await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(service.GetState().Running);
    }

    [Fact]
    public async Task Stop_then_start_race_ends_started()
    {
        using var fixture = new LanFixture();
        var stopEntered = NewSignal();
        var stopRelease = NewSignal();
        int stopHooks = 0;
        using var service = fixture.Create(beforeStop: async _ =>
        {
            if (Interlocked.Increment(ref stopHooks) == 1)
            {
                stopEntered.TrySetResult();
                await stopRelease.Task;
            }
        });
        await service.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.GetState().Running);

        Task stop = service.StopAsync();
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task start = service.StartAsync();
        stopRelease.TrySetResult();
        await Task.WhenAll(stop, start).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(service.GetState().Running);
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Repeated_toggle_ends_in_last_requested_state()
    {
        using var fixture = new LanFixture();
        using var service = fixture.Create();

        for (int i = 0; i < 9; i++)
            await service.SetEnabledAsync(i % 2 == 0).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(service.GetState().Enabled);
        Assert.True(service.GetState().Running);
        await service.SetEnabledAsync(false).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stop_under_non_pumping_synchronization_context_does_not_deadlock()
    {
        using var fixture = new LanFixture();
        var warnings = new List<string>();
        using var service = fixture.Create(shutdownTimeout: TimeSpan.FromSeconds(2), warningLogger: warnings.Add);
        await service.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        SynchronizationContext? previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            service.Stop();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.False(service.GetState().Running);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Stop_timeout_logs_and_returns()
    {
        using var fixture = new LanFixture();
        var blocker = NewSignal();
        var warnings = new List<string>();
        using var service = fixture.Create(
            beforeStop: _ => blocker.Task,
            shutdownTimeout: TimeSpan.FromMilliseconds(50),
            warningLogger: warnings.Add,
            addresses: []);

        service.Stop();

        Assert.Single(warnings);
        Assert.Contains("did not complete", warnings[0], StringComparison.Ordinal);
        blocker.TrySetResult();
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Disabled_setting_does_not_start()
    {
        using var fixture = new LanFixture(enabled: false);
        int startHooks = 0;
        using var service = fixture.Create(beforeStart: _ =>
        {
            Interlocked.Increment(ref startHooks);
            return Task.CompletedTask;
        }, addresses: []);

        service.Start();

        Assert.Equal(0, startHooks);
        Assert.False(service.GetState().Running);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        using var fixture = new LanFixture(enabled: false);
        var service = fixture.Create(addresses: []);

        service.Dispose();
        Assert.Null(Record.Exception(service.Dispose));
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
    }

    private sealed class LanFixture : IDisposable
    {
        private readonly string _root;
        private readonly SettingsService _settings;
        private readonly int _port;

        public LanFixture(bool enabled = true)
        {
            _root = Path.Combine(Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _settings = new SettingsService(Path.Combine(_root, "settings.json"));
            _port = GetFreeTcpPort();
            _settings.Update(state =>
            {
                state.LanRemoteControl.Enabled = enabled;
                state.LanRemoteControl.Port = _port;
            });
        }

        public LanRemoteControlService Create(
            Func<CancellationToken, Task>? beforeStart = null,
            Func<CancellationToken, Task>? beforeStop = null,
            TimeSpan? shutdownTimeout = null,
            Action<string>? warningLogger = null,
            IReadOnlyList<IPAddress>? addresses = null)
            => new(
                _settings,
                new LanRemoteControlActions(
                    () => new PowerPlan { PlanId = PlanId.Balanced, Name = "Balanced", IsActive = true },
                    _ => true,
                    _ => { },
                    () => new WindowsPowerCapabilityState(true, true),
                    () => "1.0.0"),
                auth: new LanRemoteAuthStore(Path.Combine(_root, "auth.json")),
                firewall: new RecordingFirewall(),
                addressProvider: () => addresses ?? [IPAddress.Loopback],
                portAvailable: (_, _) => true,
                clientAddressAllowed: _ => true,
                remoteAssetsPath: _root,
                useHttps: false,
                shutdownTimeout: shutdownTimeout,
                beforeStart: beforeStart,
                beforeStop: beforeStop,
                warningLogger: warningLogger);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        private sealed class RecordingFirewall : ILanRemoteFirewall
        {
            public void Apply(int port) { }
            public void Remove() { }
        }
    }
}
