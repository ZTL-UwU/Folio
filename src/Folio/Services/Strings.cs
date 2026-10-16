using System.Collections.Concurrent;
using System.Globalization;

namespace Folio.Services;

/// <summary>
/// Text shown to the user, from Strings\<i>language</i>\Resources.resw; XAML gets the same resources through
/// x:Uid. UI-free, so the PDF and printing code the tests compile can use it too: the app points
/// <see cref="Lookup"/> at its resources (see <c>AppLanguage</c>), the tests at the English file.
/// </summary>
public static class Strings
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private static Func<string, string?> _lookup = _ => null;
    private static bool _zeroIsSingular;

    /// <summary>Finds a resource by name, or returns null if there's none. Set once, before anything is shown.</summary>
    public static Func<string, string?> Lookup
    {
        get => _lookup;
        set
        {
            _lookup = value;
            Cache.Clear();
            // French counts 0 like 1 ("0 page"); English doesn't ("0 pages").
            _zeroIsSingular = Get("LanguageTag").StartsWith("fr", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The resource called <paramref name="name"/>, or the name itself if it's missing.</summary>
    public static string Get(string name) => Cache.GetOrAdd(name, n => _lookup(n) ?? n);

    /// <summary>The resource called <paramref name="name"/>, if there is one.</summary>
    public static string? TryGet(string name)
    {
        string value = Get(name);
        return value == name ? null : value;
    }

    /// <summary>The resource called <paramref name="name"/> with <c>{0}</c>, <c>{1}</c>... filled in.</summary>
    public static string Format(string name, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(name), args);

    /// <summary>
    /// <paramref name="name"/>_One or <paramref name="name"/>_Other, depending on <paramref name="count"/>,
    /// with the count as <c>{0}</c> and <paramref name="args"/> after it.
    /// </summary>
    public static string Plural(string name, long count, params object?[] args)
    {
        bool one = count == 1 || (count == 0 && _zeroIsSingular);
        return Format(name + (one ? "_One" : "_Other"), [count, .. args]);
    }
}
