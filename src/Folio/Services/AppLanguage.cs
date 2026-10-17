using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;

namespace Folio.Services;

/// <summary>
/// The language of Folio's text, chosen in Settings or following Windows. XAML picks its strings when it's
/// loaded, so a new choice applies the next time Folio starts.
/// </summary>
public static class AppLanguage
{
    /// <summary>The languages Folio is translated into: tags as in the Strings folder, names in their own language.</summary>
    public static readonly (string Tag, string Name)[] Available = [("en-US", "English"), ("fr", "Français")];

    /// <summary>The language preference this run started with: a tag from <see cref="Available"/>, or null for Windows'.</summary>
    public static string? Current { get; private set; }

    /// <summary>Uses <paramref name="tag"/> (null: the Windows display language) for everything shown from now on.</summary>
    public static void Apply(string? tag)
    {
        if (tag is not null && !Available.Any(a => a.Tag == tag)) tag = null;
        Current = tag;
        // Read by the resources XAML loads through x:Uid. Unpackaged, the override only lasts this run, and
        // setting it to empty fails (E_INVALIDARG), so following Windows is leaving it alone. Packaged, Windows
        // keeps it between runs, so one left from a previous run is cleared.
        if (tag is not null) ApplicationLanguages.PrimaryLanguageOverride = tag;
        else if (AppPackage.IsPackaged) Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "";

        var manager = new ResourceManager();
        var context = manager.CreateResourceContext();
        if (tag is not null) context.QualifierValues[KnownResourceQualifierName.Language] = tag;
        var map = manager.MainResourceMap.GetSubtree("Resources");
        Strings.Lookup = name => map.TryGetValue(name, context)?.ValueAsString;

        // Dates and numbers keep following the Windows regional format; only the UI culture changes.
        var culture = CultureInfo.GetCultureInfo(Strings.Get("LanguageTag"));
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
