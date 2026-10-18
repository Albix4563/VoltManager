using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class InstallTransactionSnapshotTests
    {
        [Fact]
        public void Failed_last_staged_rename_restores_entire_original_tree_and_all_staged_bytes()
        {
            using (var fixture = new SyntheticInstallFixture())
            {
                fixture.AddFile("destination/VoltManager.exe", "old executable \0 \u00ff");
                fixture.AddFile("destination/VoltManager.dll", "old library binary \0");
                fixture.AddFile("destination/VoltManager.exe.config", "<config>old</config>");
                fixture.AddFile("destination/wwwroot/index.html", "<html>old UI</html>");
                fixture.AddFile("destination/wwwroot/css/layout.css", "/* old stylesheet */");
                fixture.AddFile("destination/wwwroot/js/existing.js", "old JS");
                fixture.AddDirectory("destination/wwwroot/empty-preexisting/nested-empty");
                string personalBinary = fixture.AddFile("destination/UserData/nested/personal.bin",
                    new byte[] { 0x00, 0xFF, 0xAE, 0x19, 0x80, 0x02 });
                File.SetAttributes(personalBinary,
                    File.GetAttributes(personalBinary) | FileAttributes.ReadOnly);
                fixture.AddDirectory("destination/UserData/empty-private/nested");
                fixture.AddFile("destination/personal.txt", "private data");
                InstallManifest.Write(fixture.Destination, new[]
                {
                    "VoltManager.exe", "VoltManager.dll",
                    "VoltManager.exe.config", "wwwroot",
                });

                // The installed bytes must independently match the trusted ZIP
                // before a destructive replacement is attempted. New assets below
                // still exercise the rollback of directories not previously present.
                fixture.AddFile("staging/VoltManager.exe", "old executable \0 \u00ff");
                fixture.AddFile("staging/VoltManager.dll", "old library binary \0");
                fixture.AddFile("staging/VoltManager.exe.config", "<config>old</config>");
                fixture.AddFile("staging/a-new.dat", "new root asset");
                fixture.AddFile("staging/runtimes/win-x64/native/new-helper.dll", "native DLL");
                fixture.AddFile("staging/wwwroot/index.html", "<html>old UI</html>");
                fixture.AddFile("staging/wwwroot/css/layout.css", "/* old stylesheet */");
                fixture.AddFile("staging/wwwroot/js/existing.js", "old JS");
                fixture.AddFile("staging/wwwroot/js/new-feature.js", "brand new JS");
                fixture.AddDirectory("staging/wwwroot/empty-preexisting/nested-empty");
                fixture.AddDirectory("staging/wwwroot/new-empty-dir");
                string locked = fixture.AddFile("staging/z-last-locked.dat", "locked staged binary");

                string zip = Path.Combine(fixture.Root, "trusted-synthetic.zip");
                ZipFile.CreateFromDirectory(fixture.Staging, zip);

                // Include every directory (even empty), every file (including the install
                // marker), raw bytes, SHA-256 and basic filesystem attributes in the snapshot.
                IReadOnlyList<EntrySnapshot> originalInstall = SnapshotTree(fixture.Destination);
                IReadOnlyList<EntrySnapshot> originalStage = SnapshotTree(fixture.Staging);
                Assert.Contains(originalInstall, item => item.RelativePath == "wwwroot/empty-preexisting/nested-empty" && item.IsDirectory);
                Assert.Contains(originalInstall, item => item.RelativePath == "UserData/empty-private/nested" && item.IsDirectory);
                Assert.Contains(originalInstall, item => item.RelativePath == InstallManifest.FileName && !item.IsDirectory);
                Assert.Contains(originalStage, item => item.RelativePath == "wwwroot/new-empty-dir" && item.IsDirectory);
                Assert.Contains(originalStage, item => item.RelativePath == "wwwroot/js/new-feature.js" && !item.IsDirectory);
                Assert.Equal("z-last-locked.dat",
                    Directory.GetFileSystemEntries(fixture.Staging)
                        .Select(Path.GetFileName)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .Last());

                // The staged file permits SHA-256 preflight reads but denies FILE_SHARE_DELETE,
                // so Windows must refuse File.Move after the other staged entries have moved.
                using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    InstallManifest.ValidateStagedPayload(fixture.Staging, zip);
                    string blockedMoveTarget = Path.Combine(fixture.Root, "lock-probe-never-created.dat");
                    Assert.IsType<IOException>(Record.Exception(() => File.Move(locked, blockedMoveTarget)));
                    Assert.True(File.Exists(locked));
                    Assert.False(File.Exists(blockedMoveTarget));

                    var error = Record.Exception(() =>
                        InstallEngine.ReplaceInstallDirectoryContents(
                            fixture.Destination, fixture.Staging, fixture.Backup, zip));
                    Assert.IsType<IOException>(error);
                }

                AssertTreeIdentical(originalInstall, SnapshotTree(fixture.Destination), "installation");
                AssertTreeIdentical(originalStage, SnapshotTree(fixture.Staging), "staging");
                Assert.False(Directory.Exists(fixture.Backup),
                    "A completed rollback must not leave an empty backup behind.");
                Assert.False(File.Exists(fixture.Backup));
            }
        }

        private static IReadOnlyList<EntrySnapshot> SnapshotTree(string root)
        {
            Assert.True(Directory.Exists(root), "Expected snapshot root to exist: " + root);
            var snapshot = new List<EntrySnapshot>
            {
                EntrySnapshot.ForDirectory(".", File.GetAttributes(root)),
            };

            foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
                snapshot.Add(EntrySnapshot.ForDirectory(RelativePath(root, directory),
                    File.GetAttributes(directory)));

            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                byte[] bytes = File.ReadAllBytes(file);
                byte[] hash;
                using (var sha = SHA256.Create())
                    hash = sha.ComputeHash(bytes);
                snapshot.Add(EntrySnapshot.ForFile(RelativePath(root, file),
                    File.GetAttributes(file), bytes, BitConverter.ToString(hash)));
            }

            return snapshot.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
        }

        private static string RelativePath(string root, string fullPath)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            Assert.True(fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
                "Snapshot entry must remain inside the fixture root.");
            return fullPath.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/');
        }

        private static void AssertTreeIdentical(
            IReadOnlyList<EntrySnapshot> original, IReadOnlyList<EntrySnapshot> actual,
            string treeLabel)
        {
            Assert.Equal(original.Select(entry => entry.RelativePath),
                actual.Select(entry => entry.RelativePath));
            Assert.Equal(original.Count, actual.Count);
            for (int index = 0; index < original.Count; ++index)
            {
                EntrySnapshot before = original[index];
                EntrySnapshot after = actual[index];
                string context = treeLabel + "/" + before.RelativePath;
                Assert.True(before.IsDirectory == after.IsDirectory, "Entry type changed: " + context);
                Assert.True(before.Attributes == after.Attributes,
                    "Filesystem attributes changed: " + context);
                Assert.True(before.Sha256 == after.Sha256,
                    "SHA-256 content changed: " + context);
                Assert.Equal(before.Bytes, after.Bytes);
            }
        }

        private sealed class EntrySnapshot
        {
            public string RelativePath { get; private set; } = "";
            public bool IsDirectory { get; private set; }
            public FileAttributes Attributes { get; private set; }
            public byte[] Bytes { get; private set; } = Array.Empty<byte>();
            public string Sha256 { get; private set; } = "";

            public static EntrySnapshot ForDirectory(string relative, FileAttributes attributes)
                => new EntrySnapshot
                {
                    RelativePath = relative,
                    IsDirectory = true,
                    Attributes = attributes,
                };

            public static EntrySnapshot ForFile(
                string relative, FileAttributes attributes, byte[] bytes, string sha256)
                => new EntrySnapshot
                {
                    RelativePath = relative,
                    IsDirectory = false,
                    Attributes = attributes,
                    Bytes = bytes,
                    Sha256 = sha256,
                };
        }

        private sealed class SyntheticInstallFixture : IDisposable
        {
            public string Root { get; }
            public string Destination => Path.Combine(Root, "destination");
            public string Staging => Path.Combine(Root, "staging");
            public string Backup => Path.Combine(Root, "backup");

            public SyntheticInstallFixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "VoltManagerInstallSnapshotTests",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
            }

            public void AddDirectory(string relative)
                => Directory.CreateDirectory(Resolve(relative));

            public string AddFile(string relative, string content)
                => AddFile(relative, Encoding.UTF8.GetBytes(content));

            public string AddFile(string relative, byte[] bytes)
            {
                string path = Resolve(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                return path;
            }

            private string Resolve(string relative)
            {
                string path = Path.GetFullPath(Path.Combine(Root,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                string prefix = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Fixture path is outside isolated temporary root.");
                return path;
            }

            public void Dispose()
            {
                // Only the unique, self-created synthetic subtree is removed.
                if (Directory.Exists(Root))
                {
                    foreach (string file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                    {
                        FileAttributes attributes = File.GetAttributes(file);
                        if ((attributes & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                    Directory.Delete(Root, recursive: true);
                }
            }
        }
    }
}
