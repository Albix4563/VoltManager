using System;

namespace VoltManager.Services;

/// <summary>
/// Contract shared between VoltManager (elevated) and the non-elevated
/// jump-list helper VoltManagerPlanSwitch.exe (the file is compile-linked
/// into both projects). A command is one named auto-reset event per key;
/// the helper signals it, the app applies the requested action.
/// </summary>
public static class RemoteCommandProtocol
{
    public const string PowerSaverKey = "powerSaver";
    public const string BalancedKey = "balanced";
    public const string PerformanceKey = "performance";
    public const string AutoKey = "auto";
    public const string KeepAwakeOnKey = "keepAwakeOn";
    public const string KeepAwakeOffKey = "keepAwakeOff";
    public const string KeepAwakeToggleKey = "keepAwakeToggle";
    public const string Shutdown30Key = "scheduleShutdown30";
    public const string Shutdown60Key = "scheduleShutdown60";
    public const string Sleep30Key = "scheduleSleep30";
    public const string Sleep60Key = "scheduleSleep60";
    public const string OpenSchedulerKey = "openScheduler";

    public static readonly string[] PlanKeys =
    {
        PowerSaverKey, BalancedKey, PerformanceKey, AutoKey,
    };

    public static readonly string[] AllKeys =
    {
        PowerSaverKey, BalancedKey, PerformanceKey, AutoKey,
        KeepAwakeOnKey, KeepAwakeOffKey, KeepAwakeToggleKey,
        Shutdown30Key, Shutdown60Key, Sleep30Key, Sleep60Key, OpenSchedulerKey,
    };

    public const string PlanArgName = "--plan";
    public const string CommandArgName = "--command";

    public static string EventName(string key) => "VoltManager_PlanCmd_" + key;

    public static bool IsValidKey(string? key)
        => key != null && Array.IndexOf(AllKeys, key) >= 0;

    public static bool IsPlanKey(string? key)
        => key != null && Array.IndexOf(PlanKeys, key) >= 0;

    /// <summary>Extracts the value of a "--plan &lt;key&gt;" argument pair, or null.</summary>
    public static string? ParsePlanArg(string[] args)
    {
        string? key = ParseArg(args, PlanArgName);
        return IsPlanKey(key) ? key : null;
    }

    /// <summary>Extracts either "--plan &lt;key&gt;" or "--command &lt;key&gt;", or null.</summary>
    public static string? ParseCommandArg(string[] args)
    {
        string? plan = ParsePlanArg(args);
        if (plan != null) return plan;

        string? command = ParseArg(args, CommandArgName);
        return IsValidKey(command) ? command : null;
    }

    private static string? ParseArg(string[] args, string argName)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], argName, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
