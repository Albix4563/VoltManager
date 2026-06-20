using System.Windows;
using Microsoft.Web.WebView2.Core;
using VoltManager.Models;

namespace VoltManager.Services;

public sealed class WidgetManager : IDisposable
{
    private readonly App _app;
    private readonly Task<CoreWebView2Environment> _envTask;
    private readonly Dictionary<string, WidgetWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposing;

    public event Action<WidgetSettings>? StateChanged;

    public WidgetManager(App app, Task<CoreWebView2Environment> envTask)
    {
        _app = app;
        _envTask = envTask;

        _app.Settings.SettingsChanged += _ => PushTheme();
        _app.Theme.ThemeChanged += _ => PushTheme();
    }

    public WidgetSettings GetState()
    {
        _app.Settings.Current.Widgets ??= new WidgetSettings();
        _app.Settings.Current.Widgets.Normalize();
        return _app.Settings.Current.Widgets;
    }

    public WidgetSettings SetMasterEnabled(bool enabled)
    {
        var widgets = GetState();
        widgets.Enabled = enabled;
        _app.Settings.Save();

        if (enabled) ShowEnabled();
        else CloseAll();

        StateChanged?.Invoke(widgets);
        return widgets;
    }

    public WidgetSettings SetEnabled(string type, bool enabled)
    {
        if (!WidgetSettings.IsKnownType(type))
            throw new ArgumentException("Widget sconosciuto: " + type);

        var widgets = GetState();
        var item = widgets.GetOrAdd(type);
        item.Enabled = enabled;
        _app.Settings.Save();

        if (!widgets.Enabled || !enabled)
        {
            CloseWindow(item.Type);
        }
        else
        {
            EnsurePosition(item, EnabledIndex(item.Type));
            ShowWindow(item);
            _app.Settings.Save();
        }

        StateChanged?.Invoke(widgets);
        return widgets;
    }

    public void ShowEnabled()
    {
        var widgets = GetState();
        if (!widgets.Enabled) return;

        bool changed = false;
        int index = 0;
        foreach (var item in widgets.Items.Where(i => i.Enabled))
        {
            changed |= EnsurePosition(item, index++);
            ShowWindow(item);
        }
        if (changed) _app.Settings.Save();
    }

    internal void SavePosition(string type, double left, double top)
    {
        if (_disposing || !WidgetSettings.IsKnownType(type)) return;
        var item = GetState().GetOrAdd(type);
        item.X = left;
        item.Y = top;
        _app.Settings.Save();
    }

    internal void SetPinned(string type, bool pinned)
    {
        if (_disposing || !WidgetSettings.IsKnownType(type)) return;
        var item = GetState().GetOrAdd(type);
        item.Pinned = pinned;
        _app.Settings.Save();
        StateChanged?.Invoke(GetState());
    }

    internal void ForgetWindow(string type)
    {
        _windows.Remove(type);
    }

    internal void PushTheme()
    {
        var data = new { resolvedTheme = _app.Theme.ResolvedTheme };
        foreach (var window in _windows.Values.ToList())
            window.PushEvent("themeChanged", data);
    }

    private int EnabledIndex(string type)
    {
        int index = 0;
        foreach (var item in GetState().Items.Where(i => i.Enabled))
        {
            if (string.Equals(item.Type, type, StringComparison.OrdinalIgnoreCase))
                return index;
            index++;
        }
        return 0;
    }

    private bool EnsurePosition(WidgetItem item, int index)
    {
        if (item.X != null && item.Y != null) return false;

        var p = CalculateCascadePosition(SystemParameters.WorkArea, index, GetWidgetSize(item.Type));
        item.X ??= p.X;
        item.Y ??= p.Y;
        return true;
    }

    private void ShowWindow(WidgetItem item)
    {
        if (_windows.TryGetValue(item.Type, out var existing))
        {
            existing.Topmost = item.Pinned;
            if (!existing.IsVisible) existing.Show();
            return;
        }

        var window = new WidgetWindow(_app, this, item, _envTask, GetWidgetSize(item.Type));
        _windows[item.Type] = window;
        window.Closed += (_, _) => ForgetWindow(item.Type);
        window.Show();
    }

    private void CloseWindow(string type)
    {
        if (!_windows.TryGetValue(type, out var window)) return;
        window.Close();
    }

    private void CloseAll()
    {
        foreach (var window in _windows.Values.ToList())
            window.Close();
        _windows.Clear();
    }

    public static Size GetWidgetSize(string type) => type switch
    {
        "calendar" => new Size(320, 330),
        "usage" => new Size(300, 220),
        "temps" => new Size(280, 180),
        "power" => new Size(300, 190),
        _ => new Size(260, 150),
    };

    public static Point CalculateCascadePosition(Rect workArea, int index, Size size)
    {
        const double margin = 24;
        const double step = 24;
        double x = workArea.Right - size.Width - margin - index * step;
        double y = workArea.Top + margin + index * step;

        x = Math.Max(workArea.Left + 8, Math.Min(x, workArea.Right - size.Width - 8));
        if (y + size.Height > workArea.Bottom - 8)
            y = workArea.Top + margin;

        return new Point(x, y);
    }

    public void Dispose()
    {
        _disposing = true;
        CloseAll();
    }
}
