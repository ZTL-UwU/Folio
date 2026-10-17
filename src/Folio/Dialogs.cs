using System.Reflection;
using Folio.Pdf;
using Folio.Services;
using Microsoft.UI.Xaml;
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
            (Strings.Get("PropertyTitle"), info.Title),
            (Strings.Get("PropertyAuthor"), info.Author),
            (Strings.Get("PropertySubject"), info.Subject),
            (Strings.Get("PropertyKeywords"), info.Keywords),
            (Strings.Get("PropertyCreator"), info.Creator),
            (Strings.Get("PropertyProducer"), info.Producer),
            (Strings.Get("PropertyCreated"), info.Created?.LocalDateTime.ToString("f") ?? ""),
            (Strings.Get("PropertyModified"), info.Modified?.LocalDateTime.ToString("f") ?? ""),
        };
        var general = new List<(string, string)>
        {
            (Strings.Get("PropertyLocation"), file.FullName),
            (Strings.Get("PropertySize"), AttachmentItem.FormatSize(file.Exists ? file.Length : document.FileSize)),
            (Strings.Get("PropertyFormat"), string.IsNullOrEmpty(info.Version) ? "PDF" : $"PDF {info.Version}"),
            (Strings.Get("PropertyPages"), document.PageCount.ToString("N0")),
            (Strings.Get("PropertyPageSize"), PaperSize(document.PageSizes[0])),
            (Strings.Get("PropertySecurity"), Strings.Get(info.IsEncrypted ? "SecurityEncrypted" : "SecurityNone")),
        };

        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(Section(Strings.Get("PropertiesDocument"), rows.Where(r => !string.IsNullOrWhiteSpace(r.Item2)).ToList()));
        panel.Children.Add(Section(Strings.Get("PropertiesFile"), general));

        var dialog = Create(root);
        dialog.Title = Strings.Get("PropertiesTitle");
        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 520, Padding = new Thickness(0, 0, 16, 0) };
        dialog.CloseButtonText = Strings.Get("Close");
        dialog.DefaultButton = ContentDialogButton.Close;
        await dialog.ShowAsync();
    }

    private static FrameworkElement Section(string title, List<(string Label, string Value)> rows)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = title, Style = AppStyle("BodyStrongTextBlockStyle") });
        var card = new Border { Style = AppStyle("CardBorderStyle"), Padding = new Thickness(16, 12, 16, 12) };
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (rows.Count == 0) rows.Add(("", Strings.Get("PropertiesNone")));
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
            (Strings.Get("PaperLetter"), 215.9, 279.4), (Strings.Get("PaperLegal"), 215.9, 355.6), (Strings.Get("PaperTabloid"), 279.4, 431.8),
        ];
        var match = known.FirstOrDefault(k => Math.Abs(k.A - a) < 2 && Math.Abs(k.B - b) < 2);
        string size = Strings.Format("SizeMillimeters", w, h);
        return match.Name is null ? size : Strings.Format(w > h ? "PageSizeNamedLandscape" : "PageSizeNamed", match.Name, size);
    }

    public static async Task ShowShortcutsAsync(XamlRoot root)
    {
        // Groups and actions are resource names; keys are in English, each looked up as Key<name>.
        (string Group, (string Keys, string Action)[] Items)[] groups =
        [
            ("ShortcutsDocuments", [
                ("Ctrl+O", "ShortcutOpen"), ("Ctrl+N", "ShortcutNewWindow"), ("Ctrl+S", "ShortcutSaveCopy"),
                ("Ctrl+P", "ShortcutPrint"), ("Ctrl+Shift+P", "ShortcutPrintSystem"), ("Ctrl+R", "ShortcutReload"),
                ("Alt+Enter", "ShortcutProperties"), ("Ctrl+W", AppState.Preferences.RememberRecent ? "CloseDocument" : "CloseWindow"),
                ("Ctrl+,", "ShortcutSettings"),
            ]),
            ("ShortcutsNavigation", [
                ("Up / Down", "ShortcutScroll"),
                ("PgUp / PgDn", "ShortcutScreen"),
                ("Shift+Space / Space", "ShortcutScreen"),
                ("Left / Right", "ShortcutPage"),
                ("Home / End", "ShortcutEnds"),
                ("F9", "ShortcutSidebar"),
            ]),
            ("ShortcutsSearch", [
                ("Ctrl+F", "ShortcutSearch"), ("F3 / Enter", "ShortcutNextResult"), ("Shift+F3 / Shift+Enter", "ShortcutPreviousResult"),
                ("Ctrl+C", "ShortcutCopy"), ("Ctrl+A", "ShortcutSelectAll"), ("Ctrl+H", "ShortcutHighlight"),
            ]),
            ("ShortcutsView", [
                ("Ctrl+Plus / Ctrl+Minus", "ShortcutZoomInOut"), ("Ctrl+0", "ShortcutActualSize"), ("Ctrl+Wheel", "ShortcutZoom"),
                ("Ctrl+Left / Ctrl+Right", "ShortcutRotate"), ("Ctrl+I", "ShortcutInvert"), ("F11", "ShortcutFullScreen"), ("F5", "ShortcutPresent"),
            ]),
        ];

        var panel = new StackPanel { Spacing = 20 };
        foreach (var (group, items) in groups)
        {
            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new TextBlock { Text = Strings.Get(group), Style = AppStyle("BodyStrongTextBlockStyle") });
            var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < items.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var action = new TextBlock { Text = Strings.Get(items[i].Action), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
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
                            // Named as on the keyboard, such as Maj for Shift in French.
                            Child = new TextBlock { Text = Strings.TryGet("Key" + key) ?? key, Style = AppStyle("CaptionTextBlockStyle") },
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
        dialog.Title = Strings.Get("ShortcutsTitle");
        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 520, Padding = new Thickness(0, 0, 16, 0) };
        dialog.CloseButtonText = Strings.Get("Close");
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
            Text = Strings.Format("VersionFormat", version?.ToString(3) ?? "1.0.0"),
            Style = AppStyle("SecondaryTextBlockStyle"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        // The commit and checkout (or CI branch) it was built from; see AddBuildInfo in Folio.csproj.
        var buildInfo = typeof(Dialogs).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildInfo")?.Value;
        if (!string.IsNullOrEmpty(buildInfo))
        {
            panel.Children.Add(new TextBlock
            {
                Text = buildInfo,
                Style = AppStyle("SecondaryTextBlockStyle"),
                HorizontalAlignment = HorizontalAlignment.Center,
                IsTextSelectionEnabled = true,
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get("AboutDescription"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        });
        var dialog = Create(root);
        dialog.Content = panel;
        dialog.CloseButtonText = Strings.Get("Close");
        dialog.DefaultButton = ContentDialogButton.Close;
        await dialog.ShowAsync();
    }
}
