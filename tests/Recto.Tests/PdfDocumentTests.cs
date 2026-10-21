using Recto.Pdf;
using Windows.Foundation;
using Windows.UI;

namespace Recto.Tests;

public sealed class PdfDocumentTests : IDisposable
{
    private static readonly Color Yellow = Color.FromArgb(255, 255, 221, 51);
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Save_AppendsAnIncrementalUpdate_KeepingTheOriginalBytes()
    {
        byte[] original = TestPdf.Create(marker: "kept-by-incremental-save");
        string path = _temp.Write("doc.pdf", original);

        using (var document = await PdfDocument.OpenAsync(path))
        {
            await document.AddNoteAsync(0, new Point(20, 20), Yellow, "hello");
            Assert.True(document.IsModified);
            await document.SaveAsync(path);
            Assert.False(document.IsModified);
        }

        byte[] saved = File.ReadAllBytes(path);
        Assert.True(saved.Length > original.Length);
        Assert.Equal(original, saved[..original.Length]);
        Assert.False(File.Exists(path + ".recto-tmp"));

        using var reopened = await PdfDocument.OpenAsync(path);
        var annotations = await reopened.GetAllAnnotationsAsync(CancellationToken.None);
        var note = Assert.Single(annotations);
        Assert.Equal(AnnotationKind.Note, note.Kind);
        Assert.Equal("hello", note.Contents);
    }

