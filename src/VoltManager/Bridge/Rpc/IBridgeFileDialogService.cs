namespace VoltManager.Bridge.Rpc;

public sealed record BridgeSaveFileRequest(string Title, string Filter, string FileName);
public sealed record BridgeOpenFileRequest(string Title, string Filter, bool CheckFileExists = true, bool Multiselect = false);

public interface IBridgeFileDialogService
{
    Task<string?> SaveFileAsync(BridgeSaveFileRequest request, CancellationToken cancellationToken);
    Task<string?> OpenFileAsync(BridgeOpenFileRequest request, CancellationToken cancellationToken);
}
