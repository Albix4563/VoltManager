using System;

namespace VoltManager.Setup.Engine
{
    public enum SetupMode { Wizard, Silent, Update, Uninstall }

    public class SetupArgs
    {
        public SetupMode Mode { get; }
        public int WaitPid { get; }
        public string TargetDir { get; }
        public bool SilentUninstall { get; }

        public SetupArgs(SetupMode mode, int waitPid = 0, string targetDir = "", bool silentUninstall = false)
        {
            Mode = mode;
            WaitPid = waitPid;
            TargetDir = targetDir ?? "";
            SilentUninstall = silentUninstall;
        }

        public static SetupArgs Parse(string[] args)
        {
            if (args == null || args.Length == 0) return new SetupArgs(SetupMode.Wizard);

            bool silent = HasFlag(args, "/SILENT", "/VERYSILENT", "/silent", "/verysilent");
            bool update = HasFlag(args, "/update");
            bool uninstall = HasFlag(args, "/uninstall");

            if (update)
            {
                int pid = GetIntParam(args, "--pid");
                string target = GetParam(args, "--target");
                return new SetupArgs(SetupMode.Update, pid, target);
            }

            if (uninstall)
            {
                string target = GetParam(args, "--target");
                return new SetupArgs(SetupMode.Uninstall, 0, target, silent);
            }

            if (silent) return new SetupArgs(SetupMode.Silent);
            return new SetupArgs(SetupMode.Wizard);
        }

        private static bool HasFlag(string[] args, params string[] flags)
        {
            foreach (var a in args)
                foreach (var f in flags)
                    if (string.Equals(a, f, StringComparison.OrdinalIgnoreCase))
                        return true;
            return false;
        }

        private static string GetParam(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return "";
        }

        private static int GetIntParam(string[] args, string name)
        {
            var s = GetParam(args, name);
            return int.TryParse(s, out var n) ? n : 0;
        }
    }
}
