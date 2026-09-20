using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class RestartableLifecycleTests
{
    [Fact]
    public void Start_stop_restart_uses_one_fresh_epoch()
    {
        using var lifecycle = new RestartableLifecycle();
        CancellationToken first = lifecycle.Start();
        CancellationToken duplicate = lifecycle.Start();
        Assert.Equal(first, duplicate);
        Assert.True(lifecycle.IsCurrent(first));

        Assert.True(lifecycle.Stop());
        Assert.False(lifecycle.Stop());
        Assert.True(first.IsCancellationRequested);

        CancellationToken second = lifecycle.Start();
        Assert.NotEqual(first, second);
        Assert.True(lifecycle.IsCurrent(second));
        Assert.False(lifecycle.IsCurrent(first));
    }

    [Fact]
    public void Dispose_is_idempotent_and_terminal()
    {
        var lifecycle = new RestartableLifecycle();
        CancellationToken token = lifecycle.Start();

        lifecycle.Dispose();
        lifecycle.Dispose();

        Assert.True(token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => lifecycle.Start());
    }
}
