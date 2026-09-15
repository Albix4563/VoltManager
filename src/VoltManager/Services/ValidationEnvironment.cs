using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;

namespace VoltManager.Services;

/// <summary>
/// Private validation hooks used by the repository's Windows harness. Production runs
/// never set these variables, so normal paths and power behavior are unchanged.
/// </summary>
internal static class ValidationEnvironment
{
    internal const string RootVariable = "VOLTMANAGER_VALIDATION_ROOT";
    internal const string SuppressPowerVariable = "VOLTMANAGER_VALIDATION_NO_POWER_CHANGES";
    internal const string RendererVariable = "VOLTMANAGER_VALIDATION_RENDERER";

    public static bool IsActive
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RootVariable));

    public static string ApplicationDataRoot
    {
        get
        {
            string? validationRoot = Environment.GetEnvironmentVariable(RootVariable);
            return string.IsNullOrWhiteSpace(validationRoot)
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Path.GetFullPath(validationRoot);
        }
    }

    public static bool SuppressPowerChanges
        => string.Equals(Environment.GetEnvironmentVariable(SuppressPowerVariable), "1", StringComparison.Ordinal);

    public static WebViewRendererVariant RendererVariant
        => IsValidationRenderer("hardware")
            ? WebViewRendererVariant.HardwareDefault
            : WebViewRendererVariant.SwiftShader;

    public static string NamedObject(string productionName)
    {
        string? validationRoot = Environment.GetEnvironmentVariable(RootVariable);
        if (string.IsNullOrWhiteSpace(validationRoot)) return productionName;
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(validationRoot).ToUpperInvariant()));
        return productionName + "_Validation_" + Convert.ToHexString(digest.AsSpan(0, 6));
    }

    private static bool IsValidationRenderer(string value)
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RootVariable))
            && string.Equals(Environment.GetEnvironmentVariable(RendererVariable), value, StringComparison.OrdinalIgnoreCase);
}

internal enum WebViewRendererVariant
{
    SwiftShader,
    HardwareDefault,
}

internal static class WebViewRuntimeOptions
{
    private const string CommonArguments =
        "--js-flags=--max-old-space-size=128 " +
        "--enable-low-end-device-mode " +
        "--process-per-site " +
        "--force-gpu-mem-available-mb=32 " +
        "--disable-accelerated-2d-canvas " +
        "--disable-accelerated-video-decode " +
        "--disable-gpu-shader-disk-cache " +
        "--disk-cache-size=67108864 " +
        "--disable-background-networking " +
        "--disable-component-update " +
        "--disable-client-side-phishing-detection " +
        "--disable-breakpad " +
        "--no-pings " +
        "--disable-features=BackForwardCache,InterestFeedContentSuggestions,Translate," +
        "MediaRouter,OptimizationHints,AutofillServerCommunication";

    public static string BrowserArguments(WebViewRendererVariant renderer)
        => renderer == WebViewRendererVariant.SwiftShader
            ? CommonArguments + " --use-angle=swiftshader --use-gl=angle"
            : CommonArguments;
}

internal enum ValidationCounter
{
    MonitorTicks,
    ProcessSnapshots,
    GpuSamples,
    VramSamples,
    HardwareRpcReads,
    UiMetricPublications,
}

internal static class ValidationMetrics
{
    private const long Capacity = 4096;
    private static readonly object Gate = new();
    private static readonly long[] Local = new long[Enum.GetValues<ValidationCounter>().Length];
    private static MemoryMappedFile? _map;
    private static MemoryMappedViewAccessor? _view;

    public static void Increment(ValidationCounter counter)
    {
        if (!ValidationEnvironment.IsActive) return;
        EnsureOpen();
        long value = Interlocked.Increment(ref Local[(int)counter]);
        lock (Gate) _view?.Write((long)(int)counter * sizeof(long), value);
    }

    public static IReadOnlyDictionary<string, long> Snapshot()
    {
        if (!ValidationEnvironment.IsActive) return new Dictionary<string, long>();
        EnsureOpen();
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        lock (Gate)
        {
            foreach (ValidationCounter counter in Enum.GetValues<ValidationCounter>())
                result[counter.ToString()] = _view?.ReadInt64((long)(int)counter * sizeof(long)) ?? 0;
        }
        return result;
    }

    public static void Reset()
    {
        if (!ValidationEnvironment.IsActive) return;
        EnsureOpen();
        lock (Gate)
        {
            Array.Clear(Local);
            foreach (ValidationCounter counter in Enum.GetValues<ValidationCounter>())
                _view?.Write((long)(int)counter * sizeof(long), 0L);
        }
    }

    private static void EnsureOpen()
    {
        if (_view != null) return;
        lock (Gate)
        {
            if (_view != null) return;
            string name = ValidationEnvironment.NamedObject("VoltManager_Validation_Metrics");
            _map = MemoryMappedFile.CreateOrOpen(name, Capacity, MemoryMappedFileAccess.ReadWrite);
            _view = _map.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.ReadWrite);
        }
    }
}
