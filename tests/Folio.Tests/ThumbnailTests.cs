using System.Runtime.InteropServices;
using Folio.Thumbnails;

namespace Folio.Tests;

/// <summary>
/// The thumbnailer calls PDFium from the test thread, so these tests mustn't run while others use it on
/// the <see cref="Folio.Pdf.PdfWorker"/> thread. (Explorer loads the handler into a process of its own.)
/// </summary>
[CollectionDefinition(nameof(ThumbnailTests), DisableParallelization = true)]
[Collection(nameof(ThumbnailTests))]
public unsafe class ThumbnailTests
{
    [Fact]
    public void FirstPageFitsTheRequestedSize()
    {
        var thumbnail = Render(TestPdf.Create(pages: 2, mediaBox: "0 0 200 300"), 256);
        var page = thumbnail.Page;
        Assert.Equal(256, thumbnail.Height);
        Assert.InRange((double)(page.Right - page.Left) / (page.Bottom - page.Top), 0.65, 0.68);
        // The page is blank, so it's the white background PDFium fills in.
        Assert.Equal(0xFFFFFFFF, thumbnail[(page.Left + page.Right) / 2, (page.Top + page.Bottom) / 2]);
    }

    [Fact]
    public void LandscapePageFitsTheRequestedWidth()
    {
        var thumbnail = Render(TestPdf.Create(mediaBox: "0 0 400 100"), 96);
        var page = thumbnail.Page;
        Assert.Equal(96, thumbnail.Width);
        Assert.InRange((double)(page.Right - page.Left) / (page.Bottom - page.Top), 3.8, 4.2);
    }

    [Fact]
    public void ShadowFallsBelowAndRightOfThePage()
    {
        var thumbnail = Render(TestPdf.Create(mediaBox: "0 0 200 300"), 256);
        var page = thumbnail.Page;
        uint below = thumbnail[(page.Left + page.Right) / 2, page.Bottom];
        uint right = thumbnail[page.Right, (page.Top + page.Bottom) / 2];
        uint above = thumbnail[(page.Left + page.Right) / 2, page.Top - 1];
        foreach (uint pixel in (uint[])[below, right, above])
        {
            // Translucent black.
            Assert.Equal(0u, pixel & 0xFFFFFF);
            Assert.InRange(pixel >> 24, 1u, 254u);
        }
        Assert.True(below >> 24 > above >> 24);
        Assert.True(thumbnail.Height - page.Bottom > page.Top);
        Assert.True(thumbnail.Width - page.Right > page.Left);
        Assert.Equal(0u, thumbnail[0, 0]);
    }

    [Fact]
    public void BlockReaderReturnsTheBytesAsked()
    {
        var data = new byte[300_000];
        new Random(1).NextBytes(data);
        var reader = new PdfThumbnail.BlockReader(new ByteStream(data), data.Length);
        var random = new Random(2);
        var buffer = new byte[100_000];
        for (int i = 0; i < 500; i++)
        {
            // Mostly small reads, some bigger than a block.
            int size = random.Next(4) == 0 ? random.Next(1, buffer.Length) : random.Next(1, 5000);
            int position = random.Next(data.Length - size + 1);
            fixed (byte* p = buffer) Assert.True(reader.Read(position, p, (uint)size));
            Assert.True(buffer.AsSpan(0, size).SequenceEqual(data.AsSpan(position, size)), $"{size} bytes at {position}");
        }
        fixed (byte* p = buffer) Assert.False(reader.Read(data.Length - 10, p, 20));
    }

    [Fact]
    public void BlockReaderServesNearbyReadsFromOneStreamRead()
    {
        // Like PDFium parsing: small reads moving through the start of the file and back to the end.
        var data = new byte[1_000_000];
        var stream = new ByteStream(data);
        var reader = new PdfThumbnail.BlockReader(stream, data.Length);
        var buffer = new byte[512];
        fixed (byte* p = buffer)
        {
            for (int position = 0; position < 50_000; position += 500)
            {
                Assert.True(reader.Read(position, p, 512));
                Assert.True(reader.Read(data.Length - 512, p, 512));
            }
        }
        Assert.Equal(2, stream.Reads);
    }

