using System.Runtime.InteropServices;

namespace Folio.Thumbnails;

/// <summary>Renders the first page of a PDF into a bitmap for File Explorer.</summary>
internal static unsafe class PdfThumbnail
{
    /// <summary>
    /// Renders page 1 so its longer side is <paramref name="size"/> pixels. Returns a top-down 32-bit
    /// premultiplied ARGB HBITMAP owned by the caller, or 0 if the stream isn't a PDF that opens without a password.
    /// </summary>
    public static nint Render(IStream stream, int size)
    {
        if (size <= 0) return 0;
        ulong length;
        if (stream.Seek(0, Win32.STREAM_SEEK_END, &length) < 0 || length == 0 || length > uint.MaxValue) return 0;

        lock (Pdfium.Gate)
        {
            Pdfium.EnsureLoaded();
            // PDFium reads the file on demand, so a large PDF isn't copied into memory just for its first page.
            using var reader = new GCHandle<BlockReader>(new BlockReader(stream, (long)length));
            var access = new FPDF_FILEACCESS { FileLen = (uint)length, GetBlock = &GetBlock, Param = (void*)GCHandle<BlockReader>.ToIntPtr(reader) };
            nint document = Pdfium.FPDF_LoadCustomDocument(&access, null);
            try
            {
                return document == 0 ? 0 : RenderFirstPage(document, size);
            }
            finally
            {
                if (document != 0) Pdfium.FPDF_CloseDocument(document);
            }
        }
    }

