using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class UpdateCoordinatorTests
{
    [Fact]
    public void Repeated_start_owns_one_timer()
    {
        int timers = 0;
        using var coordinator = UpdateCoordinator.ForTest(
            check: () => Task.FromResult(new UpdateInfo()),
            protectedWorkload: () => false,
            createTimer: (_, _) => { timers++; return new CallbackDisposable(); });

        coordinator.Start();
        coordinator.Start();
        Assert.Equal(1, timers);
    }

    [Fact]
    public async Task Concurrent_checks_share_one_inflight_operation()
    {
        int checks = 0;
        var release = new TaskCompletionSource<UpdateInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = UpdateCoordinator.ForTest(
            check: () =>
            {
                Interlocked.Increment(ref checks);
                return release.Task;
            },
            protectedWorkload: () => false,
            createTimer: (_, _) => new CallbackDisposable());

        coordinator.Start();
        Task first = coordinator.CheckNowAsync(automatic: true);
        Task second = coordinator.CheckNowAsync(automatic: true);
        Assert.Equal(1, Volatile.Read(ref checks));
        release.SetResult(new UpdateInfo());
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task Protected_workload_defers_check_and_resumes_once()
    {
        bool protectedWorkload = true;
        int checks = 0;
        using var coordinator = UpdateCoordinator.ForTest(
            check: () =>
            {
                checks++;
                return Task.FromResult(new UpdateInfo());
            },
            protectedWorkload: () => protectedWorkload,
            createTimer: (_, _) => new CallbackDisposable());

        coordinator.Start();
        await coordinator.CheckNowAsync(automatic: true);
        Assert.Equal(0, checks);
        protectedWorkload = false;
        await coordinator.NotifyProtectedWorkloadChangedAsync(active: false);
        await coordinator.NotifyProtectedWorkloadChangedAsync(active: false);
        Assert.Equal(1, checks);
    }

    [Fact]
    public async Task Stop_suppresses_late_update_publication()
    {
        var release = new TaskCompletionSource<UpdateInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = UpdateCoordinator.ForTest(
            check: () => release.Task,
            protectedWorkload: () => false,
            createTimer: (_, _) => new CallbackDisposable());
        int published = 0;
        coordinator.UpdateAvailable += _ => published++;

        coordinator.Start();
        Task pending = coordinator.CheckNowAsync(automatic: false);
        coordinator.Stop();
        release.SetResult(new UpdateInfo
        {
            UpdateAvailable = true,
            LatestVersion = "9.9.9",
            DownloadUrl = "https://example.invalid/update.exe",
        });
        await pending;
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task Workload_starting_during_download_blocks_install_request()
    {
        bool protectedWorkload = false;
        var download = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = UpdateCoordinator.ForTest(
            check: () => Task.FromResult(new UpdateInfo()),
            protectedWorkload: () => protectedWorkload,
            createTimer: (_, _) => new CallbackDisposable());
        string? installPath = null;
        coordinator.InstallRequested += path => installPath = path;

        coordinator.Start();
        Task prepare = coordinator.PrepareInstallAsync(
            "https://example.invalid/update.exe",
            (_, _) => download.Task);
        protectedWorkload = true;
        download.SetResult(@"C:\Temp\VoltManagerUpdate.exe");
        await prepare;
        Assert.Null(installPath);
    }
}
