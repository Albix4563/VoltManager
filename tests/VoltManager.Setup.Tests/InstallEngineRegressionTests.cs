using System.Reflection;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests;

public sealed class InstallEngineRegressionTests
{
    [Fact]
    public async Task Corrupt_payload_does_not_destroy_existing_installation()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "VoltManager");
        string zip = Path.Combine(root, "payload.zip");
        Directory.CreateDirectory(dest);
        string existing = Path.Combine(dest, "existing.txt");
        File.WriteAllText(existing, "keep");
        File.WriteAllBytes(zip, [1, 2, 3, 4]);

        try
        {
            MethodInfo method = typeof(InstallEngine).GetMethod(
                "ExtractPayloadAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            Task task = (Task)method.Invoke(null, new object?[] { dest, CancellationToken.None, zip })!;

            await Assert.ThrowsAnyAsync<Exception>(async () => await task);
            Assert.True(File.Exists(existing));
            Assert.Equal("keep", File.ReadAllText(existing));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
