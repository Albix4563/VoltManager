using System;

namespace VoltManager.Services;

/// <summary>
/// Contract shared between VoltManager (elevated) and the non-elevated
/// jump-list helper VoltManagerPlanSwitch.exe (the file is compile-linked
/// into both projects). A command is one named auto-reset event per key;
/// the helper signals it, the app applies the plan.
/// </summary>
public static class RemoteCommandProtocol
{
    public const string PowerSaverKey = "powerSaver";
    public const string BalancedKey = "balanced";
    public const string PerformanceKey = "performance";
    public const string AutoKey = "auto";

    public static readonly string[] AllKeys =
    {
        PowerSaverKey, BalancedKey, PerformanceKey, AutoKey,
    };

    public const string PlanArgName = "--plan";

    public static string EventName(string key) => "VoltManager_PlanCmd_" + key;

    public static bool IsValidKey(string? key)
        => key != null && Array.IndexOf(AllKeys, key) >= 0;

    /// <summary>Extracts the value of a "--plan &lt;key&gt;" argument pair, or null.</summary>
    public static string? ParsePlanArg(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], PlanArgName, StringComparison.OrdinalIgnoreCase)
                && IsValidKey(args[i + 1]))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
