using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    public UpdatePromptAction Action { get; private set; } = UpdatePromptAction.Dismiss;
    public int SnoozeMinutes { get; private set; } = 30;

    public UpdatePromptWindow(UpdateInfo info)
    {
        Title = "Aggiornamento VoltManager";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(10, 17, 40));
        Foreground = Brushes.White;
        Loaded += (_, _) => PositionBottomRight();

        Content = BuildContent(info);
    }

    private UIElement BuildContent(UpdateInfo info)
    {
        var root = new Border
        {
            Padding = new Thickness(20),
            Background = new SolidColorBrush(Color.FromRgb(15, 26, 54)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0, 241, 254)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        root.Child = stack;

        stack.Children.Add(new TextBlock
        {
            Text = "Aggiornamento disponibile",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8),
        });

        stack.Children.Add(new TextBlock
        {
            Text = VersionMessage(info),
            Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        if (!string.IsNullOrWhiteSpace(info.ReleaseNotes))
        {
            stack.Children.Add(new TextBlock
            {
                Text = TrimReleaseNotes(info.ReleaseNotes),
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
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
            Text = "Rimanda di:",
            Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });

        AddSnoozeItem("15 minuti", 15);
        AddSnoozeItem("30 minuti", 30, selected: true);
        AddSnoozeItem("1 ora", 60);
        AddSnoozeItem("2 ore", 120);
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

        buttons.Children.Add(MakeButton("Salta versione", () => CloseWith(UpdatePromptAction.Skip), subtle: true));
        buttons.Children.Add(MakeButton("Rimanda", () => CloseWith(UpdatePromptAction.Snooze), subtle: true));
        buttons.Children.Add(MakeButton("Installa aggiornamento", () => CloseWith(UpdatePromptAction.Install), subtle: false));
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
            button.Background = new SolidColorBrush(Color.FromRgb(30, 42, 74));
            button.Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240));
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105));
        }
        else
        {
            button.Background = new SolidColorBrush(Color.FromRgb(0, 241, 254));
            button.Foreground = new SolidColorBrush(Color.FromRgb(3, 7, 18));
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 241, 254));
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

    private static string VersionMessage(UpdateInfo info)
    {
        string current = FormatVersion(info.CurrentVersion);
        string latest = FormatVersion(info.LatestVersion);
        return $"È disponibile una nuova versione di VoltManager. Versione attuale: {current}. Nuova versione: {latest}.";
    }

    private static string FormatVersion(string? version)
    {
        var value = string.IsNullOrWhiteSpace(version) ? "N/D" : version.Trim();
        return value.StartsWith('v') || value.StartsWith('V') ? value : "v" + value;
    }

    private static string TrimReleaseNotes(string notes)
    {
        const int max = 420;
        var trimmed = notes.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }
}