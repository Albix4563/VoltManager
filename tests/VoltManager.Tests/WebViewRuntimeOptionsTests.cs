using System.IO;
using VoltManager.Services;

namespace VoltManager.Tests;

[Collection("RuntimeServiceValidationEnvironment")]
public sealed class WebViewRuntimeOptionsTests
{
    [Fact]
    public void RendererVariant_UsesHardwareInProduction_AndKeepsValidationOverrides()
    {
        string? previousRoot = Environment.GetEnvironmentVariable(ValidationEnvironment.RootVariable);
        string? previousRenderer = Environment.GetEnvironmentVariable(ValidationEnvironment.RendererVariable);

        try
        {
            Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, null);
            Environment.SetEnvironmentVariable(ValidationEnvironment.RendererVariable, "swiftshader");

            Assert.False(ValidationEnvironment.IsActive);
            Assert.Equal(WebViewRendererVariant.HardwareDefault, ValidationEnvironment.RendererVariant);
            string productionArguments = WebViewRuntimeOptions.BrowserArguments(ValidationEnvironment.RendererVariant);
            Assert.DoesNotContain("--enable-low-end-device-mode", productionArguments, StringComparison.Ordinal);
            Assert.DoesNotContain("--force-gpu-mem-available-mb", productionArguments, StringComparison.Ordinal);

            Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, Path.GetTempPath());
            Environment.SetEnvironmentVariable(ValidationEnvironment.RendererVariable, null);
            Assert.Equal(WebViewRendererVariant.SwiftShader, ValidationEnvironment.RendererVariant);
            string validationArguments = WebViewRuntimeOptions.BrowserArguments(ValidationEnvironment.RendererVariant);
            Assert.DoesNotContain("--enable-low-end-device-mode", validationArguments, StringComparison.Ordinal);
            Assert.DoesNotContain("--force-gpu-mem-available-mb", validationArguments, StringComparison.Ordinal);
            Assert.Equal(productionArguments + " --use-angle=swiftshader --use-gl=angle", validationArguments);

            Environment.SetEnvironmentVariable(ValidationEnvironment.RendererVariable, "hardware");
            Assert.Equal(WebViewRendererVariant.HardwareDefault, ValidationEnvironment.RendererVariant);
            Assert.Equal(productionArguments, WebViewRuntimeOptions.BrowserArguments(ValidationEnvironment.RendererVariant));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, previousRoot);
            Environment.SetEnvironmentVariable(ValidationEnvironment.RendererVariable, previousRenderer);
        }
    }
}
