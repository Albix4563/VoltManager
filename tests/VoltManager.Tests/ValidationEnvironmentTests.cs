using System.IO;
using VoltManager.Services;

namespace VoltManager.Tests;

[Collection("RuntimeServiceValidationEnvironment")]
public sealed class ValidationEnvironmentTests
{
    [Fact]
    public void Test_build_compiles_validation_hooks_and_honors_environment()
    {
        Assert.True(ValidationEnvironment.HooksCompiledIn);
        string root = Path.Combine(Path.GetTempPath(), "VoltManager.Validation", Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable(ValidationEnvironment.RootVariable);
        string? previousPower = Environment.GetEnvironmentVariable(ValidationEnvironment.SuppressPowerVariable);
        try
        {
            Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, root);
            Environment.SetEnvironmentVariable(ValidationEnvironment.SuppressPowerVariable, "1");

            Assert.True(ValidationEnvironment.IsActive);
            Assert.Equal(Path.GetFullPath(root), ValidationEnvironment.ApplicationDataRoot);
            Assert.True(ValidationEnvironment.SuppressPowerChanges);
            Assert.NotEqual("VoltManager_Test", ValidationEnvironment.NamedObject("VoltManager_Test"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, previousRoot);
            Environment.SetEnvironmentVariable(ValidationEnvironment.SuppressPowerVariable, previousPower);
        }
    }

    [Fact]
    public void ResolveRoot_with_hooks_disabled_does_not_read_environment()
    {
        int reads = 0;
        string root = ValidationEnvironment.ResolveRoot(false, _ =>
        {
            reads++;
            return @"C:\AttackerControlled";
        });

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), root);
        Assert.Equal(0, reads);
    }
}
