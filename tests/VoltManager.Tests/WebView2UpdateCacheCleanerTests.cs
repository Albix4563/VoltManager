using System;
using System.IO;
using System.Reflection;

namespace VoltManager.Tests;

public sealed class WebView2UpdateCacheCleanerTests
{
    private static MethodInfo GetTryClearMethod()
    {
        var cleanerType = typeof(App).Assembly.GetType("VoltManager.Services.WebView2UpdateCacheCleaner");
        Assert.NotNull(cleanerType);

        var method = cleanerType!.GetMethod(
            "TryClear",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(string), typeof(string).MakeByRefType() },
            modifiers: null);
        Assert.NotNull(method);
        return method!;
    }

    [Fact]
    public void TryClear_RemovesOnlyWebViewProfile_AndPreservesSiblingAppData()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerCacheCleanerTests", Guid.NewGuid().ToString("N"));
        string webView = Path.Combine(root, "WebView2");
        string nested = Path.Combine(webView, "Default", "Cache");
        string settings = Path.Combine(root, "settings.json");

        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "cache.bin"), "stale");
        File.WriteAllText(settings, "{\"theme\":\"dark\"}");

        try
        {
            var method = GetTryClearMethod();
            object?[] args = { webView, null };
            bool success = (bool)method.Invoke(null, args)!;

            Assert.True(success, args[1] as string);
            Assert.False(Directory.Exists(webView));
            Assert.True(File.Exists(settings));
            Assert.Equal("{\"theme\":\"dark\"}", File.ReadAllText(settings));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryClear_IsIdempotent_WhenProfileDoesNotExist()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerCacheCleanerTests", Guid.NewGuid().ToString("N"));
        string webView = Path.Combine(root, "WebView2");
        Directory.CreateDirectory(root);

        try
        {
            var method = GetTryClearMethod();
            object?[] args = { webView, null };
            bool success = (bool)method.Invoke(null, args)!;

            Assert.True(success, args[1] as string);
            Assert.False(Directory.Exists(webView));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
