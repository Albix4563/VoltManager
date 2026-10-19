using System;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
#if NETFRAMEWORK
using System.Web.Script.Serialization;
#else
using System.Text.Json;
#endif

namespace VoltManager.Services
{
    /// <summary>
    /// A single update handoff, confined to the interactive Windows session and
    /// protected by an unpredictable token and an explicit event ACL.
    /// This source is shared by the .NET 8 host and the .NET Framework Setup.
    /// </summary>
    public static class UpdateSessionProtocol
    {
        public const string SessionArgument = "--update-session";
        public const string PaletteArgument = "--update-palette";
        public const int PaletteColorCount = 10;

        public static string NewId() => Guid.NewGuid().ToString("N");

        /// <summary>Centralized, quoted arguments for one verified installer handoff.</summary>
        public static string BuildArguments(string legacyArguments, string id, string? encodedPalette)
        {
            if (!TryValidateId(id, out string normalized))
                throw new ArgumentException("Invalid update session identifier.", nameof(id));
            if (legacyArguments == null || legacyArguments.IndexOf('"') >= 0)
                throw new ArgumentException("Invalid legacy update arguments.", nameof(legacyArguments));
            string arguments = legacyArguments + " " + SessionArgument + " \"" + normalized + "\"";
            if (!string.IsNullOrWhiteSpace(encodedPalette))
            {
                if (!TryDecodePalette(encodedPalette!, out _))
                    throw new ArgumentException("Invalid update palette.", nameof(encodedPalette));
                arguments += " " + PaletteArgument + " \"" + encodedPalette + "\"";
            }
            return arguments;
        }

        public static bool TryValidateId(string value, out string normalized)
        {
            normalized = "";
            if (value == null || value.Length != 32 ||
                !Guid.TryParseExact(value, "N", out Guid parsed))
                return false;
            normalized = parsed.ToString("N");
            return true;
        }

        public static string EncodePalette(string[] colors)
        {
            if (!IsValidPalette(colors))
                throw new ArgumentException("Invalid update theme palette.", nameof(colors));
#if NETFRAMEWORK
            string json = new JavaScriptSerializer().Serialize(colors);
#else
            string json = JsonSerializer.Serialize(colors);
#endif
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        public static bool TryDecodePalette(string value, out string[] colors)
        {
            colors = new string[0];
            if (string.IsNullOrWhiteSpace(value) || value.Length > 1024)
                return false;
            try
            {
                byte[] bytes = Convert.FromBase64String(value);
                if (bytes.Length > 256) return false;
                string json = new UTF8Encoding(false, true).GetString(bytes);
#if NETFRAMEWORK
                string[] parsed = new JavaScriptSerializer().Deserialize<string[]>(json);
#else
                string[] parsed = JsonSerializer.Deserialize<string[]>(json) ?? new string[0];
#endif
                if (!IsValidPalette(parsed)) return false;
                colors = parsed;
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException ||
                                        ex is System.Text.DecoderFallbackException ||
#if NETFRAMEWORK
                                        ex is InvalidOperationException)
#else
                                        ex is JsonException)
#endif
            {
                return false;
            }
        }

        private static bool IsValidPalette(string[] colors)
        {
            if (colors == null || colors.Length != PaletteColorCount) return false;
            foreach (string color in colors)
                if (color == null || !Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant))
                    return false;
            return true;
        }

        public static UpdateSessionChannels CreateHost(string id)
            => UpdateSessionChannels.Create(id);

        public static UpdateSessionChannels OpenSetup(string id)
            => UpdateSessionChannels.Open(id);

