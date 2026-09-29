using System;
using System.Collections.Generic;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class SettingsJsonUpdaterTests
    {
        [Fact]
        public void Widgets_operation_preserves_unknown_top_level_keys_and_replaces_widget_state()
        {
            var root = SettingsJsonUpdater.ParseRootOrEmpty(
                "{\"unknown\":{\"keep\":42},\"widgets\":{\"legacy\":true}}",
                out bool malformed);

            SettingsJsonUpdater.SetWidgetsState(
                root,
                masterEnabled: true,
                new HashSet<string>(new[] { "clock", "power" }, StringComparer.OrdinalIgnoreCase));
            root = Reparse(root);

            Assert.False(malformed);
            var unknown = Assert.IsType<Dictionary<string, object?>>(root["unknown"]);
            Assert.Equal(42, unknown["keep"]);
            var widgets = Assert.IsType<Dictionary<string, object?>>(root["widgets"]);
            Assert.Equal(true, widgets["enabled"]);
            var items = Assert.IsType<object?[]>(widgets["items"]);
            Assert.Equal(6, items.Length);
            Assert.Equal(true, Assert.IsType<Dictionary<string, object?>>(items[0])["enabled"]);
            Assert.Equal(false, Assert.IsType<Dictionary<string, object?>>(items[1])["enabled"]);
            Assert.Equal(true, Assert.IsType<Dictionary<string, object?>>(items[4])["enabled"]);
            Assert.False(widgets.ContainsKey("legacy"));
        }

        [Fact]
        public void Update_channel_operation_preserves_unknown_auto_update_properties()
        {
            var root = SettingsJsonUpdater.ParseRootOrEmpty(
                "{\"unknown\":1,\"autoUpdates\":{\"enabled\":false,\"custom\":\"keep\",\"updateChannel\":\"stable\"}}",
                out bool malformed);

            SettingsJsonUpdater.SetUpdateChannelState(root, "preview");
            root = Reparse(root);

            Assert.False(malformed);
            Assert.Equal(1, root["unknown"]);
            var autoUpdates = Assert.IsType<Dictionary<string, object?>>(root["autoUpdates"]);
            Assert.Equal(false, autoUpdates["enabled"]);
            Assert.Equal("keep", autoUpdates["custom"]);
            Assert.Equal("preview", autoUpdates["updateChannel"]);
        }

        [Fact]
        public void Missing_auto_updates_gets_existing_defaults()
        {
            var root = new Dictionary<string, object?> { ["unknown"] = true };

            SettingsJsonUpdater.SetUpdateChannelState(root, "stable");
            root = Reparse(root);

            var autoUpdates = Assert.IsType<Dictionary<string, object?>>(root["autoUpdates"]);
            Assert.Equal(true, autoUpdates["enabled"]);
            Assert.Equal(true, autoUpdates["silentInstallEnabled"]);
            Assert.Equal("stable", autoUpdates["updateChannel"]);
            Assert.Equal(30, autoUpdates["intervalMinutes"]);
            Assert.True(autoUpdates.ContainsKey("snoozedUntilUtc"));
            Assert.Null(autoUpdates["snoozedUntilUtc"]);
            Assert.Null(autoUpdates["skippedVersion"]);
            Assert.Equal(true, root["unknown"]);
        }

        [Fact]
        public void Existing_non_object_auto_updates_is_left_unchanged()
        {
            var root = new Dictionary<string, object?> { ["autoUpdates"] = "legacy" };

            SettingsJsonUpdater.SetUpdateChannelState(root, "preview");

            Assert.Equal("legacy", root["autoUpdates"]);
        }

        [Theory]
        [InlineData("{ malformed")]
        [InlineData("[1,2]")]
        [InlineData("")]
        public void Malformed_settings_fall_back_to_empty_object(string json)
        {
            var root = SettingsJsonUpdater.ParseRootOrEmpty(json, out bool malformed);

            Assert.True(malformed);
            Assert.Empty(root);

            SettingsJsonUpdater.SetWidgetsState(root, true, new HashSet<string> { "clock" });
            SettingsJsonUpdater.SetUpdateChannelState(root, "preview");
            string serialized = SettingsJsonUpdater.Serialize(root);

            Assert.Contains("\"widgets\"", serialized);
            Assert.Contains("\"autoUpdates\"", serialized);
        }

        [Fact]
        public void Install_manifest_round_trips_safe_entry_names_only()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vm-manifest-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                InstallManifest.Write(dir, new[] { "VoltManager.exe", "wwwroot", "..", "sub\file.dll" });

                var entries = InstallManifest.ReadOwnedEntries(dir);

                Assert.Equal(new[] { "VoltManager.exe", "wwwroot" }, entries);
            }
            finally
            {
                System.IO.Directory.Delete(dir, true);
            }
        }

        private static Dictionary<string, object?> Reparse(Dictionary<string, object?> root)
        {
            var reparsed = SettingsJsonUpdater.ParseRootOrEmpty(SettingsJsonUpdater.Serialize(root), out bool malformed);
            Assert.False(malformed);
            return reparsed;
        }
    }
}
