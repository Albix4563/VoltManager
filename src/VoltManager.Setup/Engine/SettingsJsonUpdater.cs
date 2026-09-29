using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace VoltManager.Setup.Engine
{
    // Uses the .NET Framework JavaScriptSerializer: the setup ships as a single exe,
    // so NuGet JSON libraries (and their dependency DLLs) would not be deployed with it.
    internal static class SettingsJsonUpdater
    {
        private static readonly string[] WidgetTypes = { "clock", "calendar", "usage", "temps", "power", "plans" };

        internal static JavaScriptSerializer CreateSerializer()
            => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        internal static Dictionary<string, object?> ParseRootOrEmpty(string json, out bool malformed)
        {
            try
            {
                if (CreateSerializer().DeserializeObject(json) is Dictionary<string, object?> root)
                {
                    malformed = false;
                    return root;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            malformed = true;
            return new Dictionary<string, object?>();
        }

        internal static void SetWidgetsState(Dictionary<string, object?> root, bool masterEnabled, HashSet<string>? enabledTypes)
        {
            var selected = enabledTypes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool any = WidgetTypes.Any(selected.Contains);
            var items = new List<object?>();
            foreach (string type in WidgetTypes)
            {
                items.Add(new Dictionary<string, object?>
                {
                    ["type"] = type,
                    ["enabled"] = selected.Contains(type),
                    ["pinned"] = false,
                });
            }

            root["widgets"] = new Dictionary<string, object?>
            {
                ["enabled"] = masterEnabled && any,
                ["items"] = items,
            };
        }

        internal static void SetUpdateChannelState(Dictionary<string, object?> root, string channel)
        {
            if (!root.TryGetValue("autoUpdates", out object? existing))
            {
                root["autoUpdates"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["silentInstallEnabled"] = true,
                    ["updateChannel"] = channel,
                    ["intervalMinutes"] = 30,
                    ["snoozedUntilUtc"] = null,
                    ["skippedVersion"] = null,
                };
                return;
            }

            if (existing is Dictionary<string, object?> autoUpdates)
                autoUpdates["updateChannel"] = channel;
        }

        internal static string Serialize(Dictionary<string, object?> root)
            => CreateSerializer().Serialize(root);
    }
}
