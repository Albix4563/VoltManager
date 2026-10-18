using System;
using System.IO;
using System.Text;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class ManifestArchiveEdgeTests
    {
        private static readonly byte[] OldApp = { 0x00, 0xD3, 0x1A, 0xFF, 0x80 };
        private static readonly byte[] PrivateData = { 0x91, 0x00, 0xFF, 0x62, 0x08 };
        private static readonly byte[] StagedScript = { 0x10, 0x30, 0x50, 0x00, 0xEE };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Case_insensitive_duplicate_ZIP_file_or_directory_entries_are_rejected(bool duplicateDirectories)
        {
            using (var fixture = new TempFixture())
            {
                string stagedScript = fixture.WriteFile("staging/wwwroot/main.js", StagedScript);
                string zip = duplicateDirectories
                    ? fixture.CreateArchive(
                        ("wwwroot/", null),
                        ("WWWROOT/", null),
                        ("wwwroot/main.js", StagedScript))
                    : fixture.CreateArchive(
                        ("wwwroot/main.js", StagedScript),
                        ("WWWROOT/MAIN.JS", StagedScript));

                InvalidDataException rejection = Assert.Throws<InvalidDataException>(() =>
                    InstallManifest.ValidateStagedPayload(fixture.Staging, zip));

                Assert.Contains("duplicate", rejection.Message.ToLowerInvariant());
                Assert.Equal(StagedScript, File.ReadAllBytes(stagedScript));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ZIP_file_and_directory_collision_is_rejected_without_modifying_staging(bool explicitDirectory)
        {
            using (var fixture = new TempFixture())
            {
                string stagedScript = fixture.WriteFile("staging/wwwroot/main.js", StagedScript);
                string zip = explicitDirectory
                    ? fixture.CreateArchive(
                        ("conflict", StagedScript),
                        ("CONFLICT/", null))
                    : fixture.CreateArchive(
                        ("conflict", StagedScript),
                        ("CONFLICT/child.dll", StagedScript));

                InvalidDataException rejection = Assert.Throws<InvalidDataException>(() =>
                    InstallManifest.ValidateStagedPayload(fixture.Staging, zip));

                Assert.Contains("collision", rejection.Message.ToLowerInvariant());
                Assert.Equal(StagedScript, File.ReadAllBytes(stagedScript));
            }
        }

        [Theory]
        [InlineData("../private.txt")]
        [InlineData("wwwroot/../../private.txt")]
        [InlineData("/absolute.txt")]
        [InlineData("C:/Windows/System32/config.txt")]
        [InlineData("wwwroot\\..\\private.txt")]
        public void Unsafe_ZIP_entry_rejects_replacement_without_touching_existing_installation(string unsafeEntry)
        {
            using (var fixture = new TempFixture())
            {
                string existingApp = fixture.WriteFile("destination/VoltManager.dll", OldApp);
                string personalFile = fixture.WriteFile("destination/personal.txt", PrivateData);
                string stagedScript = fixture.WriteFile("staging/wwwroot/main.js", StagedScript);
                InstallManifest.Write(fixture.Destination, new[] { "VoltManager.dll" });
                string manifest = Path.Combine(fixture.Destination, InstallManifest.FileName);
                byte[] manifestBefore = File.ReadAllBytes(manifest);
                string zip = fixture.CreateArchive(
                    ("wwwroot/main.js", StagedScript),
                    (unsafeEntry, StagedScript));

                InvalidDataException rejection = Assert.Throws<InvalidDataException>(() =>
                    InstallEngine.ReplaceInstallDirectoryContents(
                        fixture.Destination, fixture.Staging, fixture.Backup, zip));

                Assert.True(rejection.Message.IndexOf("unsafe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            rejection.Message.IndexOf("separator", StringComparison.OrdinalIgnoreCase) >= 0);
                Assert.Equal(OldApp, File.ReadAllBytes(existingApp));
                Assert.Equal(PrivateData, File.ReadAllBytes(personalFile));
                Assert.Equal(StagedScript, File.ReadAllBytes(stagedScript));
                Assert.Equal(manifestBefore, File.ReadAllBytes(manifest));
                Assert.False(Directory.Exists(fixture.Backup));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Missing_declared_or_extra_nested_staged_file_rejects_replacement_without_mutation(bool addForeignFile)
        {
            using (var fixture = new TempFixture())
            {
                string installedApp = fixture.WriteFile("destination/VoltManager.dll", OldApp);
                string personalFile = fixture.WriteFile("destination/personal.txt", PrivateData);
                string stagedScript = fixture.WriteFile("staging/wwwroot/js/main.js", StagedScript);
                string extraFile = Path.Combine(fixture.Staging, "wwwroot", "js", "extra.js");
                if (addForeignFile)
                    fixture.WriteFile("staging/wwwroot/js/extra.js", PrivateData);

                InstallManifest.Write(fixture.Destination, new[] { "VoltManager.dll" });
                string manifest = Path.Combine(fixture.Destination, InstallManifest.FileName);
                byte[] markerBefore = File.ReadAllBytes(manifest);
                string zip = fixture.CreateArchive(
                    ("wwwroot/js/main.js", StagedScript),
                    addForeignFile ? ("wwwroot/js/", (byte[]?)null) : ("wwwroot/js/extra.js", PrivateData));

                InvalidDataException rejection = Assert.Throws<InvalidDataException>(() =>
                    InstallEngine.ReplaceInstallDirectoryContents(
                        fixture.Destination, fixture.Staging, fixture.Backup, zip));

                Assert.Contains(addForeignFile ? "unlisted staged file" : "missing files",
                    rejection.Message.ToLowerInvariant());
                Assert.Equal(OldApp, File.ReadAllBytes(installedApp));
                Assert.Equal(PrivateData, File.ReadAllBytes(personalFile));
                Assert.Equal(markerBefore, File.ReadAllBytes(manifest));
                Assert.Equal(StagedScript, File.ReadAllBytes(stagedScript));
                if (addForeignFile)
                    Assert.Equal(PrivateData, File.ReadAllBytes(extraFile));
                Assert.False(Directory.Exists(fixture.Backup));
            }
        }

        [Theory]
        [InlineData("unknown-personal.bin")]
        [InlineData(@"C:\Users\Public\unrelated.txt")]
        [InlineData(@"\\server\share\unrelated.txt")]
        [InlineData("/absolute.txt")]
        public void Forged_unknown_or_absolute_manifest_entry_preserves_installed_and_foreign_bytes(string hostileName)
        {
            using (var fixture = new TempFixture())
            {
                string installedApp = fixture.WriteFile("destination/VoltManager.dll", OldApp);
                string personalFile = fixture.WriteFile("destination/unknown-personal.bin", PrivateData);
                string marker = fixture.WriteFile("destination/" + InstallManifest.FileName,
                    Encoding.UTF8.GetBytes("{\"entries\":[\"VoltManager.dll\",\"" +
                        hostileName.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"]}"));
                byte[] originalMarker = File.ReadAllBytes(marker);
                string zip = fixture.CreateArchive(("VoltManager.dll", OldApp));

                var failures = InstallManifest.DeleteOwnedEntries(fixture.Destination, zip);

                Assert.NotEmpty(failures);
                Assert.Contains(failures, error => error.IndexOf("ownership verification failed", StringComparison.OrdinalIgnoreCase) >= 0);
                Assert.Equal(OldApp, File.ReadAllBytes(installedApp));
                Assert.Equal(PrivateData, File.ReadAllBytes(personalFile));
                Assert.Equal(originalMarker, File.ReadAllBytes(marker));
            }
        }

        private sealed class TempFixture : IDisposable
        {
            public string Root { get; }
            public string Destination => Path.Combine(Root, "destination");
            public string Staging => Path.Combine(Root, "staging");
            public string Backup => Path.Combine(Root, "backup");

            public TempFixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "VoltManagerManifestArchiveEdges", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
            }

            public string WriteFile(string relative, byte[] bytes)
            {
                string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                return path;
            }

            public string CreateArchive(params (string Name, byte[]? Content)[] entries)
            {
                string path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip");
                // Write minimal stored ZIP records directly so duplicate/invalid member names
                // can be represented without adding a project-wide compression reference.
                using (var stream = File.Create(path))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    var central = new System.Collections.Generic.List<(byte[] Name, byte[] Content, uint Crc, uint Offset, bool Directory)>();
                    foreach (var item in entries)
                    {
                        byte[] name = Encoding.UTF8.GetBytes(item.Name);
                        byte[] content = item.Content ?? Array.Empty<byte>();
                        uint offset = checked((uint)stream.Position);
                        uint crc = Crc32(content);
                        writer.Write(0x04034b50u);
                        writer.Write((ushort)20); // minimum ZIP version
                        writer.Write((ushort)0x0800); // UTF-8 names
                        writer.Write((ushort)0); // stored
                        writer.Write((ushort)0); // time
                        writer.Write((ushort)0); // date
                        writer.Write(crc);
                        writer.Write(checked((uint)content.Length));
                        writer.Write(checked((uint)content.Length));
                        writer.Write(checked((ushort)name.Length));
                        writer.Write((ushort)0); // extra length
                        writer.Write(name);
                        writer.Write(content);
                        central.Add((name, content, crc, offset, item.Content == null));
                    }

                    uint centralStart = checked((uint)stream.Position);
                    foreach (var item in central)
                    {
                        writer.Write(0x02014b50u);
                        writer.Write((ushort)20); // creator version
                        writer.Write((ushort)20); // extractor version
                        writer.Write((ushort)0x0800);
                        writer.Write((ushort)0);
                        writer.Write((ushort)0);
                        writer.Write((ushort)0);
                        writer.Write(item.Crc);
                        writer.Write(checked((uint)item.Content.Length));
                        writer.Write(checked((uint)item.Content.Length));
                        writer.Write(checked((ushort)item.Name.Length));
                        writer.Write((ushort)0); // extra
                        writer.Write((ushort)0); // comment
                        writer.Write((ushort)0); // disk start
                        writer.Write((ushort)0); // internal attributes
                        writer.Write(item.Directory ? 0x10u : 0u); // external attributes
                        writer.Write(item.Offset);
                        writer.Write(item.Name);
                    }

                    uint centralEnd = checked((uint)stream.Position);
                    writer.Write(0x06054b50u);
                    writer.Write((ushort)0); // disk
                    writer.Write((ushort)0); // central disk
                    writer.Write(checked((ushort)central.Count));
                    writer.Write(checked((ushort)central.Count));
                    writer.Write(centralEnd - centralStart);
                    writer.Write(centralStart);
                    writer.Write((ushort)0); // comment
                }
                return path;
            }

            private static uint Crc32(byte[] data)
            {
                uint crc = 0xffffffffu;
                foreach (byte b in data)
                {
                    crc ^= b;
                    for (int bit = 0; bit < 8; bit++)
                        crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320u;
                }
                return ~crc;
            }

            public void Dispose()
            {
                // This unique test-owned directory has no reparse/symlink fixtures.
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
        }
    }
}
