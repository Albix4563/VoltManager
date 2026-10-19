using System;
using System.Text;
using System.Threading.Tasks;
using VoltManager.Services;

namespace VoltManager.Setup.Tests
{
    public sealed class UpdateSessionProtocolSetupTests
    {
        [Fact]
        public void NetFramework_accepts_strict_snapshot_serialized_by_host()
        {
            // Literal JSON matches System.Text.Json's array representation in .NET 8.
            const string json = "[\"#0B1120\",\"#111827\",\"#1E293B\",\"#123456\",\"#567890\",\"#ABCDEF\",\"#F8FAFC\",\"#CBD5E1\",\"#434343\",\"#FFFFFF\"]";
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            Assert.True(UpdateSessionProtocol.TryDecodePalette(base64, out string[] colors));
            Assert.Equal("#123456", colors[3]);
            Assert.Equal(base64, UpdateSessionProtocol.EncodePalette(colors));
            Assert.False(UpdateSessionProtocol.TryDecodePalette(
                Convert.ToBase64String(Encoding.UTF8.GetBytes(json.Replace("#ABCDEF", "red"))),
                out _));
        }

        [Fact]
        public async Task NetFramework_setup_can_open_and_signal_its_scoped_session()
        {
            string id = UpdateSessionProtocol.NewId();
            using (UpdateSessionChannels host = UpdateSessionProtocol.CreateHost(id))
            using (UpdateSessionChannels setup = UpdateSessionProtocol.OpenSetup(id))
            {
                setup.SignalVisible();
                Assert.True(await host.WaitVisibleAsync(TimeSpan.FromSeconds(1)));
                host.SignalProceed();
                Assert.True(await setup.WaitProceedAsync(TimeSpan.FromSeconds(1)));
                setup.SignalAccepted();
                Assert.True(await host.WaitAcceptedAsync(TimeSpan.FromSeconds(1)));
                Assert.False(await setup.WaitReadyAsync(TimeSpan.FromMilliseconds(10)));
                Assert.True(UpdateSessionProtocol.TrySignalReady(id));
                Assert.True(await setup.WaitReadyAsync(TimeSpan.FromSeconds(1)));
            }
        }
    }
}