        /// <summary>The relaunched host may signal only an already existing Setup session.</summary>
        public static bool TrySignalReady(string id)
        {
            if (!TryValidateId(id, out string normalized)) return false;
            try
            {
                using (var ready =
#if NETFRAMEWORK
                    EventWaitHandle.OpenExisting(EventName(normalized, "Ready"), EventWaitHandleRights.Modify))
#else
                    EventWaitHandle.OpenExisting(EventName(normalized, "Ready")))
#endif
                    return ready.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static string EventName(string id, string step)
        {
            if (!TryValidateId(id, out string normalized))
                throw new ArgumentException("Invalid update session id.", nameof(id));
            return @"Local\VoltManager_Update_" +
                Process.GetCurrentProcess().SessionId + "_" + normalized + "_" + step;
        }

        internal static EventWaitHandleSecurity CreateEventSecurity()
        {
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Missing Windows user SID.");
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(user,
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
                AccessControlType.Allow));
            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                EventWaitHandleRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                EventWaitHandleRights.FullControl, AccessControlType.Allow));
            return security;
        }
    }

    public sealed class UpdateSessionChannels : IDisposable
    {
        private readonly EventWaitHandle _visible;
        private readonly EventWaitHandle _proceed;
        private readonly EventWaitHandle _accepted;
        private readonly EventWaitHandle _abort;
        private readonly EventWaitHandle _ready;

        private UpdateSessionChannels(EventWaitHandle visible, EventWaitHandle proceed,
            EventWaitHandle accepted, EventWaitHandle abort, EventWaitHandle ready)
        {
            _visible = visible;
            _proceed = proceed;
            _accepted = accepted;
            _abort = abort;
            _ready = ready;
        }

        internal static UpdateSessionChannels Create(string id)
        {
            var security = UpdateSessionProtocol.CreateEventSecurity();
            EventWaitHandle CreateEvent(string step)
            {
#if NETFRAMEWORK
                return new EventWaitHandle(false, EventResetMode.ManualReset,
                    UpdateSessionProtocol.EventName(id, step), out _, security);
#else
                return EventWaitHandleAcl.Create(false, EventResetMode.ManualReset,
                    UpdateSessionProtocol.EventName(id, step), out _, security);
#endif
            }

            EventWaitHandle visible = CreateEvent("Visible");
            try
            {
                EventWaitHandle proceed = CreateEvent("Proceed");
                try
                {
                    EventWaitHandle accepted = CreateEvent("Accepted");
                    try
                    {
                        EventWaitHandle abort = CreateEvent("Abort");
                        try { return new UpdateSessionChannels(visible, proceed, accepted, abort, CreateEvent("Ready")); }
                        catch { abort.Dispose(); throw; }
                    }
                    catch { accepted.Dispose(); throw; }
                }
                catch { proceed.Dispose(); throw; }
            }
            catch { visible.Dispose(); throw; }
        }

        internal static UpdateSessionChannels Open(string id)
        {
            EventWaitHandle OpenEvent(string step)
            {
#if NETFRAMEWORK
                return EventWaitHandle.OpenExisting(UpdateSessionProtocol.EventName(id, step),
                    EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize);
#else
                return EventWaitHandle.OpenExisting(UpdateSessionProtocol.EventName(id, step));
#endif
            }
            EventWaitHandle visible = OpenEvent("Visible");
            try
            {
                EventWaitHandle proceed = OpenEvent("Proceed");
                try
                {
                    EventWaitHandle accepted = OpenEvent("Accepted");
                    try
                    {
                        EventWaitHandle abort = OpenEvent("Abort");
                        try { return new UpdateSessionChannels(visible, proceed, accepted, abort, OpenEvent("Ready")); }
                        catch { abort.Dispose(); throw; }
                    }
                    catch { accepted.Dispose(); throw; }
                }
                catch { proceed.Dispose(); throw; }
            }
            catch { visible.Dispose(); throw; }
        }

        public void SignalVisible() => _visible.Set();
        public void SignalProceed() => _proceed.Set();
        public void SignalAccepted() => _accepted.Set();
        public void SignalAbort() => _abort.Set();
        public void SignalReady() => _ready.Set();
        public bool IsAborted => _abort.WaitOne(0);

        public Task<bool> WaitVisibleAsync(TimeSpan timeout, CancellationToken cancellationToken = default(CancellationToken))
            => WaitAsync(_visible, timeout, cancellationToken);
        public Task<bool> WaitProceedAsync(TimeSpan timeout, CancellationToken cancellationToken = default(CancellationToken))
            => WaitAsync(_proceed, timeout, cancellationToken);
        public Task<bool> WaitAcceptedAsync(TimeSpan timeout, CancellationToken cancellationToken = default(CancellationToken))
            => WaitAsync(_accepted, timeout, cancellationToken);
        public Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default(CancellationToken))
            => WaitAsync(_ready, timeout, cancellationToken);
        public Task<bool> WaitAbortAsync(TimeSpan timeout, CancellationToken cancellationToken = default(CancellationToken))
            => Task.Run(() => _abort.WaitOne(timeout), cancellationToken);

        private Task<bool> WaitAsync(EventWaitHandle target, TimeSpan timeout, CancellationToken token)
        {
            if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            return Task.Run(() =>
            {
                if (_abort.WaitOne(0))
                    return false;
                int result = WaitHandle.WaitAny(new WaitHandle[] { target, _abort, token.WaitHandle },
                    (int)Math.Ceiling(timeout.TotalMilliseconds));
                token.ThrowIfCancellationRequested();
                // Abort wins if the two signals race: a cancelled handoff must
                // never accidentally be treated as authorized to install.
                return result == 0 && !_abort.WaitOne(0);
            }, token);
        }

        public void Dispose()
        {
            _visible.Dispose();
            _proceed.Dispose();
            _accepted.Dispose();
            _abort.Dispose();
            _ready.Dispose();
        }
    }
}
