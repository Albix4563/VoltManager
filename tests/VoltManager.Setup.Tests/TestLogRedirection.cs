using System.IO;
using System.Runtime.CompilerServices;
using VoltManager.Setup.Engine;

namespace System.Runtime.CompilerServices
{
    // net48 lacks the attribute; the C# compiler only needs its name.
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}

namespace VoltManager.Setup.Tests
{
    internal static class TestLogRedirection
    {
        // Setup tests exercise failure paths that log: keep them out of the real %APPDATA%.
#pragma warning disable CA2255 // test assembly: a module initializer is the only hook before any test runs.
        [ModuleInitializer]
        internal static void Initialize()
            => SetupUpdateLog.FilePath = Path.Combine(
                Path.GetTempPath(), "VoltManagerSetupTests", "setup-update.log");
#pragma warning restore CA2255
    }
}