    [Fact]
    public async Task GetAllAnnotations_SkipsPagesThatCantBeLoaded()
    {
        string path = _temp.Write("doc.pdf", TestPdf.Create());
        using (var document = await PdfDocument.OpenAsync(path))
        {
            await document.AddNoteAsync(0, new Point(20, 20), Yellow, "kept");
            await document.SaveAsync(path);
        }
        // The page tree claims a second page that isn't there; the edit keeps the file's byte offsets.
        string text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));
        Assert.Contains("/Count 1", text);
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(text.Replace("/Count 1", "/Count 2")));

        using var broken = await PdfDocument.OpenAsync(path);
        Assert.Equal(2, broken.PageCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => broken.GetPageTextAsync(1));
        var note = Assert.Single(await broken.GetAllAnnotationsAsync(CancellationToken.None));
        Assert.Equal("kept", note.Contents);
    }

    [Fact]
    public async Task Save_KeepsMarkOfTheWeb()
    {
        const string zone = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/doc.pdf\r\n";
        string path = _temp.Write("doc.pdf", TestPdf.Create());
        File.WriteAllText(path + ":Zone.Identifier", zone);
        string copy = Path.Combine(_temp.Path, "copy.pdf");

        using (var document = await PdfDocument.OpenAsync(path))
        {
            Assert.False(document.Info.IsEncrypted);
            await document.AddNoteAsync(0, new Point(20, 20), Yellow, "x");
            await document.SaveAsync(copy);
            await document.SaveAsync(path);
        }

        Assert.Equal(zone, File.ReadAllText(path + ":Zone.Identifier"));
        Assert.Equal(zone, File.ReadAllText(copy + ":Zone.Identifier"));
    }

    [Fact]
    public async Task Save_InPlace_KeepsTheFilesOwnMetadata()
    {
        string path = _temp.Write("doc.pdf", TestPdf.Create());
        var created = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        File.WriteAllText(path + ":recto-test", "tag");

        using (var document = await PdfDocument.OpenAsync(path))
        {
            await document.AddNoteAsync(0, new Point(20, 20), Yellow, "x");
            await document.SaveAsync(path);
        }

        Assert.Equal(created, File.GetCreationTimeUtc(path));
        Assert.Equal("tag", File.ReadAllText(path + ":recto-test"));
        Assert.False(File.Exists(path + ".recto-tmp"));
    }

    [Fact]
    public async Task SaveAs_MarksTheDocumentSavedAndLeavesTheOriginalUntouched()
    {
        byte[] original = TestPdf.Create();
        string path = _temp.Write("doc.pdf", original);
        string copy = Path.Combine(_temp.Path, "copy.pdf");

        using var document = await PdfDocument.OpenAsync(path);
        await document.AddNoteAsync(0, new Point(20, 20), Yellow, "copy only");
        await document.SaveAsync(copy);

        Assert.False(document.IsModified);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(original, File.ReadAllBytes(copy)[..original.Length]);
    }

    [Fact]
    public async Task FailedSave_RemovesItsTemporaryFile()
    {
        string path = _temp.Write("doc.pdf", TestPdf.Create());
        // A directory can't be replaced by a file, so the final move fails after the temp file was written.
        string target = Directory.CreateDirectory(Path.Combine(_temp.Path, "target.pdf")).FullName;

        using var document = await PdfDocument.OpenAsync(path);
        await document.AddNoteAsync(0, new Point(20, 20), Yellow, "x");
        await Assert.ThrowsAnyAsync<Exception>(() => document.SaveAsync(target));

        Assert.False(File.Exists(target + ".recto-tmp"));
        Assert.True(document.IsModified);
    }

    [Fact]
    public async Task EncryptedDocument_NeedsThePassword_AndReopensWithIt()
    {
        string path = _temp.Write("locked.pdf", TestPdf.Create(userPassword: "secret"));

        var missing = await Assert.ThrowsAsync<PdfPasswordException>(() => PdfDocument.OpenAsync(path));
        Assert.False(missing.WrongPassword);
        var wrong = await Assert.ThrowsAsync<PdfPasswordException>(() => PdfDocument.OpenAsync(path, "nope"));
        Assert.True(wrong.WrongPassword);

        using (var document = await PdfDocument.OpenAsync(path, "secret"))
        {
            Assert.Equal(1, document.PageCount);
            Assert.True(document.Info.IsEncrypted);
        }
        // What a reload does: open the same file again with the password from the session.
        using var reloaded = await PdfDocument.OpenAsync(path, "secret");
        Assert.Equal(1, reloaded.PageCount);
    }

    [Fact]
    public async Task EncryptedDocument_SurvivesAnIncrementalSave()
    {
        string path = _temp.Write("locked.pdf", TestPdf.Create(userPassword: "secret"));

        using (var document = await PdfDocument.OpenAsync(path, "secret"))
        {
            await document.AddNoteAsync(0, new Point(20, 20), Yellow, "still private");
            await document.SaveAsync(path);
        }

        await Assert.ThrowsAsync<PdfPasswordException>(() => PdfDocument.OpenAsync(path));
        using var reopened = await PdfDocument.OpenAsync(path, "secret");
        var note = Assert.Single(await reopened.GetAllAnnotationsAsync(CancellationToken.None));
        Assert.Equal("still private", note.Contents);
    }

    [Fact]
    public async Task DocumentWithoutPages_IsRejected()
    {
        string path = _temp.Write("empty.pdf", TestPdf.Create(pages: 0));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => PdfDocument.OpenAsync(path));
        Assert.Contains("no pages", error.Message);
    }

    [Fact]
    public async Task HugeMediaBox_IsClampedAndStillRenders()
    {
        string path = _temp.Write("huge.pdf", TestPdf.Create(mediaBox: "0 0 50000000 20000000"));

        using var document = await PdfDocument.OpenAsync(path);
        var size = document.PageSizes[0];
        Assert.Equal(PdfDocument.MaxPageSide, size.Width, 3);
        Assert.Equal(PdfDocument.MaxPageSide * 0.4, size.Height, 3);

        using var buffer = await document.RenderAsync(0, 0.01, 0, new Windows.Graphics.RectInt32(0, 0, 144, 58), false, WorkPriority.Visible, CancellationToken.None);
        Assert.NotNull(buffer);
    }

    [Fact]
    public void ClampPageSize_KeepsNormalPages_AndPadsDegenerateOnes()
    {
        var (letter, letterScale) = PdfDocument.ClampPageSize(612, 792);
        Assert.Equal(new Size(612, 792), letter);
        Assert.Equal((1.0, 1.0), letterScale);

        var (sliver, _) = PdfDocument.ClampPageSize(1e9, 1);
        Assert.Equal(PdfDocument.MaxPageSide, sliver.Width, 3);
        Assert.Equal(1, sliver.Height, 3);
    }

    [Fact]
    public async Task RemovingAnnotations_WithStaleIndexes_RemovesTheRightOnes()
    {
        string path = _temp.Write("notes.pdf", TestPdf.Create());
        using var document = await PdfDocument.OpenAsync(path);
        await document.AddNoteAsync(0, new Point(10, 10), Yellow, "a");
        await document.AddNoteAsync(0, new Point(10, 60), Yellow, "b");
        await document.AddNoteAsync(0, new Point(10, 110), Yellow, "c");
        var before = await document.GetAllAnnotationsAsync(CancellationToken.None);
        Assert.Equal(["a", "b", "c"], before.Select(a => a.Contents));

        // Both deletions use the list read before either happened, like two quick clicks in the sidebar.
        await document.RemoveAnnotationAsync(before[0]);
        await document.RemoveAnnotationAsync(before[2]);

        var after = await document.GetAllAnnotationsAsync(CancellationToken.None);
        Assert.Equal("b", Assert.Single(after).Contents);
    }

    [Fact]
    public async Task EditingAnAnnotation_AfterAnEarlierDelete_EditsTheRightOne()
    {
        string path = _temp.Write("notes.pdf", TestPdf.Create());
        using var document = await PdfDocument.OpenAsync(path);
        await document.AddNoteAsync(0, new Point(10, 10), Yellow, "a");
        await document.AddNoteAsync(0, new Point(10, 60), Yellow, "b");
        var before = await document.GetAllAnnotationsAsync(CancellationToken.None);

        await document.RemoveAnnotationAsync(before[0]);
        await document.SetAnnotationContentsAsync(before[1], "b, edited");

        var after = await document.GetAllAnnotationsAsync(CancellationToken.None);
        Assert.Equal("b, edited", Assert.Single(after).Contents);
    }

    [Fact]
    public async Task RemovingAnAnnotationTwice_DoesNotRemoveAnother()
    {
        string path = _temp.Write("notes.pdf", TestPdf.Create());
        using var document = await PdfDocument.OpenAsync(path);
        await document.AddNoteAsync(0, new Point(10, 10), Yellow, "a");
        await document.AddNoteAsync(0, new Point(10, 60), Yellow, "b");
        var before = await document.GetAllAnnotationsAsync(CancellationToken.None);

        await document.RemoveAnnotationAsync(before[0]);
        await document.RemoveAnnotationAsync(before[0]);

        Assert.Equal("b", Assert.Single(await document.GetAllAnnotationsAsync(CancellationToken.None)).Contents);
    }

    [Fact]
    public async Task RemovedHighlight_IsPutBackAsItWas()
    {
        string path = _temp.Write("doc.pdf", TestPdf.Create());
        using var document = await PdfDocument.OpenAsync(path);
        var green = Color.FromArgb(255, 102, 204, 102);
        var added = await document.AddHighlightAsync(0, [new Rect(10, 10, 80, 12), new Rect(10, 30, 50, 12)], green);
        await document.SetAnnotationContentsAsync(added, "comment");
        // Drawing the page gives the highlight an appearance stream, after which PDFium won't read its color.
        (await document.RenderAsync(0, 1, 0, new Windows.Graphics.RectInt32(0, 0, 200, 300), false, WorkPriority.Visible, CancellationToken.None))?.Dispose();
        var before = Assert.Single(await document.GetAllAnnotationsAsync(CancellationToken.None));
        var removed = await document.RemoveAnnotationAsync(before);
        Assert.NotNull(removed);
        Assert.Empty(await document.GetAllAnnotationsAsync(CancellationToken.None));

        var restored = await document.RestoreAnnotationAsync(removed);
        var after = Assert.Single(await document.GetAllAnnotationsAsync(CancellationToken.None));
        Assert.Equal(AnnotationKind.Highlight, after.Kind);
        Assert.Equal("comment", after.Contents);
        Assert.Equal(before.Author, after.Author);
        Assert.Equal(before.Modified, after.Modified);
        Assert.Equal(before.Bounds, after.Bounds);
        Assert.Equal(green, after.Color);
        Assert.Equal(after.Bounds, restored.Bounds);

        // What was read before it was removed still finds it, so it can be removed again.
        Assert.NotNull(await document.RemoveAnnotationAsync(before));
        Assert.Empty(await document.GetAllAnnotationsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RemovedNote_IsPutBackWithItsText()
    {
        string path = _temp.Write("doc.pdf", TestPdf.Create());
        using var document = await PdfDocument.OpenAsync(path);
        var note = await document.AddNoteAsync(0, new Point(10, 10), Yellow, "remember\nthis");

        var removed = await document.RemoveAnnotationAsync(note);
        Assert.NotNull(removed);
        await document.RestoreAnnotationAsync(removed);
        await document.SaveAsync(path);

        using var reopened = await PdfDocument.OpenAsync(path);
        var restored = Assert.Single(await reopened.GetAllAnnotationsAsync(CancellationToken.None));
        Assert.Equal(AnnotationKind.Note, restored.Kind);
        Assert.Equal("remember\nthis", restored.Contents);
        Assert.Equal(note.Bounds, restored.Bounds);
    }

    [Fact]
    public async Task SettingContents_ReturnsTheTextItReplaced()
    {
        string path = _temp.Write("doc.pdf", TestPdf.Create());
        using var document = await PdfDocument.OpenAsync(path);
        var note = await document.AddNoteAsync(0, new Point(10, 10), Yellow, "a");

        Assert.Equal("a", await document.SetAnnotationContentsAsync(note, "b"));
        // Nothing to take back when the text didn't change or the note is gone.
        Assert.Null(await document.SetAnnotationContentsAsync(note, "b"));
        await document.RemoveAnnotationAsync(note);
        Assert.Null(await document.SetAnnotationContentsAsync(note, "c"));
    }

    [Fact]
    public async Task SearchText_HasPdfiumsCharacterIndexes_AndFindsWordsHyphenatedAcrossLines()
    {
        string path = _temp.Write("text.pdf", TestPdf.Create(text: "This is an exam-\nple of a search\nengine."));
        using var document = await PdfDocument.OpenAsync(path);
        string text = await document.GetSearchTextAsync(0, CancellationToken.None);
        var page = await document.GetPageTextAsync(0);
        Assert.Equal(page.Count, text.Length);

        var hyphenated = Assert.Single(TextSearch.Find(text, 0, "example", SearchOptions.JoinHyphenated));
        Assert.Equal("example", hyphenated.Match);
        var acrossLines = Assert.Single(TextSearch.Find(text, 0, "search engine", SearchOptions.None));
        Assert.Equal("search engine", acrossLines.Match);
        Assert.NotEmpty(page.Layout.SourceRangeRects(acrossLines.CharIndex, acrossLines.CharIndex + acrossLines.Length));
        Assert.Same(text, await document.GetSearchTextAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task Search_PassesOnEveryPagesHits_InOrder()
    {
        string path = _temp.Write("text.pdf", TestPdf.Create(pages: 3, text: "a search engine"));
        using var document = await PdfDocument.OpenAsync(path);

        var pages = new List<(int Page, int Hits)>();
        await foreach (var (page, hits) in document.Search("search", SearchOptions.None, CancellationToken.None).ReadAllAsync())
            pages.Add((page, hits.Count));

        Assert.Equal([(0, 1), (1, 1), (2, 1)], pages);
    }

    [Fact]
    public async Task SearchText_IsKept_WhenReadAheadOrShown()
    {
        string path = _temp.Write("text.pdf", TestPdf.Create(pages: 3, text: "Some text"));
        using var document = await PdfDocument.OpenAsync(path);
        Assert.Null(document.EstimatedTextLength);

        var page = await document.GetPageTextAsync(1);
        Assert.Equal(3L * page.Count, document.EstimatedTextLength);
        var shown = document.GetSearchTextAsync(1, CancellationToken.None);
        Assert.True(shown.IsCompletedSuccessfully);
        Assert.Equal(page.Count, shown.Result.Length);

        await document.ReadSearchTextAsync(CancellationToken.None);
        for (int i = 0; i < 3; i++) Assert.True(document.GetSearchTextAsync(i, CancellationToken.None).IsCompletedSuccessfully);
        Assert.Equal(3L * page.Count, document.EstimatedTextLength);
    }
}
