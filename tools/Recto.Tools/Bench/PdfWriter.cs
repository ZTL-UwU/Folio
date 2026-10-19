using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Recto.Tools.Bench;

/// <summary>Writes a PDF object by object. Object numbers can be reserved first and written in any order.</summary>
internal sealed class PdfWriter : IDisposable
{
    private readonly FileStream _file;
    private readonly List<long> _offsets = [0];

    public PdfWriter(string path)
    {
        _file = File.Create(path);
        // The comment of high bytes marks the file as binary for tools that guess.
        Text("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
    }

    public int Reserve()
    {
        _offsets.Add(-1);
        return _offsets.Count - 1;
    }

    public int Add(string body)
    {
        int number = Reserve();
        Write(number, body);
        return number;
    }

    public void Write(int number, string body)
    {
        _offsets[number] = _file.Position;
        Text($"{number} 0 obj\n{body}\nendobj\n");
    }

    /// <summary>Writes a stream object, Flate-compressing it unless <paramref name="filter"/> says it's already encoded.</summary>
    public void WriteStream(int number, string entries, byte[] data, string? filter = null)
    {
        if (filter is null)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal)) zlib.Write(data);
            data = compressed.ToArray();
            filter = "FlateDecode";
        }
        _offsets[number] = _file.Position;
        Text($"{number} 0 obj\n<< {entries} /Filter /{filter} /Length {data.Length} >>\nstream\n");
        _file.Write(data);
        Text("\nendstream\nendobj\n");
    }

    public int AddStream(string entries, byte[] data, string? filter = null)
    {
        int number = Reserve();
        WriteStream(number, entries, data, filter);
        return number;
    }

    /// <summary>Writes the cross-reference table and trailer.</summary>
    public void Finish(int catalog, int info)
    {
        int missing = _offsets.FindIndex(1, o => o < 0);
        if (missing > 0) throw new InvalidOperationException($"Object {missing} was reserved but never written.");
        long xref = _file.Position;
        var table = new StringBuilder($"xref\n0 {_offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in _offsets.Skip(1)) table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {_offsets.Count} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Text(table.ToString());
    }

    private void Text(string text) => _file.Write(Encoding.Latin1.GetBytes(text));

    public void Dispose() => _file.Dispose();

    /// <summary>A number as PDF content syntax: invariant culture, at most two decimals.</summary>
    public static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
