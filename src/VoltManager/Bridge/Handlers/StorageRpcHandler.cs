using System.Text.Json;
using VoltManager.Bridge.Rpc;
using VoltManager.Services;

namespace VoltManager.Bridge.Handlers;

public sealed class StorageRpcHandler(StorageService storage,
    Func<CancellationToken, Task<bool>>? confirmCleanup = null,
    Func<CancellationToken, Task<StorageCleanupResult>>? cleanup = null) : IBridgeRpcHandler
{
    private int _cleaning;
    private static readonly string[] RegisteredMethods = ["getStorageDrives", "getStorageAnalysis", "cleanStorageTemp", "openStorageTool"];
    public static IReadOnlyCollection<string> MethodNames => RegisteredMethods;
    public IReadOnlyCollection<string> Methods => MethodNames;

    public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "getStorageDrives":
                return await Task.Run(storage.GetStorageDrives, cancellationToken);
            case "getStorageAnalysis":
                if (Volatile.Read(ref _cleaning) != 0) throw new InvalidOperationException("Pulizia già in corso.");
                return await storage.GetStorageAnalysisAsync(
                    BridgePayload.RequiredString(payload, "root", "Il parametro 'root' è obbligatorio."), cancellationToken);
            case "cleanStorageTemp":
                if (Interlocked.CompareExchange(ref _cleaning, 1, 0) != 0)
                    throw new InvalidOperationException("Pulizia già in corso.");
                try
                {
                    if (confirmCleanup == null || !await confirmCleanup(cancellationToken))
                        return new StorageCleanupResult(false, 0, 0, 0, false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return await (cleanup ?? storage.CleanTempAsync)(cancellationToken);
                }
                finally { Volatile.Write(ref _cleaning, 0); }
            case "openStorageTool":
                storage.OpenTool(BridgePayload.RequiredString(payload, "tool", "Il parametro 'tool' è obbligatorio."));
                return new { launched = true };
            default:
                throw new ArgumentException($"Metodo RPC non supportato: {method}.");
        }
    }
}
