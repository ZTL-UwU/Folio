using Folio.Services;

namespace Folio.Tests;

public sealed class LaunchPolicyTests
{
    [Theory]
    [InlineData("https://example.com/a?b=c", "https://example.com/a?b=c")]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("  HTTPS://Example.com  ", "https://example.com/")]
    [InlineData("mailto:someone@example.com", "mailto:someone@example.com")]
    [InlineData("www.example.com/page", "https://www.example.com/page")]
    public void AllowsWebAndMailLinks(string link, string expected) =>
        Assert.Equal(expected, LaunchPolicy.ParseExternalLink(link)?.AbsoluteUri);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData(@"\\server\share\setup.exe")]
    [InlineData("/etc/passwd")]
    [InlineData("ms-settings:privacy")]
    [InlineData("search-ms:query=x")]
    [InlineData("shell:startup")]
    [InlineData("ftp://files.example.com/x")]
    [InlineData("https://accounts.example.com@evil.example/")]
    [InlineData("http://user:pass@evil.example/")]
    [InlineData("accounts.example.com@evil.example/login")]
    public void RejectsEverythingElse(string? link) => Assert.Null(LaunchPolicy.ParseExternalLink(link));

    [Theory]
    [InlineData("https://example.com/", "https://example.com")]
    [InlineData("https://example.com/a?b=c#d", "https://example.com/a?b=c#d")]
    [InlineData("http://example.com:8080/x", "http://example.com:8080/x")]
    [InlineData("https://bücher.example/", "https://xn--bcher-kva.example")]
    [InlineData("https://аррӏе.com/", "https://xn--80ak6aa92e.com")]
    [InlineData("http://[::1]:8000/", "http://[::1]:8000")]
    [InlineData("mailto:someone@example.com", "mailto:someone@example.com")]
    public void DescribesLinksByTheirAsciiHost(string link, string expected) =>
        Assert.Equal(expected, LaunchPolicy.DescribeLink(LaunchPolicy.ParseExternalLink(link)!));

    [Fact]
    public void BidiOverridesCannotDisguiseAnExtension()
    {
        // Shown as "notesLMTH.txt" with the override in place; the type that would open is .html.
        string name = LaunchPolicy.SafeFileName("notes‮txt.html");
        Assert.Equal("notestxt.html", name);
        Assert.Equal(AttachmentRisk.Ask, LaunchPolicy.Classify(name));
        Assert.Equal("invoicefdp.exe", LaunchPolicy.SafeFileName("invoice‮fdp.exe"));
        Assert.Equal("a.txt", LaunchPolicy.SafeFileName("a​⁦.txt⁩"));
    }

    [Theory]
    [InlineData("Title", "Title")]
    [InlineData("Re‮port‬", "Report")]
    [InlineData("⁧عنوان⁩ — Folio", "عنوان — Folio")]
    [InlineData("a‏b", "a‏b")]
    public void StripsBidiControlsFromDocumentText(string text, string expected) =>
        Assert.Equal(expected, LaunchPolicy.StripBidiControls(text));

    [Theory]
    [InlineData("link.url", "link.url.txt")]
    [InlineData("Report.LNK", "Report.LNK.txt")]
    [InlineData("x.scf", "x.scf.txt")]
    [InlineData("a.searchConnector-ms", "a.searchConnector-ms.txt")]
    [InlineData("a.library-ms", "a.library-ms.txt")]
    [InlineData("dark.theme", "dark.theme.txt")]
    [InlineData("desktop.ini", "desktop.ini.txt")]
    [InlineData("setup.exe", "setup.exe")]
    [InlineData("notes.docx", "notes.docx")]
    [InlineData("settings.ini", "settings.ini")]
    public void ShortcutsAreSavedInert(string name, string expected) => Assert.Equal(expected, LaunchPolicy.SaveFileName(name));

    [Theory]
    [InlineData("report.pdf", AttachmentRisk.Pdf)]
    [InlineData("REPORT.PDF", AttachmentRisk.Pdf)]
    [InlineData("notes.docx", AttachmentRisk.Ask)]
    [InlineData("photo.jpg", AttachmentRisk.Ask)]
    [InlineData("README", AttachmentRisk.Ask)]
    [InlineData("setup.exe", AttachmentRisk.Blocked)]
    [InlineData("Setup.EXE", AttachmentRisk.Blocked)]
    [InlineData("invoice.pdf.lnk", AttachmentRisk.Blocked)]
    [InlineData("run.bat", AttachmentRisk.Blocked)]
    [InlineData("run.cmd", AttachmentRisk.Blocked)]
    [InlineData("script.js", AttachmentRisk.Blocked)]
    [InlineData("page.hta", AttachmentRisk.Blocked)]
    [InlineData("tool.ps1", AttachmentRisk.Blocked)]
    [InlineData("installer.msi", AttachmentRisk.Blocked)]
    [InlineData("disk.iso", AttachmentRisk.Blocked)]
    [InlineData("link.url", AttachmentRisk.Blocked)]
    [InlineData("update.appinstaller", AttachmentRisk.Blocked)]
    [InlineData("addin.vsto", AttachmentRisk.Blocked)]
    [InlineData("extension.vsix", AttachmentRisk.Blocked)]
    [InlineData("query.iqy", AttachmentRisk.Blocked)]
    [InlineData("sheet.slk", AttachmentRisk.Blocked)]
    [InlineData("workspace.wcx", AttachmentRisk.Blocked)]
    public void ClassifiesAttachments(string name, AttachmentRisk expected) =>
        Assert.Equal(expected, LaunchPolicy.Classify(LaunchPolicy.SafeFileName(name)));

    [Theory]
    [InlineData("setup.exe.", "setup.exe")]
    [InlineData("setup.exe . . ", "setup.exe")]
    [InlineData(@"..\..\evil.bat", ".._.._evil.bat")]
    [InlineData("a/b:c*d?.txt", "a_b_c_d_.txt")]
    [InlineData("..", "attachment")]
    [InlineData("", "attachment")]
    [InlineData(null, "attachment")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("LPT1 .pdf", "_LPT1 .pdf")]
    public void MakesSafeFileNames(string? name, string expected) => Assert.Equal(expected, LaunchPolicy.SafeFileName(name));

    [Fact]
    public void TrailingDotsCannotHideAnExecutable() =>
        Assert.Equal(AttachmentRisk.Blocked, LaunchPolicy.Classify(LaunchPolicy.SafeFileName("invoice.exe...")));

    [Fact]
    public void LongNamesKeepTheirExtension()
    {
        string name = LaunchPolicy.SafeFileName(new string('x', 500) + ".exe");
        Assert.True(name.Length <= 120);
        Assert.EndsWith(".exe", name);
        Assert.Equal(AttachmentRisk.Blocked, LaunchPolicy.Classify(name));
    }
}
