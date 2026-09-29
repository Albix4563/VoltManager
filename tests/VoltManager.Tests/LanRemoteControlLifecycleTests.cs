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
        using var service = fixture.Create(shutdownTimeout: TimeSpan.FromSeconds(2));
        await service.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        SynchronizationContext? previous = SynchronizationContext.Current;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            service.Stop();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(service.GetState().Running);
    }

    [Fact]
    public async Task Stop_returns_without_waiting_for_blocked_stop_hook()
    {
        using var fixture = new LanFixture();
        var blocker = NewSignal();
        using var service = fixture.Create(
            beforeStop: _ => blocker.Task,
            shutdownTimeout: TimeSpan.FromMilliseconds(50),
            addresses: []);

        var stopwatch = Stopwatch.StartNew();
        service.Stop();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
        blocker.TrySetResult();
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Failed_enable_keeps_enabled_false_and_exposes_last_error()
    {
        using var fixture = new LanFixture(enabled: false);
        using var service = fixture.Create(
            beforeStart: _ => Task.FromException(new InvalidOperationException("listener failed")));

        LanRemoteEnableResult result = await service.SetEnabledAsync(true).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.State.Enabled);
        Assert.False(result.State.Running);
        Assert.NotNull(result.State.LastError);
        Assert.Contains("listener failed", result.State.LastError!, StringComparison.Ordinal);
        Assert.False(fixture.Settings.Current.LanRemoteControl.Enabled);
    }

    [Fact]
    public void Corrupt_auth_file_disables_feature_and_state_exposes_flag()
    {
        using var fixture = new LanFixture(enabled: true);
        File.WriteAllText(fixture.AuthPath, "{broken");
        var auth = new LanRemoteAuthStore(fixture.AuthPath);

        using var service = fixture.Create(auth: auth);

        Assert.False(fixture.Settings.Current.LanRemoteControl.Enabled);
        Assert.True(service.GetState().AuthStoreCorrupt);
        Assert.False(service.GetState().HasPin);

        service.SetPin("Abc12345");
        Assert.False(service.GetState().AuthStoreCorrupt);
    }

    [Fact]
    public void Generating_new_secret_clears_corrupt_auth_flag()
    {
        using var fixture = new LanFixture(enabled: false);
        File.WriteAllText(fixture.AuthPath, "{broken");
        var auth = new LanRemoteAuthStore(fixture.AuthPath);
        using var service = fixture.Create(auth: auth);

        string secret = service.GeneratePin();

        Assert.True(LanRemotePinAuth.IsValidPin(secret));
        Assert.False(service.GetState().AuthStoreCorrupt);
        Assert.True(service.GetState().HasPin);
    }

    [Fact]
    public void Legacy_auth_keeps_has_pin_and_requests_regeneration()
    {
        using var fixture = new LanFixture(enabled: false);
        var legacy = new LanRemotePinVerifier
        {
            SaltBase64 = Convert.ToBase64String(new byte[16]),
            HashBase64 = Convert.ToBase64String(new byte[32]),
            Iterations = LanRemotePinAuth.Iterations,
            Digits = 4,
        };
        File.WriteAllText(fixture.AuthPath, System.Text.Json.JsonSerializer.Serialize(legacy));
        var auth = new LanRemoteAuthStore(fixture.AuthPath);

        using var service = fixture.Create(auth: auth);
        LanRemoteControlState state = service.GetState();

        Assert.True(state.HasPin);
        Assert.True(state.PinNeedsRegeneration);
        Assert.False(state.AuthStoreCorrupt);
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

        public SettingsService Settings => _settings;
        public string AuthPath => Path.Combine(_root, "auth.json");

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
            IReadOnlyList<IPAddress>? addresses = null,
            LanRemoteAuthStore? auth = null)
            => new(
                _settings,
                new LanRemoteControlActions(
                    () => new PowerPlan { PlanId = PlanId.Balanced, Name = "Balanced", IsActive = true },
                    _ => true,
                    _ => { },
                    () => new WindowsPowerCapabilityState(true, true),
                    () => "1.0.0"),
                auth: auth ?? new LanRemoteAuthStore(AuthPath),
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
