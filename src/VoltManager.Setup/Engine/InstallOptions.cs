namespace VoltManager.Setup.Engine
{
    public class InstallOptions
    {
        public string InstallDir { get; set; } =
            System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
                "VoltManager");
        public bool CreateDesktopShortcut { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;
        public bool EnableWidgets { get; set; } = false;
        public System.Collections.Generic.HashSet<string> EnabledWidgetTypes { get; set; } =
            new System.Collections.Generic.HashSet<string> { "clock", "calendar", "usage", "temps", "power" };
        public bool LaunchAfterInstall { get; set; } = true;
    }
}
