using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class AsyncSingleFlightTests
{
    [Fact]
    public async Task Concurrent_callers_wait_for_the_same_initialization()
    {
        var gate = new AsyncSingleFlight();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;

        Task first = gate.Run(() => { starts++; return completion.Task; });
        Task second = gate.Run(() => { starts++; return completion.Task; });

        Assert.Equal(1, starts);
        Assert.Same(first, second);
        completion.SetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task Failed_initialization_can_be_retried()
    {
        var gate = new AsyncSingleFlight();
        int starts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.Run(() =>
        {
            starts++;
            return Task.FromException(new InvalidOperationException("runtime unavailable"));
        }));

        await gate.Run(() => { starts++; return Task.CompletedTask; });
        Assert.Equal(2, starts);
    }
}
