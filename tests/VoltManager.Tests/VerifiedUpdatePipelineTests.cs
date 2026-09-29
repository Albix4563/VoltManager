using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class VerifiedUpdatePipelineTests
{
    private const string InstallerUrl = "https://github.com/Albix4563/VoltManager/releases/download/v2.0.0/VoltManagerSetup.exe";
    private const string ChecksumsUrl = "https://github.com/Albix4563/VoltManager/releases/download/v2.0.0/SHA256SUMS";
    private const string AssetName = "VoltManagerSetup.exe";

    [Fact]
    public void Updates_directory_owned_by_another_account_is_not_trusted()
    {
        Assert.True(SystemUpdateFileSystem.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.True(SystemUpdateFileSystem.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        // The interactive user could rewrite the DACL unelevated: ownership must move to Administrators.
        Assert.False(SystemUpdateFileSystem.IsTrustedOwner(WindowsIdentity.GetCurrent().User));
        Assert.False(SystemUpdateFileSystem.IsTrustedOwner(new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1001")));
        Assert.False(SystemUpdateFileSystem.IsTrustedOwner(null));
    }

    [Fact]
    public void Installer_handle_kept_until_launch_is_read_only_and_lets_the_loader_open_the_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vm-reopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            IUpdateFileSystem fileSystem = new SystemUpdateFileSystem();
            string path = Path.Combine(dir, "setup.exe");
            Stream written = fileSystem.CreateInstallerFile(path);
            written.Write([1, 2, 3]);

            using Stream held = fileSystem.ReopenInstallerForLaunch(path, written);

            Assert.False(held.CanWrite);
            // Same share mode the image loader uses: fails if our handle still had write access.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) { }
            Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Mismatching_hash_is_rejected_logged_and_cleaned_before_launch()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        var fs = new TestUpdateFileSystem();
        var errors = new List<string>();
        using var http = CreateHttp(payload, new string('0', 64) + "  " + AssetName + "\n");
        var downloader = new UpdateDownloadClient(http, fs, TimeSpan.FromSeconds(1), errors.Add);
        int launches = 0;
        var launcher = new UpdateInstallerLauncher(startProcess: _ =>
        {
            launches++;
            return Process.GetCurrentProcess();
        });

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            using VerifiedUpdateDownload download = await downloader.DownloadAsync(
                InstallerUrl, AssetName, ChecksumsUrl, null, CancellationToken.None);
            launcher.Launch(download, "/update");
        });

        Assert.Contains("non corrispondente", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(errors, message => message.Contains("non corrispondente", StringComparison.OrdinalIgnoreCase));
        Assert.False(fs.HasRunDirectories);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task Missing_sha256sums_asset_is_rejected_without_creating_run_directory()
    {
        byte[] payload = [1, 2, 3];
        var fs = new TestUpdateFileSystem();
        var errors = new List<string>();
        using var http = CreateHttp(payload, string.Empty);
        var downloader = new UpdateDownloadClient(http, fs, TimeSpan.FromSeconds(1), errors.Add);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.DownloadAsync(InstallerUrl, AssetName, null, null, CancellationToken.None));

        Assert.Contains("SHA256SUMS mancante", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(errors, message => message.Contains("SHA256SUMS mancante", StringComparison.OrdinalIgnoreCase));
        Assert.False(fs.HasRunDirectories);
    }

    [Fact]
    public async Task Missing_installer_entry_is_rejected_and_run_directory_is_removed()
    {
        byte[] payload = [7, 8, 9];
        string hash = Hash(payload);
        var fs = new TestUpdateFileSystem();
        using var http = CreateHttp(payload, hash + "  OtherSetup.exe\n");
        var downloader = new UpdateDownloadClient(http, fs, TimeSpan.FromSeconds(1));

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.DownloadAsync(InstallerUrl, AssetName, ChecksumsUrl, null, CancellationToken.None));

        Assert.Contains("mancante", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(fs.HasRunDirectories);
    }

    [Fact]
    public async Task Matching_hash_launches_random_programdata_path()
    {
        byte[] payload = Encoding.UTF8.GetBytes("verified installer payload");
        var fs = new TestUpdateFileSystem();
        using var http = CreateHttp(payload, Hash(payload) + "  " + AssetName + "\n");
        var downloader = new UpdateDownloadClient(http, fs, TimeSpan.FromSeconds(1));
        using VerifiedUpdateDownload download = await downloader.DownloadAsync(
            InstallerUrl, AssetName, ChecksumsUrl, null, CancellationToken.None);
        ProcessStartInfo? observed = null;
        var launcher = new UpdateInstallerLauncher(startProcess: info =>
        {
            observed = info;
            return Process.GetCurrentProcess();
        });

        launcher.Launch(download, "/update --pid 123 --lang it");

        Assert.NotNull(observed);
        Assert.Equal(download.Path, observed.FileName);
        Assert.StartsWith(fs.UpdatesBasePath, download.Path, StringComparison.OrdinalIgnoreCase);
        Assert.False(download.Path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
        Assert.Matches(@"^VoltManagerSetup-[0-9a-f]{8}\.exe$", Path.GetFileName(download.Path));
        Assert.Contains(fs.UpdatesBasePath, fs.ProtectedDirectories, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, fs.ProtectedDirectories.Count);
    }

    [Fact]
    public async Task Launcher_rechecks_hash_and_rejects_tampered_open_file()
    {
        byte[] payload = [10, 20, 30, 40];
        var fs = new TestUpdateFileSystem();
        using var http = CreateHttp(payload, Hash(payload) + "  " + AssetName + "\n");
        var downloader = new UpdateDownloadClient(http, fs, TimeSpan.FromSeconds(1));
        VerifiedUpdateDownload download = await downloader.DownloadAsync(
            InstallerUrl, AssetName, ChecksumsUrl, null, CancellationToken.None);
        MemoryStream stream = fs.GetInstallerStream();
        stream.Position = 0;
        stream.WriteByte(0xFF);
        stream.Flush();
        int starts = 0;
        var launcher = new UpdateInstallerLauncher(startProcess: _ =>
        {
            starts++;
            return Process.GetCurrentProcess();
        });

        Assert.Throws<InvalidDataException>(() => launcher.Launch(download, "/update"));

        Assert.Equal(0, starts);
        Assert.False(fs.HasRunDirectories);
    }

    [Fact]
    public async Task Acl_failure_aborts_without_falling_back_to_temp()
    {
        byte[] payload = [1];
        var fs = new TestUpdateFileSystem { FailAcl = true };
        using var http = CreateHttp(payload, Hash(payload) + "  " + AssetName + "\n");
        var downloader = new UpdateDownloadClient(http, fs, TimeSpan.FromSeconds(1));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            downloader.DownloadAsync(InstallerUrl, AssetName, ChecksumsUrl, null, CancellationToken.None));

        Assert.Contains("proteggere", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(fs.HasRunDirectories);
    }

    [Fact]
    public async Task Redirected_updates_root_is_rejected_before_cleanup_or_download()
    {
        byte[] payload = [1, 2];
        var fs = new TestUpdateFileSystem();
        fs.CreateDirectory(fs.UpdatesBasePath);
        string stale = Path.Combine(fs.UpdatesBasePath, "stale");
        fs.CreateDirectory(stale);
        fs.SetDirectoryCreationTimeUtc(stale, DateTime.UtcNow.AddDays(-3));
        fs.ReparsePoints.Add(fs.UpdatesBasePath);
        using var http = CreateHttp(payload, Hash(payload) + "  " + AssetName + "\n");
        var downloader = new UpdateDownloadClient(http, fs, TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            downloader.DownloadAsync(InstallerUrl, AssetName, ChecksumsUrl, null, CancellationToken.None));

        Assert.Empty(fs.DeletedDirectories);
        Assert.Empty(fs.ProtectedDirectories);
        Assert.Null(fs.LastInstallerPath);
    }

    [Fact]
    public async Task Stale_cleanup_waits_for_a_protected_root_and_skips_reparse_points()
    {
        byte[] payload = [3, 4];
        var fs = new TestUpdateFileSystem { FailAcl = true };
        fs.CreateDirectory(fs.UpdatesBasePath);
        string stale = Path.Combine(fs.UpdatesBasePath, "stale");
        string link = Path.Combine(fs.UpdatesBasePath, "link");
        fs.CreateDirectory(stale);
        fs.CreateDirectory(link);
        fs.SetDirectoryCreationTimeUtc(stale, DateTime.UtcNow.AddDays(-3));
        fs.SetDirectoryCreationTimeUtc(link, DateTime.UtcNow.AddDays(-3));
        fs.ReparsePoints.Add(link);
        using var http = CreateHttp(payload, Hash(payload) + "  " + AssetName + "\n");
        var downloader = new UpdateDownloadClient(http, fs, TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            downloader.DownloadAsync(InstallerUrl, AssetName, ChecksumsUrl, null, CancellationToken.None));
        Assert.Empty(fs.DeletedDirectories);

        fs.FailAcl = false;
        using (await downloader.DownloadAsync(InstallerUrl, AssetName, ChecksumsUrl, null, CancellationToken.None)) { }

        Assert.Contains(stale, fs.DeletedDirectories, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(link, fs.DeletedDirectories, StringComparer.OrdinalIgnoreCase);
    }

    private static HttpClient CreateHttp(byte[] installer, string checksums)
        => new(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/SHA256SUMS", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(checksums, Encoding.UTF8, "text/plain"),
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(installer),
            };
        }));

    private static string Hash(byte[] payload)
        => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _send;
        internal StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_send(request));
    }
}
