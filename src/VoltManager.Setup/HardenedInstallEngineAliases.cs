namespace VoltManager.Setup
{
    // App.xaml.cs resolves this local type before the imported Engine.InstallEngine.
    // It preserves install/update behavior through inheritance while selecting the
    // hardened UninstallAsync implementation for silent uninstall flows.
    internal sealed class InstallEngine : Engine.HardenedInstallEngine
    {
    }
}

namespace VoltManager.Setup.Windows
{
    // SetupWindow.xaml.cs likewise resolves the type in its own namespace first,
    // so interactive uninstall uses the same hardened lifecycle without invasive
    // edits to the existing installer UI code.
    internal sealed class InstallEngine : Engine.HardenedInstallEngine
    {
    }
}
