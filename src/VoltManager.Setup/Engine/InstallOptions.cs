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
        public bool LaunchAfterInstall { get; set; } = true;
    }
}
