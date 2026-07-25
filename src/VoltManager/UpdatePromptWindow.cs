using System.Windows;
using System.Windows.Controls;
using VoltManager.Localization;
using VoltManager.Models;

namespace VoltManager;

public enum UpdatePromptAction
{
    Dismiss,
    Install,
    Snooze,
    Skip
}

public sealed class UpdatePromptWindow : Window
{
    private readonly ComboBox _snoozeCombo = new();
    private readonly LocalizationService _loc;

    public UpdatePromptAction Action { get; private set; } = UpdatePromptAction.Dismiss;
    public int SnoozeMinutes { get; private set; } = 30;

    public UpdatePromptWindow(UpdateInfo info, LocalizationService loc)
    {
        _loc = loc;
        Title = loc.T("UpdatePrompt_Title");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        SetResourceReference(BackgroundProperty, "ThemeBackgroundBrush");
        SetResourceReference(ForegroundProperty, "ThemeTextBrush");
        Loaded += (_, _) => PositionBottomRight();

        Content = BuildContent(info);
    }

    private UIElement BuildContent(UpdateInfo info)
    {
        var root = new Border
        {
            Padding = new Thickness(20),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
        };
        Bind(root, Border.BackgroundProperty, "ThemeSurfaceBrush");
        Bind(root, Border.BorderBrushProperty, "ThemePrimaryBrush");

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        root.Child = stack;

        var title = new TextBlock
        {
            Text = _loc.T("UpdatePrompt_Available"),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Bind(title, TextBlock.ForegroundProperty, "ThemeTextBrush");
        stack.Children.Add(title);

        var version = new TextBlock
        {
            Text = VersionMessage(info),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        Bind(version, TextBlock.ForegroundProperty, "ThemeTextBrush");
        stack.Children.Add(version);

        if (!string.IsNullOrWhiteSpace(info.ReleaseNotes))
        {
            var notes = new TextBlock
            {
                Text = TrimReleaseNotes(info.ReleaseNotes),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 120,
                Margin = new Thickness(0, 0, 0, 16),
            };
            Bind(notes, TextBlock.ForegroundProperty, "ThemeMutedTextBrush");
            stack.Children.Add(notes);
        }

        var snoozeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 14),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var snoozeLabel = new TextBlock
        {
            Text = _loc.T("UpdatePrompt_SnoozeLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Bind(snoozeLabel, TextBlock.ForegroundProperty, "ThemeTextBrush");
        snoozeRow.Children.Add(snoozeLabel);

        AddSnoozeItem(_loc.T("UpdatePrompt_15min"), 15);
        AddSnoozeItem(_loc.T("UpdatePrompt_30min"), 30, selected: true);
        AddSnoozeItem(_loc.T("UpdatePrompt_1hour"), 60);
        AddSnoozeItem(_loc.T("UpdatePrompt_2hours"), 120);
        _snoozeCombo.Width = 130;
        Bind(_snoozeCombo, Control.BackgroundProperty, "ThemeSurfaceElevatedBrush");
        Bind(_snoozeCombo, Control.ForegroundProperty, "ThemeTextBrush");
        Bind(_snoozeCombo, Control.BorderBrushProperty, "ThemeBorderBrush");
        _snoozeCombo.SelectionChanged += (_, _) =>
        {
            if (_snoozeCombo.SelectedItem is ComboBoxItem item && item.Tag is int minutes)
                SnoozeMinutes = minutes;
        };
        snoozeRow.Children.Add(_snoozeCombo);
        stack.Children.Add(snoozeRow);

        var buttons = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            ItemHeight = 36,
        };

        buttons.Children.Add(MakeButton(_loc.T("UpdatePrompt_Skip"), () => CloseWith(UpdatePromptAction.Skip), subtle: true));
        buttons.Children.Add(MakeButton(_loc.T("UpdatePrompt_Snooze"), () => CloseWith(UpdatePromptAction.Snooze), subtle: true));
        buttons.Children.Add(MakeButton(_loc.T("UpdatePrompt_Install"), () => CloseWith(UpdatePromptAction.Install), subtle: false));
        stack.Children.Add(buttons);

        return root;
    }

    private void AddSnoozeItem(string label, int minutes, bool selected = false)
    {
        var item = new ComboBoxItem { Content = label, Tag = minutes };
        Bind(item, Control.ForegroundProperty, "ThemeTextBrush");
        Bind(item, Control.BackgroundProperty, "ThemeSurfaceElevatedBrush");
        _snoozeCombo.Items.Add(item);
        if (selected)
        {
            _snoozeCombo.SelectedItem = item;
            SnoozeMinutes = minutes;
        }
    }

    private static Button MakeButton(string text, System.Action click, bool subtle)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = subtle ? 104 : 160,
            Height = 36,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(12, 0, 12, 0),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        if (subtle)
        {
            Bind(button, Control.BackgroundProperty, "ThemeSurfaceElevatedBrush");
            Bind(button, Control.ForegroundProperty, "ThemeTextBrush");
            Bind(button, Control.BorderBrushProperty, "ThemeBorderBrush");
        }
        else
        {
            Bind(button, Control.BackgroundProperty, "ThemePrimaryBrush");
            Bind(button, Control.ForegroundProperty, "ThemeOnPrimaryBrush");
            Bind(button, Control.BorderBrushProperty, "ThemePrimaryBrush");
            button.FontWeight = FontWeights.Bold;
        }

        button.Click += (_, _) => click();
        return button;
    }

    private static void Bind(FrameworkElement element, DependencyProperty property, object resourceKey)
        => element.SetResourceReference(property, resourceKey);

    private void CloseWith(UpdatePromptAction action)
    {
        Action = action;
        DialogResult = true;
        Close();
    }

    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left, area.Right - ActualWidth - 24);
        Top = Math.Max(area.Top, area.Bottom - ActualHeight - 24);
    }

    private string VersionMessage(UpdateInfo info)
    {
        string current = FormatVersion(info.CurrentVersion);
        string latest = FormatVersion(info.LatestVersion);
        return _loc.T("UpdatePrompt_VersionMsg", current, latest);
    }

    private string FormatVersion(string? version)
    {
        var value = string.IsNullOrWhiteSpace(version) ? _loc.T("UpdatePrompt_ND") : version.Trim();
        return value.StartsWith('v') || value.StartsWith('V') ? value : "v" + value;
    }

    private static string TrimReleaseNotes(string notes)
    {
        const int max = 420;
        var trimmed = notes.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }
}
