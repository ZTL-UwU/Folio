using Folio.Pdf;

namespace Folio.Tests;

public sealed class PdfDateTests
{
    [Theory]
    [InlineData("D:20240131235959+05'30'", 2024, 1, 31, 23, 59, 59, 330)]
    [InlineData("D:20240131235959-08'00'", 2024, 1, 31, 23, 59, 59, -480)]
    [InlineData("D:20240131235959Z", 2024, 1, 31, 23, 59, 59, 0)]
    [InlineData("D:20240131235959+05", 2024, 1, 31, 23, 59, 59, 300)]
    [InlineData("D:2024", 2024, 1, 1, 0, 0, 0, 0)]
    [InlineData("D:202403", 2024, 3, 1, 0, 0, 0, 0)]
    [InlineData("20240131", 2024, 1, 31, 0, 0, 0, 0)]
    [InlineData("  D:20240131120000  ", 2024, 1, 31, 12, 0, 0, 0)]
    // Out of range parts are clamped rather than rejected.
    [InlineData("D:20230231", 2023, 2, 28, 0, 0, 0, 0)]
    [InlineData("D:20241399996199", 2024, 12, 31, 23, 59, 59, 0)]
    public void Parses(string text, int year, int month, int day, int hour, int minute, int second, int offsetMinutes)
    {
        var expected = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.FromMinutes(offsetMinutes));
        var parsed = PdfDocument.ParsePdfDate(text);
        Assert.Equal(expected, parsed);
        Assert.Equal(expected.Offset, parsed!.Value.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("D:")]
    [InlineData("yesterday")]
    [InlineData("D:0000")]
    [InlineData("D:20240131235959+99'99'")]
    public void RejectsInvalid(string text) => Assert.Null(PdfDocument.ParsePdfDate(text));
}
