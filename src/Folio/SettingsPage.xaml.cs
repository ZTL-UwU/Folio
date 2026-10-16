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
        (AppTheme.Light, Strings.Get("ThemeLight")), (AppTheme.Dark, Strings.Get("ThemeDark")), (AppTheme.System, Strings.Get("ThemeSystem")),
    ];

    /// <summary>Tags as in <see cref="AppLanguage.Available"/>, null first for the Windows language.</summary>
    private static readonly string?[] Languages = [null, .. AppLanguage.Available.Select(a => a.Tag)];

    /// <summary>Set while showing the current preferences, so that doesn't count as the user changing them.</summary>
    private bool _refreshing;

    public SettingsPage()
    {
        InitializeComponent();
        foreach (var (_, name) in Themes) ThemeOptions.Items.Add(name);
        LanguageBox.Items.Add(Strings.Get("LanguageSystem"));
        foreach (var (_, name) in AppLanguage.Available) LanguageBox.Items.Add(name);
        var assembly = typeof(SettingsPage).Assembly;
        VersionText.Text = Strings.Format("VersionFormat", assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        AboutCard.Description = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright.Replace("(c)", "©") ?? "";
    }

    /// <summary>Shows the current preferences, which another window may have changed, from the top.</summary>
    public void Refresh()
    {
        var prefs = AppState.Preferences;
        _refreshing = true;
        ThemeOptions.SelectedIndex = Array.FindIndex(Themes, t => t.Theme == prefs.Theme);
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, prefs.Language));
        CornersToggle.IsOn = prefs.RoundedPageCorners;
        HyphenatedToggle.IsOn = prefs.SearchJoinHyphenated;
        QuotesToggle.IsOn = prefs.SearchAnyQuote;
        DashesToggle.IsOn = prefs.SearchAnyDash;
        RecentToggle.IsOn = prefs.RememberRecent;
        _refreshing = false;
        UpdateThemeValue();
        UpdateLanguageNote();
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

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || LanguageBox.SelectedIndex < 0) return;
        AppState.Preferences.Language = Languages[LanguageBox.SelectedIndex];
        AppState.Save();
        UpdateLanguageNote();
    }

    /// <summary>Says a restart is needed while the chosen language isn't the one this run started with.</summary>
    private void UpdateLanguageNote() => LanguageCard.Description = Strings.Get(
        AppState.Preferences.Language == AppLanguage.Current ? "SettingsLanguage/Description" : "SettingsLanguageRestart");

    private void OnCornersToggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        AppState.Preferences.RoundedPageCorners = CornersToggle.IsOn;
        AppState.Save();
        App.ApplyPageCorners();
    }

    private void OnHyphenatedToggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        AppState.Preferences.SearchJoinHyphenated = HyphenatedToggle.IsOn;
        AppState.Save();
    }

    private void OnQuotesToggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        AppState.Preferences.SearchAnyQuote = QuotesToggle.IsOn;
        AppState.Save();
    }

    private void OnDashesToggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        AppState.Preferences.SearchAnyDash = DashesToggle.IsOn;
        AppState.Save();
    }

    private void OnRecentToggled(object sender, RoutedEventArgs e)
    {
        if (!_refreshing) AppState.SetRememberRecent(RecentToggle.IsOn);
    }
}
