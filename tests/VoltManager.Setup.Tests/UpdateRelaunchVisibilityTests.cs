using System;
using System.Threading.Tasks;

namespace VoltManager.Setup.Tests
{
    public sealed class UpdateRelaunchVisibilityTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Updater_hides_before_launch_and_returns_only_on_failure(bool fails)
        {
            bool visible = true;
            var readiness = new TaskCompletionSource<bool>();
            Task handoff = App.RelaunchWithWindowHiddenAsync(
                () => visible = false,
                () => visible = true,
                () =>
                {
                    Assert.False(visible);
                    return readiness.Task;
                });

            Assert.False(visible);
            Assert.False(handoff.IsCompleted);
            if (fails)
            {
                readiness.SetException(new TimeoutException());
                await Assert.ThrowsAsync<TimeoutException>(() => handoff);
            }
            else
            {
                readiness.SetResult(true);
                await handoff;
            }

            Assert.Equal(fails, visible);
        }
    }
}
