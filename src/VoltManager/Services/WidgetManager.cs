using System.Windows;
using Microsoft.Web.WebView2.Core;
using VoltManager.Localization;
using VoltManager.Models;

namespace VoltManager.Services;

public sealed record WidgetDisplayState(string Id, int Number, string Name, bool IsPrimary);

public sealed record WidgetItemState(
    string Type,
    bool Enabled,
    bool Pinned,
    string Size,
    double Width,
    double Height,
    string? MonitorId,
    string? MonitorName,
    int? MonitorNumber,
    string Anchor,
    double OffsetX,
    double OffsetY,
    string EffectiveMonitorId,
    string EffectiveAnchor,
    bool UsesFallbackDisplay,
    double? X,
    double? Y);

public sealed record WidgetStateSnapshot(
    bool Enabled,
    IReadOnlyList<WidgetItemState> Items,
    IReadOnlyList<WidgetDisplayState> Monitors);

public sealed class WidgetManager : IDisposable
{
    private readonly App _app;
    private readonly Task<CoreWebView2Environment> _envTask;
    private readonly Dictionary<string, WidgetWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DisplayService _displays;
    private readonly Dictionary<string, WidgetPlacement> _lastPlacements = new(StringComparer.OrdinalIgnoreCase);
    private DisplaySnapshot _snapshot;
    private bool _disposing;
    private bool _relayoutQueued;

    public event Action<WidgetStateSnapshot>? StateChanged;

    public WidgetManager(App app, Task<CoreWebView2Environment> envTask)
    {
        _app = app;
        _envTask = envTask;
        _displays = new DisplayService();
        _snapshot = _displays.GetSnapshot();
        _displays.DisplaysChanged += OnDisplaysChanged;

        _app.Settings.SettingsChanged += _ => {
            PushTheme();
            PushFont();
        };
        _app.Theme.ThemeChanged += _ => PushTheme();
    }

    public WidgetStateSnapshot GetSnapshot() => BuildSnapshotFromCurrent();

    // Kept for internal callers that need the mutable settings model.
    public WidgetSettings GetState()
    {
        var widgets = GetSettings();
        return widgets;
    }

    public WidgetStateSnapshot SetMasterEnabled(bool enabled)
    {
        var widgets = GetSettings();
        widgets.Enabled = enabled;
        if (!enabled)
        {
            CloseAll();
            _app.Settings.Save();
            var closed = BuildSnapshotFromCurrent();
            StateChanged?.Invoke(closed);
            return closed;
        }

        return Relayout(save: true);
    }

    public WidgetStateSnapshot SetEnabled(string type, bool enabled)
    {
        if (!WidgetSettings.IsKnownType(type))
            throw new ArgumentException(_app.Loc.T("Error_UnknownWidget", type));

        var widgets = GetSettings();
        var item = widgets.GetOrAdd(type);
        item.Enabled = enabled;
        return Relayout(save: true);
    }

    public void ShowEnabled() => Relayout(save: true);

    public WidgetStateSnapshot SetPinned(string type, bool pinned)
    {
        if (_disposing || !WidgetSettings.IsKnownType(type)) return GetSnapshot();
        var widgets = GetSettings();
        var item = widgets.GetOrAdd(type);
        item.Pinned = pinned;
        _app.Settings.Save();

        if (_windows.TryGetValue(type, out var window))
            window.Topmost = pinned;

        var snapshot = BuildSnapshotFromCurrent();
        StateChanged?.Invoke(snapshot);
        return snapshot;
    }

    public WidgetStateSnapshot SetSize(string type, string size)
    {
        if (_disposing || !WidgetSettings.IsKnownType(type)) return GetSnapshot();
        var widgets = GetSettings();
        var item = widgets.GetOrAdd(type);
        item.Size = WidgetSettings.NormalizeSize(size);
        return Relayout(save: true);
    }

    public WidgetStateSnapshot ResetPosition(string type)
    {
        if (_disposing || !WidgetSettings.IsKnownType(type)) return GetSnapshot();
        var widgets = GetSettings();
        var item = widgets.GetOrAdd(type);
        item.OffsetX = 0;
        item.OffsetY = 0;
        return Relayout(save: true);
    }

    public WidgetStateSnapshot SetPlacement(string type, string monitorId, string anchor)
    {
        if (_disposing || !WidgetSettings.IsKnownType(type)) return GetSnapshot();
        if (!WidgetSettings.IsKnownAnchor(anchor))
            throw new ArgumentException("Unknown widget anchor: " + anchor);

        var display = _snapshot.Displays.FirstOrDefault(d =>
            string.Equals(d.Id, monitorId, StringComparison.OrdinalIgnoreCase));
        if (display == null)
            throw new ArgumentException("Unknown monitor: " + monitorId);

        var widgets = GetSettings();
        var item = widgets.GetOrAdd(type);
        item.MonitorId = display.Id;
        item.MonitorName = display.Name;
        item.MonitorNumber = display.Number;
        item.Anchor = WidgetSettings.NormalizeAnchor(anchor);
        return Relayout(save: true);
    }

