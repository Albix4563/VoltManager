using System.Text.Json;
using VoltManager.Bridge.Rpc;
using VoltManager.Services;

namespace VoltManager.Bridge.Handlers;

public sealed class StorageRpcHandler(StorageService storage) : IBridgeRpcHandler
{
    private static readonly string[] RegisteredMethods = ["getStorageDrives", "getStorageAnalysis"];
    public static IReadOnlyCollection<string> MethodNames => RegisteredMethods;
    public IReadOnlyCollection<string> Methods => MethodNames;

    public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken) => method switch
    {
        "getStorageDrives" => await Task.Run(storage.GetStorageDrives, cancellationToken),
        "getStorageAnalysis" => await storage.GetStorageAnalysisAsync(
            BridgePayload.RequiredString(payload, "root", "Il parametro 'root' è obbligatorio."), cancellationToken),
        _ => throw new ArgumentException($"Metodo RPC non supportato: {method}."),
    };
}
