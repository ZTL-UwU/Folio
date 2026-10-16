using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Folio.Services;

namespace Folio.Tests;

/// <summary>The app's strings: every language has them all, and the code only asks for ones that exist.</summary>
public sealed partial class StringsTests
{
    private static readonly string Source = Path.GetFullPath(Path.Combine(ThisFolder(), "..", "..", "src", "Folio"));
    private static readonly Dictionary<string, string> English = Load("en-US");

    private static string ThisFolder([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    /// <summary>Names as in the .resw file. Code asks for x:Uid ones with a slash (Card/Description).</summary>
    private static Dictionary<string, string> Load(string language) =>
        XDocument.Load(Path.Combine(Source, "Strings", language, "Resources.resw")).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

    /// <summary>The PDF and printing code shows its errors in English under test, as some tests check them.</summary>
    [ModuleInitializer]
    internal static void UseEnglish() => Strings.Lookup = name => English.GetValueOrDefault(name.Replace('/', '.'));

    public static TheoryData<string> Translations => new(Directory.GetDirectories(Path.Combine(Source, "Strings"))
        .Select(Path.GetFileName).Where(l => l != "en-US")!);

    [Theory]
    [MemberData(nameof(Translations))]
    public void Translation_HasEveryStringWithTheSamePlaceholders(string language)
    {
        var translated = Load(language);
        Assert.Empty(English.Keys.Except(translated.Keys));
        Assert.Empty(translated.Keys.Except(English.Keys));
        foreach (var (name, text) in English)
            Assert.True(Placeholders(text).SetEquals(Placeholders(translated[name])), $"{language}: {name} has other placeholders than in English");
    }

    /// <summary>LanguageTag sets the UI culture, so it's the folder's language, or one of its regions (fr-FR in fr).</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    public void Translation_HasItsOwnLanguageTag(string language)
    {
        var folder = CultureInfo.GetCultureInfo(language);
        var tagged = CultureInfo.GetCultureInfo(Load(language)["LanguageTag"]);
        Assert.True(tagged.Name == folder.Name || tagged.Parent.Name == folder.Name, $"{language}: LanguageTag is {tagged.Name}");
    }

    [Fact]
    public void Code_AsksOnlyForStringsThatExist()
    {
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Source, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            foreach (Match m in StringCall().Matches(File.ReadAllText(file)))
            {
                string name = m.Groups["name"].Value.Replace('/', '.');
                string[] names = m.Groups["method"].Value == "Plural" ? [name + "_One", name + "_Other"] : [name];
                missing.AddRange(names.Where(n => !English.ContainsKey(n)).Select(n => $"{Path.GetFileName(file)}: {n}"));
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void Xaml_HasStringsForEveryUid()
    {
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Source, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            foreach (Match m in Uid().Matches(File.ReadAllText(file)))
            {
                string uid = m.Groups[1].Value;
                if (!English.Keys.Any(k => k.StartsWith(uid + ".", StringComparison.Ordinal))) missing.Add($"{Path.GetFileName(file)}: {uid}");
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void Plural_CountsOneAsSingular()
    {
        Assert.Equal("1 page", Strings.Plural("PageCount", 1));
        Assert.Equal("0 pages", Strings.Plural("PageCount", 0));
        Assert.Equal("2 pages", Strings.Plural("PageCount", 2));
    }

    private static HashSet<string> Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value)];

    [GeneratedRegex(@"\{(\d+)[:,}]")]
    private static partial Regex Placeholder();

    // A whole name, not the start of one put together in code ("Key" + key).
    [GeneratedRegex(@"Strings\.(?<method>Get|TryGet|Format|Plural)\(""(?<name>[^""]+)""\s*[,)]")]
    private static partial Regex StringCall();

    [GeneratedRegex(@"x:Uid=""([^""]+)""")]
    private static partial Regex Uid();
}
