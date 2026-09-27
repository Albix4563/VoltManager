using Microsoft.Win32;
using Microsoft.Web.WebView2.Wpf;
using System.Windows;

namespace VoltManager.Bridge.Rpc;

public sealed class WebViewBridgeFileDialogService(WebView2 webView) : IBridgeFileDialogService
{
    public async Task<bool> ConfirmAsync(string message, string title, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool confirmed = await webView.Dispatcher.InvokeAsync(() =>
        {
            Window? owner = Window.GetWindow(webView);
            MessageBoxResult result = owner != null
                ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        });
        cancellationToken.ThrowIfCancellationRequested();
        return confirmed;
    }

    public async Task<string?> SaveFileAsync(BridgeSaveFileRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? path = await webView.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new SaveFileDialog
            {
                Title = request.Title,
                Filter = request.Filter,
                FileName = request.FileName,
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        });
        cancellationToken.ThrowIfCancellationRequested();
        return path;
    }

    public async Task<string?> OpenFileAsync(BridgeOpenFileRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? path = await webView.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = request.Title,
                Filter = request.Filter,
                CheckFileExists = request.CheckFileExists,
                Multiselect = request.Multiselect,
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        });
        cancellationToken.ThrowIfCancellationRequested();
        return path;
    }
}
