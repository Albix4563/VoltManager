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
            var fileSystem = new SystemUpdateFileSystem();
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
    public async Task Interactive_launcher_waits_for_acceptance_and_preserves_staging_after_ack()
    {
        var download = UpdateTestHelpers.CreateVerifiedDownload();
        download.SupportsInteractiveUpdate = true;
        ProcessStartInfo? observed = null;
        var launcher = new UpdateInstallerLauncher(startProcess: info =>
        {
            observed = info;
            string[] args = info.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string id = args[Array.IndexOf(args, UpdateSessionProtocol.SessionArgument) + 1].Trim('"');
            using UpdateSessionChannels setup = UpdateSessionProtocol.OpenSetup(id);
            setup.SignalVisible();
            setup.SignalAccepted();
            return Process.GetCurrentProcess();
        });

        await launcher.LaunchOrLegacyAsync(download, "/update --pid 123 --lang it", null);

        Assert.NotNull(observed);
        Assert.Contains(UpdateSessionProtocol.SessionArgument, observed.Arguments);
        Assert.Throws<ObjectDisposedException>(() => download.VerifyOrThrow());
    }

    [Fact]
    public async Task Interactive_launcher_aborts_when_setup_does_not_accept_after_proceed()
    {
        var fs = new TestUpdateFileSystem();
        using VerifiedUpdateDownload download = UpdateTestHelpers.CreateVerifiedDownload(fs);
        download.SupportsInteractiveUpdate = true;
        UpdateSessionChannels? setup = null;
        using var cts = new CancellationTokenSource();
        var launcher = new UpdateInstallerLauncher(startProcess: info =>
        {
            string[] args = info.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            setup = UpdateSessionProtocol.OpenSetup(args[Array.IndexOf(args, UpdateSessionProtocol.SessionArgument) + 1].Trim('"'));
            setup.SignalVisible();
            return Process.GetCurrentProcess();
        });

        Task update = launcher.LaunchVerifiedAsync(download, "/update", null, cts.Token);
        Assert.NotNull(setup);
        Assert.True(await setup.WaitProceedAsync(TimeSpan.FromSeconds(5)));
        Assert.False(update.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        Assert.True(setup.IsAborted);
        Assert.False(fs.HasRunDirectories);
        setup.Dispose();
    }

    [Fact]
    public async Task Interactive_launcher_rejects_setup_abort_before_acceptance()
    {
        using VerifiedUpdateDownload download = UpdateTestHelpers.CreateVerifiedDownload();
        download.SupportsInteractiveUpdate = true;
        UpdateSessionChannels? setup = null;
        var launcher = new UpdateInstallerLauncher(startProcess: info =>
        {
            string[] args = info.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            setup = UpdateSessionProtocol.OpenSetup(args[Array.IndexOf(args, UpdateSessionProtocol.SessionArgument) + 1].Trim('"'));
            setup.SignalVisible();
            setup.SignalAbort();
            return Process.GetCurrentProcess();
        });

        await Assert.ThrowsAnyAsync<Exception>(() =>
            launcher.LaunchVerifiedAsync(download, "/update", null));
        Assert.NotNull(setup);
        Assert.True(setup.IsAborted);
        setup.Dispose();
    }

    [Fact]
    public async Task Existing_release_still_uses_legacy_update_arguments()
    {
        using VerifiedUpdateDownload download = UpdateTestHelpers.CreateVerifiedDownload();
        ProcessStartInfo? observed = null;
        var launcher = new UpdateInstallerLauncher(startProcess: info =>
        {
            observed = info;
            return Process.GetCurrentProcess();
        });

        await launcher.LaunchOrLegacyAsync(download, "/update --pid 123 --lang it", null);

        Assert.NotNull(observed);
        Assert.Equal("/update --pid 123 --lang it", observed.Arguments);
    }

    [Fact]
    public async Task Interactive_launcher_signals_abort_on_start_failure()
    {
        using VerifiedUpdateDownload download = UpdateTestHelpers.CreateVerifiedDownload();
        download.SupportsInteractiveUpdate = true;
        UpdateSessionChannels? setup = null;
        var launcher = new UpdateInstallerLauncher(startProcess: info =>
        {
            string[] args = info.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            setup = UpdateSessionProtocol.OpenSetup(args[Array.IndexOf(args, UpdateSessionProtocol.SessionArgument) + 1].Trim('"'));
            throw new InvalidOperationException("UAC denied");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.LaunchVerifiedAsync(download, "/update", null));
        try
        {
            Assert.NotNull(setup);
            Assert.True(setup.IsAborted);
        }
        finally { setup?.Dispose(); }
    }

    [Fact]
    public async Task Interactive_launcher_signals_abort_when_cancelled_before_visibility()
    {
        using VerifiedUpdateDownload download = UpdateTestHelpers.CreateVerifiedDownload();
        download.SupportsInteractiveUpdate = true;
        using var cts = new CancellationTokenSource();
        UpdateSessionChannels? setup = null;
        var launcher = new UpdateInstallerLauncher(startProcess: info =>
        {
            string[] args = info.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            setup = UpdateSessionProtocol.OpenSetup(args[Array.IndexOf(args, UpdateSessionProtocol.SessionArgument) + 1].Trim('"'));
            cts.Cancel();
            return Process.GetCurrentProcess();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            launcher.LaunchVerifiedAsync(download, "/update", null, cts.Token));
        try
        {
            Assert.NotNull(setup);
            Assert.True(setup.IsAborted);
        }
        finally { setup?.Dispose(); }
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

    [Fact]
    public async Task Chunked_sha256sums_over_buffer_size_verifies_successfully()
    {
        byte[] payload = Encoding.UTF8.GetBytes("verified chunked payload");
        var checksumBuilder = new StringBuilder();
        string dummyHash = new('a', 64);
        for (int i = 0; i < 80; i++)
            checksumBuilder.Append(dummyHash).Append("  OtherInstaller-").Append(i).Append(".exe\n");
        checksumBuilder.Append(Hash(payload)).Append("  ").Append(AssetName).Append('\n');

        var fs = new TestUpdateFileSystem();
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/SHA256SUMS", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ChunkedReadStream(Encoding.UTF8.GetBytes(checksumBuilder.ToString()), 512)),
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
        }));
        var downloader = new UpdateDownloadClient(http, fs, TimeSpan.FromSeconds(2));

        using VerifiedUpdateDownload download = await downloader.DownloadAsync(
            InstallerUrl, AssetName, ChecksumsUrl, null, CancellationToken.None);

        download.VerifyOrThrow();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_checksum_download_disposes_installer_stream_before_deleting_run_directory(bool failOnDispose)
    {
        byte[] payload = [1, 2, 3, 4];
        var fs = new DisposeOrderFileSystem(failOnDispose);
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/SHA256SUMS", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
        }));
        var downloader = new UpdateDownloadClient(http, fs, TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            downloader.DownloadAsync(InstallerUrl, AssetName, ChecksumsUrl, null, CancellationToken.None));

        Assert.True(fs.StreamDisposedBeforeDirectoryDelete);
        Assert.False(fs.HasRunDirectories);
    }

    [Fact]
    public async Task Interactive_launcher_completes_async_visibility_and_cancels_exit_wait()
    {
        using VerifiedUpdateDownload download = UpdateTestHelpers.CreateVerifiedDownload();
        download.SupportsInteractiveUpdate = true;
        UpdateSessionChannels? setup = null;
        Task? handshakeTask = null;
        var launcher = new UpdateInstallerLauncher(startProcess: info =>
        {
            string[] args = info.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string id = args[Array.IndexOf(args, UpdateSessionProtocol.SessionArgument) + 1].Trim('"');
            setup = UpdateSessionProtocol.OpenSetup(id);
            handshakeTask = Task.Run(async () =>
            {
                await Task.Delay(20);
                setup.SignalVisible();
                if (await setup.WaitProceedAsync(TimeSpan.FromSeconds(5)))
                    setup.SignalAccepted();
            });
            return Process.GetCurrentProcess();
        });

        await launcher.LaunchVerifiedAsync(download, "/update", null);
        if (handshakeTask is not null)
            await handshakeTask;
        setup?.Dispose();

        Assert.Throws<ObjectDisposedException>(() => download.VerifyOrThrow());
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

    private sealed class ChunkedReadStream : MemoryStream
    {
        private readonly int _maxChunkSize;
        internal ChunkedReadStream(byte[] data, int maxChunkSize) : base(data) => _maxChunkSize = maxChunkSize;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = Math.Min(buffer.Length, _maxChunkSize);
            return base.ReadAsync(buffer[..count], cancellationToken);
        }
    }

    private sealed class DisposeOrderFileSystem(bool failOnDispose) : IUpdateFileSystem
    {
        private readonly TestUpdateFileSystem _inner = new();
        private bool _streamDisposed;
        public string UpdatesBasePath => _inner.UpdatesBasePath;
        internal bool StreamDisposedBeforeDirectoryDelete { get; private set; }
        internal bool HasRunDirectories => _inner.HasRunDirectories;

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public void ApplyProtectedDirectoryAcl(string path) => _inner.ApplyProtectedDirectoryAcl(path);
        public IEnumerable<string> EnumerateDirectories(string path) => _inner.EnumerateDirectories(path);
        public DateTime GetDirectoryCreationTimeUtc(string path) => _inner.GetDirectoryCreationTimeUtc(path);
        public bool IsReparsePoint(string path) => _inner.IsReparsePoint(path);

        public Stream CreateInstallerFile(string path)
        {
            _streamDisposed = false;
            return new TrackingMemoryStream(() =>
            {
                _streamDisposed = true;
                if (failOnDispose) throw new IOException("Chiusura non riuscita (test).");
            });
        }

        public void DeleteDirectory(string path, bool recursive)
        {
            // Verifica che lo stream aperto sia già stato chiuso prima della rimozione della directory.
            StreamDisposedBeforeDirectoryDelete = _streamDisposed;
            _inner.DeleteDirectory(path, recursive);
        }

        private sealed class TrackingMemoryStream : MemoryStream
        {
            private readonly Action _onDisposed;
            internal TrackingMemoryStream(Action onDisposed) => _onDisposed = onDisposed;

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _onDisposed();
                base.Dispose(disposing);
            }
        }
    }
}
