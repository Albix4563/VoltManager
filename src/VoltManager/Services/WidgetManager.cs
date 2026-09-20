using System.Windows;
using Microsoft.Web.WebView2.Core;
using VoltManager.Bridge;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Performance;

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
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly LocalizationService _loc;
    private readonly Func<Task<CoreWebView2Environment>> _envFactory;
    private readonly Action<bool> _refreshSamplingDemand;
    private readonly Func<WidgetManager, WidgetItem, Task<CoreWebView2Environment>, Size, WidgetPlacement, WidgetWindow> _windowFactory;
    private readonly Dictionary<string, WidgetWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private DisplayService? _displays;
    private readonly Dictionary<string, WidgetPlacement> _lastPlacements = new(StringComparer.OrdinalIgnoreCase);
    private DisplaySnapshot _snapshot;
    private bool _disposing;
    private bool _relayoutQueued;
    private bool _displayInit;
    private volatile bool _hasOpenWindows;
    internal bool HasOpenWindows => _hasOpenWindows;
    internal bool HasVisibleResourceConsumers
        => _windows.Values.Any(window => window.HasVisibleResourceSurface);

    public event Action<WidgetStateSnapshot>? StateChanged;

    public WidgetManager(
        SettingsService settings,
        ThemeService theme,
        LocalizationService loc,
        Func<Task<CoreWebView2Environment>> environmentFactory,
        Action<bool> refreshSamplingDemand,
        Func<WidgetManager, WidgetItem, Task<CoreWebView2Environment>, Size, WidgetPlacement, WidgetWindow> windowFactory)
    {
        _settings = settings;
        _theme = theme;
        _loc = loc;
        _envFactory = environmentFactory;
        _refreshSamplingDemand = refreshSamplingDemand;
        _windowFactory = windowFactory;
        // Defer DisplayService init: display enumeration queries monitor APIs
        // and subscribes SystemEvents — not needed if widgets are disabled.
        _snapshot = DisplaySnapshot.SyntheticPrimary();

        _settings.SettingsChanged += _ => {
            PushTheme();
            PushFont();
        };
        _theme.ThemeChanged += _ => PushTheme();
    }

    private Task<CoreWebView2Environment> EnvTask() => _envFactory();

    private void EnsureDisplayService()
    {
        if (_displayInit) return;
        _displayInit = true;
        _displays = new DisplayService();
        _snapshot = _displays.GetSnapshot();
        _displays.DisplaysChanged += OnDisplaysChanged;
    }

    public WidgetStateSnapshot GetSnapshot()
    {
        EnsureDisplayService();
        return BuildSnapshotFromCurrent();
    }

    // Returns a detached settings snapshot for callers that need widget configuration.
    public WidgetSettings GetState()
    {
        var widgets = GetSettings();
        return widgets;
    }

    public WidgetStateSnapshot SetMasterEnabled(bool enabled)
    {
        _settings.Update(state => state.Widgets.Enabled = enabled);
        if (!enabled)
        {
            CloseAll();
            var closed = BuildSnapshotFromCurrent();
            StateChanged?.Invoke(closed);
            return closed;
        }

        return Relayout(save: false);
    }

    public WidgetStateSnapshot SetEnabled(string type, bool enabled)
    {
        if (!WidgetSettings.IsKnownType(type))
            throw new ArgumentException(_loc.T("Error_UnknownWidget", type));

        _settings.Update(state => state.Widgets.GetOrAdd(type).Enabled = enabled);
        return Relayout(save: false);
    }

    public void ShowEnabled()
    {
        EnsureDisplayService();
        Relayout(save: true);
    }

    public WidgetStateSnapshot SetPinned(string type, bool pinned)
    {
        if (_disposing || !WidgetSettings.IsKnownType(type)) return GetSnapshot();
        _settings.Update(state => state.Widgets.GetOrAdd(type).Pinned = pinned);

        if (_windows.TryGetValue(type, out var window))
            window.Topmost = pinned;

        var snapshot = BuildSnapshotFromCurrent();
        StateChanged?.Invoke(snapshot);
        return snapshot;
    }

    public WidgetStateSnapshot SetSize(string type, string size)
    {
        if (_disposing || !WidgetSettings.IsKnownType(type)) return GetSnapshot();
        string normalizedSize = WidgetSettings.NormalizeSize(size);
        _settings.Update(state => state.Widgets.GetOrAdd(type).Size = normalizedSize);
        return Relayout(save: false);
    }

    public WidgetStateSnapshot ResetPosition(string type)
    {
        if (_disposing || !WidgetSettings.IsKnownType(type)) return GetSnapshot();
        _settings.Update(state =>
        {
            var item = state.Widgets.GetOrAdd(type);
            item.OffsetX = 0;
            item.OffsetY = 0;
        });
        return Relayout(save: false);
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

        string normalizedAnchor = WidgetSettings.NormalizeAnchor(anchor);
        _settings.Update(state =>
        {
            var item = state.Widgets.GetOrAdd(type);
            item.MonitorId = display.Id;
            item.MonitorName = display.Name;
            item.MonitorNumber = display.Number;
            item.Anchor = normalizedAnchor;
        });
        return Relayout(save: false);
    }

    internal void SaveDragOffset(string type, PixelRect draggedBoundsPixels)
    {
        if (_disposing || !WidgetSettings.IsKnownType(type)) return;
        if (!_lastPlacements.TryGetValue(type, out var placement)) return;

        double sx = placement.EffectiveDisplay.DpiScaleX <= 0 ? 1 : placement.EffectiveDisplay.DpiScaleX;
        double sy = placement.EffectiveDisplay.DpiScaleY <= 0 ? 1 : placement.EffectiveDisplay.DpiScaleY;

        // Delta from the nominal (pre-offset) base bounds.
        double offsetX = (draggedBoundsPixels.X - placement.BaseBounds.X) / sx;
        double offsetY = (draggedBoundsPixels.Y - placement.BaseBounds.Y) / sy;
        if (!double.IsFinite(offsetX)) offsetX = 0;
        if (!double.IsFinite(offsetY)) offsetY = 0;
        _settings.Update(state =>
        {
            var item = state.Widgets.GetOrAdd(type);
            item.OffsetX = offsetX;
            item.OffsetY = offsetY;
        });
        Relayout(save: false);
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

    internal void ForgetWindow(string type)
    {
        _windows.Remove(type);
        _hasOpenWindows = _windows.Count != 0;
        _refreshSamplingDemand(false);
    }

    internal void PushTheme()
    {
        var data = _theme.GetWebTheme();
        foreach (var window in _windows.Values.ToList())
            window.PushEvent(BridgeEventNames.ThemeChanged, data);
    }

    internal void PushLanguage()
    {
        var data = new { language = _loc.CurrentLanguage, locale = _loc.CurrentCulture.Name };
        foreach (var window in _windows.Values.ToList())
            window.PushEvent(BridgeEventNames.LanguageChanged, data);
    }

    internal void PushFont()
    {
        var data = new { font = _settings.Current.Font };
        foreach (var window in _windows.Values.ToList())
            window.PushEvent(BridgeEventNames.FontChanged, data);
    }

    internal void PushResourceProfile(ResourcePressureState state)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposing) return;
            foreach (var window in _windows.Values) window.PushResourceProfile(state);
        });
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
        if (_displays != null)
        {
            _displays.DisplaysChanged -= OnDisplaysChanged;
            _displays.Dispose();
        }
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
        var widgets = _settings.Current.Widgets;
        widgets.Normalize();
        return widgets;
    }

    private WidgetStateSnapshot Relayout(bool save)
    {
        EnsureDisplayService();
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
            _settings.Update(state => state.Widgets = widgets);

        var snapshot = BuildSnapshot(placements, widgets);
        StateChanged?.Invoke(snapshot);
        _refreshSamplingDemand(false);
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

        var window = _windowFactory(this, item, EnvTask(), GetWidgetSize(item.Type, item.Size), placement);
        _windows[item.Type] = window;
        _hasOpenWindows = true;
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
        _hasOpenWindows = false;
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
