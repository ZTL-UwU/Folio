using Windows.Foundation;
using Windows.UI;

namespace Folio.Pdf;

/// <summary>Affine transform from PDF user space to "display points" (top-left origin, page rotation applied).</summary>
public readonly record struct PageTransform(double A, double B, double C, double D, double E, double F)
{
    public Point Apply(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);

    public Point Invert(double x, double y)
    {
        double det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) return new Point(x, y);
        double dx = x - E, dy = y - F;
        return new Point((D * dx - C * dy) / det, (-B * dx + A * dy) / det);
    }

    /// <summary>Maps a user-space rectangle (left, top, right, bottom) to an axis-aligned display rect.</summary>
    public Rect ApplyRect(double left, double top, double right, double bottom)
    {
        var p1 = Apply(left, top);
        var p2 = Apply(right, bottom);
        return new Rect(new Point(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y)), new Point(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y)));
    }
}

public sealed class PdfDestination
{
    public int PageIndex { get; init; } = -1;
    /// <summary>Target location in PDF user space, when the destination specifies one.</summary>
    public double? UserX { get; init; }
    public double? UserY { get; init; }
    public string? Uri { get; init; }

    public bool IsInternal => PageIndex >= 0;
}

public sealed class PdfLink
{
    public required Rect Bounds { get; init; }
    public required PdfDestination Destination { get; init; }
}

public enum AnnotationKind
{
    Note,
    Highlight,
    Underline,
    StrikeOut,
    Squiggly,
    FreeText,
    Shape,
    Ink,
    Stamp,
    Attachment,
    Other,
}

public sealed class PdfAnnotation
{
    public required int PageIndex { get; init; }
    /// <summary>Index into the page's /Annots array when this was read. Edits re-check it against the fields below.</summary>
    public required int Index { get; init; }
    /// <summary>PDFium subtype, /Rect in user space and /NM, used to find the annotation again after the array shifted.</summary>
    internal int Subtype { get; init; }
    internal Rect UserRect { get; init; }
    internal string Name { get; init; } = "";
    public required AnnotationKind Kind { get; init; }
    public required Rect Bounds { get; init; }
    public string Contents { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTimeOffset? Modified { get; init; }
    public Color Color { get; init; } = Color.FromArgb(255, 255, 214, 10);
}

public sealed class PageText
{
    public required int PageIndex { get; init; }
    public required int[] CodePoints { get; init; }
    /// <summary>Per character bounds in display points. <see cref="Rect.Empty"/> for generated characters.</summary>
    public required Rect[] Boxes { get; init; }
    public required PdfLink[] Links { get; init; }
    public required PdfAnnotation[] Annotations { get; init; }
    public required PageTransform Transform { get; init; }

    /// <summary>
    /// For each character index PDFium uses (as in search results), its index here, where characters
    /// are in reading order. Null when the orders are the same.
    /// </summary>
    public int[]? SourceIndexMap { get; init; }

    public int Count => CodePoints.Length;

    private TextLayout? _layout;
    /// <summary>The characters grouped into lines, for hit testing and highlighting.</summary>
    public TextLayout Layout => _layout ??= new TextLayout(this);

    public string GetText(int start, int end)
    {
        start = Math.Clamp(start, 0, Count);
        end = Math.Clamp(end, 0, Count);
        // A range that starts inside a surrogate pair takes the whole character.
        if (start > 0 && start < Count && IsLow(start) && IsHigh(start - 1)) start--;
        var sb = new System.Text.StringBuilder(Math.Max(0, end - start));
        for (int i = start; i < end; i++)
        {
            int cp = CodePoints[i];
            if (cp == 0xFFFE || cp == 0x02 || cp == 0) continue; // soft hyphen markers
            if (cp == '\r') continue;
            // PDFium reports UTF-16 code units, so characters outside the BMP (math italic letters, emoji)
            // arrive as two entries. Keep pairs together, even when the range ends between them.
            if (IsHigh(i))
            {
                if (i + 1 < Count && IsLow(i + 1)) sb.Append((char)cp).Append((char)CodePoints[++i]);
                continue;
            }
            if (cp is > 0 and <= 0xFFFF && !IsLow(i)) sb.Append((char)cp);
        }
        return sb.ToString();
    }

    private bool IsHigh(int i) => CodePoints[i] is >= 0xD800 and <= 0xDBFF;
    private bool IsLow(int i) => CodePoints[i] is >= 0xDC00 and <= 0xDFFF;

    public bool IsWordChar(int i) => i >= 0 && i < Count && CodePoints[i] > 32 && !char.IsWhiteSpace((char)Math.Min(CodePoints[i], 0xFFFF)) && !IsPunctuation(CodePoints[i]);

    private static bool IsPunctuation(int cp) => cp < 0x10000 && char.IsPunctuation((char)cp) && cp != '\'' && cp != '-' && cp != '_';
}

public sealed class OutlineItem
{
    public required string Title { get; init; }
    public PdfDestination? Destination { get; init; }
    public List<OutlineItem> Children { get; } = [];
}

public sealed class PdfAttachment
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public long Size { get; init; }
}

public sealed class SearchHit
{
    public required int PageIndex { get; init; }
    public required int CharIndex { get; init; }
    public required int Length { get; init; }
    public string Before { get; init; } = "";
    public string Match { get; init; } = "";
    public string After { get; init; } = "";
}

public sealed class DocumentInfo
{
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Keywords { get; init; } = "";
    public string Creator { get; init; } = "";
    public string Producer { get; init; } = "";
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string Version { get; init; } = "";
    public bool IsEncrypted { get; init; }
}

/// <summary>
/// BGRA pixels in native memory. Page bitmaps run to tens of megabytes; as managed arrays they'd
/// land on the large object heap, or stay parked in a shared pool long after a render.
/// </summary>
public sealed unsafe class PixelBuffer : IDisposable
{
    private byte* _data;

    public PixelBuffer(int width, int height)
    {
        Width = width;
        Height = height;
        _data = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)width * (nuint)height * 4);
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public int Length => Stride * Height;

    public byte* Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_data is null, this);
            return _data;
        }
    }

    public ReadOnlySpan<byte> Span => new(Pointer, Length);

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~PixelBuffer() => Free();

    private void Free()
    {
        System.Runtime.InteropServices.NativeMemory.Free(_data);
        _data = null;
    }
}

public sealed class PdfPasswordException(bool wrongPassword) : Exception(wrongPassword ? "Incorrect password" : "Password required")
{
    public bool WrongPassword { get; } = wrongPassword;
}
