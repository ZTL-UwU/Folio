using Folio.Pdf;
using Folio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Folio;

internal static class Dialogs
{
    private static Style AppStyle(string key) => (Style)Application.Current.Resources[key];

    /// <summary>
    /// A dialog for the window <paramref name="root"/> belongs to. Dialogs are shown above the window's
    /// content rather than inside it, so they don't pick up the theme chosen in Settings by themselves.
    /// </summary>
    public static ContentDialog Create(XamlRoot root)
    {
        var dialog = new ContentDialog { XamlRoot = root };
        if (root.Content is FrameworkElement content) dialog.RequestedTheme = content.RequestedTheme;
        return dialog;
    }

    public static async Task ShowPropertiesAsync(XamlRoot root, PdfDocument document)
    {
        var info = document.Info;
        var file = new FileInfo(document.FilePath);
        var rows = new List<(string, string)>
        {
            ("Title", info.Title),
            ("Author", info.Author),
            ("Subject", info.Subject),
            ("Keywords", info.Keywords),
            ("Creator", info.Creator),
            ("Producer", info.Producer),
            ("Created", info.Created?.LocalDateTime.ToString("f") ?? ""),
            ("Modified", info.Modified?.LocalDateTime.ToString("f") ?? ""),
        };
        var general = new List<(string, string)>
        {
            ("Location", file.FullName),
            ("Size", AttachmentItem.FormatSize(file.Exists ? file.Length : document.FileSize)),
            ("Format", string.IsNullOrEmpty(info.Version) ? "PDF" : $"PDF {info.Version}"),
            ("Pages", document.PageCount.ToString("N0")),
            ("Page size", PaperSize(document.PageSizes[0])),
            ("Security", info.IsEncrypted ? "Encrypted" : "None"),
        };

        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(Section("Document", rows.Where(r => !string.IsNullOrWhiteSpace(r.Item2)).ToList()));
        panel.Children.Add(Section("File", general));

        var dialog = Create(root);
        dialog.Title = "Properties";
        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 520, Padding = new Thickness(0, 0, 16, 0) };
        dialog.CloseButtonText = "Close";
        dialog.DefaultButton = ContentDialogButton.Close;
        await dialog.ShowAsync();
    }

    private static FrameworkElement Section(string title, List<(string Label, string Value)> rows)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = title, Style = AppStyle("BodyStrongTextBlockStyle") });
        var card = new Border { Style = AppStyle("CardBorderStyle"), Padding = new Thickness(16, 12, 16, 12) };
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (rows.Count == 0) rows.Add(("", "No information available"));
        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, Style = AppStyle("SecondaryTextBlockStyle") };
            var value = new TextBlock { Text = rows[i].Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        card.Child = grid;
        stack.Children.Add(card);
        return stack;
    }

    private static string PaperSize(Windows.Foundation.Size points)
    {
        double w = points.Width * 25.4 / 72, h = points.Height * 25.4 / 72;
        double a = Math.Min(w, h), b = Math.Max(w, h);
        (string Name, double A, double B)[] known =
        [
            ("A3", 297, 420), ("A4", 210, 297), ("A5", 148, 210), ("B5", 176, 250),
            ("Letter", 215.9, 279.4), ("Legal", 215.9, 355.6), ("Tabloid", 279.4, 431.8),
        ];
        var match = known.FirstOrDefault(k => Math.Abs(k.A - a) < 2 && Math.Abs(k.B - b) < 2);
        string orientation = w > h ? ", landscape" : "";
        string size = $"{w:0} × {h:0} mm";
        return match.Name is null ? size : $"{match.Name}{orientation} ({size})";
    }

    public static async Task ShowShortcutsAsync(XamlRoot root)
    {
        (string Group, (string Keys, string Action)[] Items)[] groups =
        [
            ("Documents", [
                ("Ctrl+O", "Open a document"), ("Ctrl+N", "New window"), ("Ctrl+S", "Save annotations"),
                ("Ctrl+Shift+S", "Save a copy"), ("Ctrl+P", "Print"), ("Ctrl+R", "Reload"),
                ("Alt+Enter", "Properties"), ("Ctrl+W", AppState.Preferences.RememberRecent ? "Close document" : "Close window"),
                ("Ctrl+,", "Settings"),
            ]),
            ("Navigation", [
                ("PgUp / PgDn", "Previous / next screen"),
                ("Left / Right", "Previous / next page"), ("Ctrl+Home / Ctrl+End", "First / last page"),
                ("F9", "Show or hide the sidebar"),
            ]),
            ("Search and selection", [
                ("Ctrl+F", "Search"), ("F3 / Enter", "Next result"), ("Shift+F3", "Previous result"),
                ("Ctrl+C", "Copy selected text"), ("Ctrl+A", "Select all"), ("Ctrl+H", "Highlight selection"),
            ]),
            ("View", [
                ("Ctrl+Plus / Ctrl+Minus", "Zoom in / out"), ("Ctrl+0", "Actual size"), ("Ctrl+Wheel", "Zoom"),
                ("Ctrl+Left / Ctrl+Right", "Rotate"), ("Ctrl+I", "Invert colors"), ("F11", "Full screen"), ("F5", "Present"),
            ]),
        ];

        var panel = new StackPanel { Spacing = 20 };
        foreach (var (group, items) in groups)
        {
            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new TextBlock { Text = group, Style = AppStyle("BodyStrongTextBlockStyle") });
            var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < items.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var action = new TextBlock { Text = items[i].Action, VerticalAlignment = VerticalAlignment.Center };
                var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
                var alternatives = items[i].Keys.Split(" / ");
                for (int alt = 0; alt < alternatives.Length; alt++)
                {
                    if (alt > 0)
                        keys.Children.Add(new TextBlock { Text = "/", VerticalAlignment = VerticalAlignment.Center, Style = AppStyle("SecondaryTextBlockStyle") });
                    foreach (var key in alternatives[alt].Split('+'))
                    {
                        keys.Children.Add(new Border
                        {
                            Style = AppStyle("KeyCapBorderStyle"),
                            Child = new TextBlock { Text = key, Style = AppStyle("CaptionTextBlockStyle") },
                        });
                    }
                }
                Grid.SetRow(action, i);
                Grid.SetRow(keys, i);
                Grid.SetColumn(keys, 1);
                grid.Children.Add(action);
                grid.Children.Add(keys);
            }
            section.Children.Add(grid);
            panel.Children.Add(section);
        }

        var dialog = Create(root);
        dialog.Title = "Keyboard shortcuts";
        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 520, Padding = new Thickness(0, 0, 16, 0) };
        dialog.CloseButtonText = "Close";
        dialog.DefaultButton = ContentDialogButton.Close;
        await dialog.ShowAsync();
    }

    public static async Task ShowAboutAsync(XamlRoot root)
    {
        var version = typeof(Dialogs).Assembly.GetName().Version;
        var panel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Width = 320 };
        panel.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.png")),
            Width = 96,
            Height = 96,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock { Text = "Folio", Style = AppStyle("SubtitleTextBlockStyle"), HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = $"Version {version?.ToString(3) ?? "1.0.0"}",
            Style = AppStyle("SecondaryTextBlockStyle"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "A simple document viewer for Windows, inspired by GNOME Papers. PDF rendering by PDFium.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        });
        var dialog = Create(root);
        dialog.Content = panel;
        dialog.CloseButtonText = "Close";
        dialog.DefaultButton = ContentDialogButton.Close;
        await dialog.ShowAsync();
    }

    public static async Task ShowSettingsAsync(XamlRoot root)
    {
        var prefs = AppState.Preferences;
        var dialog = Create(root);

        var theme = new ComboBox { MinWidth = 140, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(theme, "Theme");
        // In the order of AppTheme.
        foreach (var name in new[] { "System", "Light", "Dark" }) theme.Items.Add(name);
        theme.SelectedIndex = (int)prefs.Theme;
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedIndex < 0) return;
            prefs.Theme = (AppTheme)theme.SelectedIndex;
            AppState.Save();
            App.ApplyTheme();
            // That restyles the windows, but the dialog sits above the window's content.
            dialog.RequestedTheme = ((FrameworkElement)root.Content).RequestedTheme;
        };

        var corners = new ToggleSwitch { IsOn = prefs.RoundedPageCorners, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(corners, "Rounded page corners");
        corners.Toggled += (_, _) =>
        {
            prefs.RoundedPageCorners = corners.IsOn;
            AppState.Save();
            App.ApplyPageCorners();
        };

        var recent = new ToggleSwitch { IsOn = prefs.RememberRecent, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(recent, "Remember recent documents");
        recent.Toggled += (_, _) => AppState.SetRememberRecent(recent.IsOn);

        var panel = new StackPanel { Spacing = 16, Width = 440 };
        panel.Children.Add(SettingsSection("Appearance",
            SettingRow("Theme", "Use light or dark colors, or follow Windows.", theme),
            SettingRow("Rounded page corners", "Round the corners of pages in the viewer.", corners)));
        panel.Children.Add(SettingsSection("History", SettingRow("Remember recent documents",
            "List opened documents on the start page and reopen them where you left off.", recent)));

        dialog.Title = "Settings";
        dialog.Content = panel;
        dialog.CloseButtonText = "Close";
        dialog.DefaultButton = ContentDialogButton.Close;
        await dialog.ShowAsync();
    }

    private static FrameworkElement SettingsSection(string title, params FrameworkElement[] rows)
    {
        // One card per row, a little apart, like Windows Settings.
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = title, Style = AppStyle("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 0, 0, 4) });
        foreach (var row in rows)
            stack.Children.Add(new Border { Style = AppStyle("CardBorderStyle"), Padding = new Thickness(16, 12, 16, 12), Child = row });
        return stack;
    }

    private static FrameworkElement SettingRow(string title, string description, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title });
        text.Children.Add(new TextBlock { Text = description, Style = AppStyle("SecondaryCaptionTextBlockStyle"), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }
}