    [Fact]
    public void NoThumbnailForAPasswordProtectedPdf()
    {
        Assert.Equal(0, PdfThumbnail.Render(new ByteStream(TestPdf.Create(userPassword: "secret")), 256));
    }

    [Fact]
    public void NoThumbnailForSomethingElse()
    {
        Assert.Equal(0, PdfThumbnail.Render(new ByteStream("not a pdf"u8.ToArray()), 256));
        Assert.Equal(0, PdfThumbnail.Render(new ByteStream([]), 256));
    }

    [Fact]
    public void ProviderNeedsInitializing()
    {
        var provider = new ThumbnailProvider();
        Assert.Equal(HResult.E_UNEXPECTED, provider.GetThumbnail(256, out _, out _));
        Assert.Equal(HResult.S_OK, provider.Initialize(new ByteStream(TestPdf.Create()), 0));
        Assert.Equal(HResult.AlreadyInitialized, provider.Initialize(new ByteStream(TestPdf.Create()), 0));
        Assert.Equal(HResult.S_OK, provider.GetThumbnail(64, out nint bitmap, out _));
        Win32.DeleteObject(bitmap);
    }

    private static Thumbnail Render(byte[] pdf, int size)
    {
        nint bitmap = PdfThumbnail.Render(new ByteStream(pdf), size);
        Assert.NotEqual(0, bitmap);
        try
        {
            BITMAP info;
            Assert.NotEqual(0, GetObjectW(bitmap, sizeof(BITMAP), &info));
            Assert.Equal(32, info.BitsPixel);
            return new Thumbnail(info.Width, info.Height, new ReadOnlySpan<uint>(info.Bits, info.Width * info.Height).ToArray());
        }
        finally
        {
            Win32.DeleteObject(bitmap);
        }
    }

    /// <summary>A rendered thumbnail's BGRA pixels, top row first.</summary>
    private sealed record Thumbnail(int Width, int Height, uint[] Pixels)
    {
        public uint this[int x, int y] => Pixels[y * Width + x];

        /// <summary>The bounds of the opaque pixels (right and bottom exclusive), which is where the page is.</summary>
        public (int Left, int Top, int Right, int Bottom) Page
        {
            get
            {
                int left = Width, top = Height, right = 0, bottom = 0;
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        if (this[x, y] >> 24 != 255) continue;
                        (left, top) = (Math.Min(left, x), Math.Min(top, y));
                        (right, bottom) = (Math.Max(right, x + 1), Math.Max(bottom, y + 1));
                    }
                }
                Assert.True(right > left, "No page in the thumbnail");
                return (left, top, right, bottom);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public void* Bits;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObjectW(nint obj, int size, void* buffer);

    /// <summary>An in-memory IStream, called directly rather than through COM.</summary>
    private sealed class ByteStream(byte[] data) : IStream
    {
        private long _position;

        public int Reads { get; private set; }

        public int Read(byte* buffer, uint count, uint* read)
        {
            Reads++;
            int n = (int)Math.Clamp(data.Length - _position, 0, count);
            data.AsSpan((int)_position, n).CopyTo(new Span<byte>(buffer, n));
            _position += n;
            if (read != null) *read = (uint)n;
            return n == count ? HResult.S_OK : HResult.S_FALSE;
        }

        public int Write(byte* buffer, uint count, uint* written) => HResult.E_FAIL;

        public int Seek(long offset, int origin, ulong* position)
        {
            _position = origin == Win32.STREAM_SEEK_END ? data.Length + offset : origin == Win32.STREAM_SEEK_SET ? offset : _position + offset;
            if (position != null) *position = (ulong)_position;
            return HResult.S_OK;
        }
    }
}
