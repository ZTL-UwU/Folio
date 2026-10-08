using System.Reflection;
using Folio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Folio;

/// <summary>The settings page a window shows in place of its content, like the one in Windows apps.</summary>
public sealed partial class SettingsPage : UserControl
{
    // In the order the options are listed.
    private static readonly (AppTheme Theme, string Name)[] Themes =
    [
        (AppTheme.Light, "Light"), (AppTheme.Dark, "Dark"), (AppTheme.System, "Use system setting"),
    ];

    /// <summary>Set while showing the current preferences, so that doesn't count as the user changing them.</summary>
    private bool _refreshing;

    public SettingsPage()
    {
        InitializeComponent();
        foreach (var (_, name) in Themes) ThemeOptions.Items.Add(name);
        var assembly = typeof(SettingsPage).Assembly;
        VersionText.Text = $"Version {assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
        AboutCard.Description = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright.Replace("(c)", "©") ?? "";
    }

    /// <summary>Shows the current preferences, which another window may have changed, from the top.</summary>
    public void Refresh()
    {
        var prefs = AppState.Preferences;
        _refreshing = true;
        ThemeOptions.SelectedIndex = Array.FindIndex(Themes, t => t.Theme == prefs.Theme);
        CornersToggle.IsOn = prefs.RoundedPageCorners;
        RecentToggle.IsOn = prefs.RememberRecent;
        _refreshing = false;
        UpdateThemeValue();
        Scroller.ChangeView(null, 0, null, true);
    }

    private void UpdateThemeValue() => ThemeValue.Text = ThemeOptions.SelectedIndex >= 0 ? Themes[ThemeOptions.SelectedIndex].Name : "";

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateThemeValue();
        if (_refreshing || ThemeOptions.SelectedIndex < 0) return;
        AppState.Preferences.Theme = Themes[ThemeOptions.SelectedIndex].Theme;
        AppState.Save();
        App.ApplyTheme();
    }

    private void OnCornersToggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        AppState.Preferences.RoundedPageCorners = CornersToggle.IsOn;
        AppState.Save();
        App.ApplyPageCorners();
    }

    private void OnRecentToggled(object sender, RoutedEventArgs e)
    {
        if (!_refreshing) AppState.SetRememberRecent(RecentToggle.IsOn);
    }
}
