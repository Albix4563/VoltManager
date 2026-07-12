using System.Windows;
using Microsoft.Web.WebView2.Core;
using VoltManager.Localization;
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
            throw new ArgumentException(_app.Loc.T("Error_UnknownWidget", type));

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

    public WidgetSettings SetPinned(string type, bool pinned)
    {
        if (_disposing || !WidgetSettings.IsKnownType(type)) return GetState();
        var widgets = GetState();
        var item = widgets.GetOrAdd(type);
        item.Pinned = pinned;
        _app.Settings.Save();

        if (_windows.TryGetValue(type, out var window))
            window.Topmost = pinned;

        StateChanged?.Invoke(widgets);
        return widgets;
    }

    public WidgetSettings SetSize(string type, string size)
    {
        if (_disposing || !WidgetSettings.IsKnownType(type)) return GetState();
        var widgets = GetState();
        var item = widgets.GetOrAdd(type);
        item.Size = WidgetSettings.NormalizeSize(size);
        var preset = GetWidgetSize(item.Type, item.Size);

        if (_windows.TryGetValue(item.Type, out var window))
        {
            window.ApplyPresetSize(preset, item.Size);
            item.X = window.Left;
            item.Y = window.Top;
        }
        else if (item.X != null && item.Y != null)
        {
            var p = ClampPosition(SystemParameters.WorkArea, item.X.Value, item.Y.Value, preset);
            item.X = p.X;
            item.Y = p.Y;
        }

        _app.Settings.Save();
        StateChanged?.Invoke(widgets);
        return widgets;
    }

    public WidgetSettings ResetPosition(string type)
    {
        if (_disposing || !WidgetSettings.IsKnownType(type)) return GetState();
        var widgets = GetState();
        var item = widgets.GetOrAdd(type);

        item.X = null;
        item.Y = null;

        if (widgets.Enabled && item.Enabled)
        {
            EnsurePosition(item, EnabledIndex(item.Type));
            if (_windows.TryGetValue(type, out var window) && item.X != null && item.Y != null)
            {
                window.Left = item.X.Value;
                window.Top = item.Y.Value;
            }
        }

        _app.Settings.Save();
        StateChanged?.Invoke(widgets);
        return widgets;
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

    internal void PushLanguage()
    {
        var data = new { language = _app.Loc.CurrentLanguage, locale = _app.Loc.CurrentCulture.Name };
        foreach (var window in _windows.Values.ToList())
            window.PushEvent("languageChanged", data);
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

        var p = CalculateCascadePosition(SystemParameters.WorkArea, index, GetWidgetSize(item.Type, item.Size));
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

        var window = new WidgetWindow(_app, this, item, _envTask, GetWidgetSize(item.Type, item.Size));
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

    public static Size GetWidgetSize(string type, string size = "medium") => (type, WidgetSettings.NormalizeSize(size)) switch
    {
        ("clock", "mini") => new Size(180, 96),
        ("clock", "large") => new Size(340, 200),
        ("calendar", "mini") => new Size(190, 120),
        ("calendar", "medium") => new Size(320, 330),
        ("calendar", "large") => new Size(420, 430),
        ("usage", "mini") => new Size(220, 118),
        ("usage", "medium") => new Size(300, 220),
        ("usage", "large") => new Size(390, 285),
        ("temps", "mini") => new Size(210, 110),
        ("temps", "medium") => new Size(280, 180),
        ("temps", "large") => new Size(360, 235),
        ("power", "mini") => new Size(220, 118),
        ("power", "medium") => new Size(300, 230),
        ("power", "large") => new Size(390, 300),
        ("plans", "mini") => new Size(280, 108),
        ("plans", "medium") => new Size(340, 150),
        ("plans", "large") => new Size(420, 190),
        (_, "mini") => new Size(180, 96),
        (_, "large") => new Size(340, 200),
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

    public static Point ClampPosition(Rect workArea, double left, double top, Size size)
    {
        const double edge = 8;
        double minX = workArea.Left + edge;
        double maxX = Math.Max(minX, workArea.Right - size.Width - edge);
        double minY = workArea.Top + edge;
        double maxY = Math.Max(minY, workArea.Bottom - size.Height - edge);
        return new Point(Math.Clamp(left, minX, maxX), Math.Clamp(top, minY, maxY));
    }

    public void Dispose()
    {
        _disposing = true;
        CloseAll();
    }
}