    private static nint RenderFirstPage(nint document, int size)
    {
        nint page = Pdfium.FPDF_LoadPage(document, 0);
        if (page == 0) return 0;
        try
        {
            // Both already account for the page's /Rotate.
            float width = Pdfium.FPDF_GetPageWidthF(page), height = Pdfium.FPDF_GetPageHeightF(page);
            if (!(width > 0 && height > 0)) return 0;

            // Room for the shadow around the page: more below and to the right, where it falls.
            var shadow = new Shadow(size);
            int before = shadow.Before, after = shadow.After;
            int fit = Math.Max(1, size - before - after);
            double scale = fit / Math.Max(width, height);
            int w = Math.Max(1, (int)Math.Round(width * scale)), h = Math.Max(1, (int)Math.Round(height * scale));
            int bw = before + w + after, bh = before + h + after;

            // A negative height makes the DIB top-down, the row order PDFium writes.
            var header = new BITMAPINFOHEADER { Size = (uint)sizeof(BITMAPINFOHEADER), Width = bw, Height = -bh, Planes = 1, BitCount = 32 };
            uint* bits;
            nint hbitmap = Win32.CreateDIBSection(0, &header, 0, (void**)&bits, 0, 0);
            if (hbitmap == 0) return 0;
            shadow.Draw(bits, bw, bh, w, h);

            nint bitmap = Pdfium.FPDFBitmap_CreateEx(w, h, Pdfium.FPDFBitmap_BGRA, bits + before * bw + before, bw * 4);
            if (bitmap == 0)
            {
                Win32.DeleteObject(hbitmap);
                return 0;
            }
            Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF);
            Pdfium.FPDF_RenderPageBitmap(bitmap, page, 0, 0, w, h, 0, Pdfium.FPDF_ANNOT);
            Pdfium.FPDFBitmap_Destroy(bitmap);
            return hbitmap;
        }
        finally
        {
            Pdfium.FPDF_ClosePage(page);
        }
    }

    /// <summary>
    /// The drop shadow Explorer puts under photo thumbnails. Explorer only adds it for file types that ask
    /// for it in the registry, which a packaged app can't do, so it's drawn into the thumbnail instead.
    /// Measured from Explorer at 100%: the page's rectangle moved 1px down and right, blurred (Gaussian,
    /// sigma 1.16px), in black at 40%. Explorer asks for thumbnails about the size it shows them, so the
    /// shadow scales with the requested size from a 96px thumbnail.
    /// </summary>
    private readonly struct Shadow(int size)
    {
        private const double Opacity = 0.40;
        private readonly double _offset = size / 96.0, _sigma = 1.16 * size / 96.0;

        /// <summary>Margin above and left of the page; the shadow fades out within 2.5 sigma.</summary>
        public int Before => Math.Max(0, (int)Math.Ceiling(2.5 * _sigma - _offset));

        /// <summary>Margin below and right of the page.</summary>
        public int After => (int)Math.Ceiling(2.5 * _sigma + _offset);

        /// <summary>
        /// Draws the shadow of a <paramref name="w"/> x <paramref name="h"/> page placed at (Before, Before)
        /// into the bitmap, around the page. The page's own pixels are left for PDFium to fill.
        /// </summary>
        public void Draw(uint* bits, int bw, int bh, int w, int h)
        {
            // A blurred rectangle is the product of a blurred edge pair along each axis.
            var column = Profile(bw, w);
            var row = Profile(bh, h);
            int before = Before;
            for (int y = 0; y < bh; y++)
            {
                double alpha = 255 * Opacity * row[y];
                bool besidePage = y >= before && y < before + h;
                for (int x = 0; x < bw; x++)
                {
                    if (besidePage && x == before) x += w;
                    // Black, so premultiplied and straight alpha are the same.
                    bits[y * bw + x] = (uint)Math.Round(alpha * column[x]) << 24;
                }
            }
        }

        private double[] Profile(int length, int extent)
        {
            double start = Before + _offset, end = start + extent;
            var profile = new double[length];
            for (int i = 0; i < length; i++)
            {
                double center = i + 0.5;
                profile[i] = NormalCdf((center - start) / _sigma) - NormalCdf((center - end) / _sigma);
            }
            return profile;
        }

        private static double NormalCdf(double z) => 0.5 * (1 + Erf(z / Math.Sqrt(2)));

        /// <summary>Abramowitz and Stegun 7.1.26, accurate to 1.5e-7.</summary>
        private static double Erf(double x)
        {
            double t = 1 / (1 + 0.3275911 * Math.Abs(x));
            double y = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
            return x < 0 ? -y : y;
        }
    }

    /// <summary>PDFium's read callback: fills <paramref name="buffer"/> from <paramref name="position"/>. Returns 0 on failure.</summary>
    [UnmanagedCallersOnly]
    private static int GetBlock(void* param, uint position, byte* buffer, uint size)
    {
        try
        {
            return GCHandle<BlockReader>.FromIntPtr((nint)param).Target.Read(position, buffer, size) ? 1 : 0;
        }
        catch
        {
            // An exception can't cross back into PDFium.
            return 0;
        }
    }

    /// <summary>
    /// Reads the stream in aligned blocks and keeps the most recently used ones. The stream lives in
    /// Explorer, so every Seek and Read is a call into another process, and PDFium makes many small reads,
    /// going back and forth between a few places in the file (the cross-reference table and the objects).
    /// </summary>
    internal sealed class BlockReader(IStream stream, long length)
    {
        // Over the PDFs in a Downloads folder, this cut the calls per file from about 230 to 9, for 20% more bytes read.
        internal const int BlockSize = 64 * 1024, BlockCount = 4;

        /// <summary>Reused across documents; only touched under <see cref="Pdfium.Gate"/>.</summary>
        private static readonly byte[] Blocks = new byte[BlockSize * BlockCount];

        private readonly long[] _starts = Enumerable.Repeat(-1L, BlockCount).ToArray();
        private readonly int[] _lengths = new int[BlockCount];
        private readonly long[] _used = new long[BlockCount];
        private long _clock;
        /// <summary>Where the stream is, so a read that continues from the last one needs no Seek.</summary>
        private long _streamPosition = -1;

        public bool Read(long position, byte* buffer, uint size)
        {
            // Past the end, the last block would supply no bytes and be fetched again forever.
            if (position < 0 || position + size > length) return false;
            while (size > 0)
            {
                int slot = Find(position);
                if (slot < 0)
                {
                    // A read bigger than a block (an image, say) is passed straight through.
                    if (size >= BlockSize) return ReadStream(position, buffer, size);
                    slot = Array.IndexOf(_used, _used.Min());
                    long start = position - position % BlockSize;
                    int count = (int)Math.Min(BlockSize, length - start);
                    _starts[slot] = -1;
                    fixed (byte* block = &Blocks[slot * BlockSize])
                    {
                        if (count <= 0 || !ReadStream(start, block, (uint)count)) return false;
                    }
                    (_starts[slot], _lengths[slot]) = (start, count);
                }
                _used[slot] = ++_clock;
                int offset = (int)(position - _starts[slot]);
                int n = (int)Math.Min(size, (uint)(_lengths[slot] - offset));
                Blocks.AsSpan(slot * BlockSize + offset, n).CopyTo(new Span<byte>(buffer, n));
                position += n;
                buffer += n;
                size -= (uint)n;
            }
            return true;
        }

        private int Find(long position)
        {
            for (int i = 0; i < BlockCount; i++)
            {
                if (_starts[i] >= 0 && position >= _starts[i] && position < _starts[i] + _lengths[i]) return i;
            }
            return -1;
        }

        private bool ReadStream(long position, byte* buffer, uint size)
        {
            if (position != _streamPosition)
            {
                _streamPosition = -1;
                if (stream.Seek(position, Win32.STREAM_SEEK_SET, null) < 0) return false;
                _streamPosition = position;
            }
            while (size > 0)
            {
                uint read = 0;
                if (stream.Read(buffer, size, &read) < 0 || read == 0)
                {
                    _streamPosition = -1;
                    return false;
                }
                buffer += read;
                size -= read;
                _streamPosition += read;
            }
            return true;
        }
    }
}
