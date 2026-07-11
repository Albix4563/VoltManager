using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private readonly PromptPalette _palette;
    private readonly LocalizationService _loc;

    public UpdatePromptAction Action { get; private set; } = UpdatePromptAction.Dismiss;
    public int SnoozeMinutes { get; private set; } = 30;

    public UpdatePromptWindow(UpdateInfo info, LocalizationService loc, string? theme = null)
    {
        _loc = loc;
        _palette = PromptPalette.For(theme);
        Title = loc.T("UpdatePrompt_Title");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        Background = _palette.Background;
        Foreground = _palette.Text;
        Loaded += (_, _) => PositionBottomRight();

        Content = BuildContent(info);
    }

    private UIElement BuildContent(UpdateInfo info)
    {
        var root = new Border
        {
            Padding = new Thickness(20),
            Background = _palette.Surface,
            BorderBrush = _palette.Accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        root.Child = stack;

        stack.Children.Add(new TextBlock
        {
            Text = _loc.T("UpdatePrompt_Available"),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8),
        });

        stack.Children.Add(new TextBlock
        {
            Text = VersionMessage(info),
            Foreground = _palette.Text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        if (!string.IsNullOrWhiteSpace(info.ReleaseNotes))
        {
            stack.Children.Add(new TextBlock
            {
                Text = TrimReleaseNotes(info.ReleaseNotes),
                Foreground = _palette.Muted,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 120,
                Margin = new Thickness(0, 0, 0, 16),
            });
        }

        var snoozeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 14),
            VerticalAlignment = VerticalAlignment.Center,
        };
        snoozeRow.Children.Add(new TextBlock
        {
            Text = _loc.T("UpdatePrompt_SnoozeLabel"),
            Foreground = _palette.Text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });

        AddSnoozeItem(_loc.T("UpdatePrompt_15min"), 15);
        AddSnoozeItem(_loc.T("UpdatePrompt_30min"), 30, selected: true);
        AddSnoozeItem(_loc.T("UpdatePrompt_1hour"), 60);
        AddSnoozeItem(_loc.T("UpdatePrompt_2hours"), 120);
        _snoozeCombo.Width = 130;
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

        buttons.Children.Add(MakeButton(_loc.T("UpdatePrompt_Skip"), () => CloseWith(UpdatePromptAction.Skip), subtle: true, _palette));
        buttons.Children.Add(MakeButton(_loc.T("UpdatePrompt_Snooze"), () => CloseWith(UpdatePromptAction.Snooze), subtle: true, _palette));
        buttons.Children.Add(MakeButton(_loc.T("UpdatePrompt_Install"), () => CloseWith(UpdatePromptAction.Install), subtle: false, _palette));
        stack.Children.Add(buttons);

        return root;
    }

    private void AddSnoozeItem(string label, int minutes, bool selected = false)
    {
        var item = new ComboBoxItem { Content = label, Tag = minutes };
        _snoozeCombo.Items.Add(item);
        if (selected)
        {
            _snoozeCombo.SelectedItem = item;
            SnoozeMinutes = minutes;
        }
    }

    private static Button MakeButton(string text, System.Action click, bool subtle, PromptPalette palette)
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
            button.Background = palette.SubtleButton;
            button.Foreground = palette.Text;
            button.BorderBrush = palette.Border;
        }
        else
        {
            button.Background = palette.Accent;
            button.Foreground = palette.OnAccent;
            button.BorderBrush = palette.Accent;
            button.FontWeight = FontWeights.Bold;
        }

        button.Click += (_, _) => click();
        return button;
    }

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