    internal void SaveDragOffset(string type, PixelRect draggedBoundsPixels)
    {
        if (_disposing || !WidgetSettings.IsKnownType(type)) return;
        if (!_lastPlacements.TryGetValue(type, out var placement)) return;

        var widgets = GetSettings();
        var item = widgets.GetOrAdd(type);
        double sx = placement.EffectiveDisplay.DpiScaleX <= 0 ? 1 : placement.EffectiveDisplay.DpiScaleX;
        double sy = placement.EffectiveDisplay.DpiScaleY <= 0 ? 1 : placement.EffectiveDisplay.DpiScaleY;

        // Delta from the nominal (pre-offset) base bounds.
        item.OffsetX = (draggedBoundsPixels.X - placement.BaseBounds.X) / sx;
        item.OffsetY = (draggedBoundsPixels.Y - placement.BaseBounds.Y) / sy;
        if (!double.IsFinite(item.OffsetX)) item.OffsetX = 0;
        if (!double.IsFinite(item.OffsetY)) item.OffsetY = 0;
        Relayout(save: true);
    }

    internal void RequestRelayout()
    {
        if (_disposing || _relayoutQueued) return;
        _relayoutQueued = true;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _relayoutQueued = false;
            if (!_disposing) Relayout(save: true);
        });
    }

    internal void ForgetWindow(string type) => _windows.Remove(type);

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

    internal void PushFont()
    {
        var data = new { font = _app.Settings.Current.Font };
        foreach (var window in _windows.Values.ToList())
            window.PushEvent("fontChanged", data);
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
        ("plans", "mini") => new Size(280, 96),
        ("plans", "medium") => new Size(340, 150),
        ("plans", "large") => new Size(420, 190),
        (_, "mini") => new Size(180, 96),
        (_, "large") => new Size(340, 200),
        _ => new Size(260, 150),
    };

    public void Dispose()
    {
        _disposing = true;
        _displays.DisplaysChanged -= OnDisplaysChanged;
        _displays.Dispose();
        CloseAll();
    }

    private void OnDisplaysChanged(DisplaySnapshot snapshot)
    {
        if (_disposing) return;
        _snapshot = snapshot;
        Relayout(save: true);
    }

    private WidgetSettings GetSettings()
    {
        _app.Settings.Current.Widgets ??= new WidgetSettings();
        _app.Settings.Current.Widgets.Normalize();
        return _app.Settings.Current.Widgets;
    }

    private WidgetStateSnapshot Relayout(bool save)
    {
        var widgets = GetSettings();
        bool changed = false;

        if (_snapshot.IsReliable)
        {
            foreach (var item in widgets.Items)
                changed |= EnsurePlacementModel(item, _snapshot);
        }

        var enabledItems = widgets.Enabled
            ? widgets.Items.Where(i => i.Enabled).ToList()
            : new List<WidgetItem>();

        var requests = enabledItems.Select(item =>
        {
            EnsurePlacementModel(item, _snapshot);
            return new LayoutRequest(
                item.Type,
                item.MonitorId ?? _snapshot.Primary.Id,
                item.Anchor ?? "topRight",
                item.OffsetX,
                item.OffsetY,
                GetWidgetSize(item.Type, item.Size));
        }).ToList();

        var placements = WidgetLayout.Calculate(requests, _snapshot);
        _lastPlacements.Clear();
        foreach (var p in placements)
            _lastPlacements[p.Type] = p;

        // Apply clamped offsets back when they differ (persistent clamp).
        foreach (var p in placements)
        {
            var item = widgets.Items.FirstOrDefault(i =>
                string.Equals(i.Type, p.Type, StringComparison.OrdinalIgnoreCase));
            if (item == null) continue;

            // Only persist clamp when the desired monitor is present (not temporary fallback).
            if (!p.UsesFallbackDisplay)
            {
                if (Math.Abs(item.OffsetX - p.AppliedOffsetX) > 0.01 ||
                    Math.Abs(item.OffsetY - p.AppliedOffsetY) > 0.01)
                {
                    item.OffsetX = p.AppliedOffsetX;
                    item.OffsetY = p.AppliedOffsetY;
                    changed = true;
                }
            }

            // Snapshot absolute coords for legacy/diagnostics (DIP of effective display).
            double sx = p.EffectiveDisplay.DpiScaleX <= 0 ? 1 : p.EffectiveDisplay.DpiScaleX;
            double sy = p.EffectiveDisplay.DpiScaleY <= 0 ? 1 : p.EffectiveDisplay.DpiScaleY;
            item.X = p.FinalBounds.X / sx;
            item.Y = p.FinalBounds.Y / sy;
            changed = true;
        }

        if (!widgets.Enabled)
        {
            CloseAll();
        }
        else
        {
            var enabledTypes = new HashSet<string>(
                enabledItems.Select(i => i.Type), StringComparer.OrdinalIgnoreCase);

            foreach (var type in _windows.Keys.Where(t => !enabledTypes.Contains(t)).ToList())
                CloseWindow(type);

            foreach (var item in enabledItems)
            {
                if (!_lastPlacements.TryGetValue(item.Type, out var placement)) continue;
                ShowWindow(item, placement);
            }
        }

        if (save || changed)
            _app.Settings.Save();

        var snapshot = BuildSnapshot(placements, widgets);
        StateChanged?.Invoke(snapshot);
        return snapshot;
    }

    private bool EnsurePlacementModel(WidgetItem item, DisplaySnapshot displays)
    {
        if (item.Anchor != null && !string.IsNullOrEmpty(item.MonitorId))
            return false;

        if (!displays.IsReliable)
            return false;

        if (item.X != null && item.Y != null)
        {
            var size = GetWidgetSize(item.Type, item.Size);
            // Treat stored X/Y as DIP on the primary scale for migration.
            double sx = displays.Primary.DpiScaleX <= 0 ? 1 : displays.Primary.DpiScaleX;
            double sy = displays.Primary.DpiScaleY <= 0 ? 1 : displays.Primary.DpiScaleY;
            var legacy = new PixelRect(
                item.X.Value * sx,
                item.Y.Value * sy,
                size.Width * sx,
                size.Height * sy);
            var migrated = WidgetLayout.MigrateLegacy(legacy, displays);
            item.MonitorId = migrated.Display.Id;
            item.MonitorName = migrated.Display.Name;
            item.MonitorNumber = migrated.Display.Number;
            item.Anchor = migrated.Anchor;
            item.OffsetX = migrated.OffsetX;
            item.OffsetY = migrated.OffsetY;
            return true;
        }

        var primary = displays.Primary;
        item.MonitorId = primary.Id;
        item.MonitorName = primary.Name;
        item.MonitorNumber = primary.Number;
        item.Anchor = "topRight";
        item.OffsetX = 0;
        item.OffsetY = 0;
        return true;
    }

    private void ShowWindow(WidgetItem item, WidgetPlacement placement)
    {
        if (_windows.TryGetValue(item.Type, out var existing))
        {
            existing.Topmost = item.Pinned;
            existing.ApplyPlacement(placement, item.Size);
            if (!existing.IsVisible) existing.Show();
            return;
        }

        var window = new WidgetWindow(_app, this, item, _envTask, GetWidgetSize(item.Type, item.Size), placement);
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
        _lastPlacements.Clear();
    }

    private WidgetStateSnapshot BuildSnapshotFromCurrent()
    {
        var widgets = GetSettings();
        var placements = widgets.Enabled
            ? WidgetLayout.Calculate(
                widgets.Items.Where(i => i.Enabled).Select(item =>
                {
                    EnsurePlacementModel(item, _snapshot);
                    return new LayoutRequest(
                        item.Type,
                        item.MonitorId ?? _snapshot.Primary.Id,
                        item.Anchor ?? "topRight",
                        item.OffsetX,
                        item.OffsetY,
                        GetWidgetSize(item.Type, item.Size));
                }).ToList(),
                _snapshot)
            : Array.Empty<WidgetPlacement>();
        return BuildSnapshot(placements, widgets);
    }

    private WidgetStateSnapshot BuildSnapshot(IReadOnlyList<WidgetPlacement> placements, WidgetSettings widgets)
    {
        var byType = placements.ToDictionary(p => p.Type, StringComparer.OrdinalIgnoreCase);
        var items = widgets.Items.Select(item =>
        {
            var size = GetWidgetSize(item.Type, item.Size);
            byType.TryGetValue(item.Type, out var p);
            return new WidgetItemState(
                item.Type,
                item.Enabled,
                item.Pinned,
                WidgetSettings.NormalizeSize(item.Size),
                size.Width,
                size.Height,
                item.MonitorId,
                item.MonitorName,
                item.MonitorNumber,
                item.Anchor ?? "topRight",
                item.OffsetX,
                item.OffsetY,
                p?.EffectiveDisplay.Id ?? item.MonitorId ?? _snapshot.Primary.Id,
                p?.EffectiveAnchor ?? item.Anchor ?? "topRight",
                p?.UsesFallbackDisplay ?? false,
                item.X,
                item.Y);
        }).ToList();

        var monitors = _snapshot.Displays
            .Select(d => new WidgetDisplayState(d.Id, d.Number, d.Name, d.IsPrimary))
            .ToList();

        return new WidgetStateSnapshot(widgets.Enabled, items, monitors);
    }
}
