namespace Folio.Pdf;

// Async entry points live in a separate, safe part of the class: C# does not allow awaiting in an unsafe context.
public sealed partial class PdfDocument
{
    public static Task<PdfDocument> OpenAsync(string path, string? password = null)
    {
        return Task.Run(async () =>
        {
            var (buffer, size) = ReadFile(path);
            return await PdfWorker.Run(() => Load(path, buffer, size, password), WorkPriority.Visible).ConfigureAwait(false);
        });
    }

    public Task<List<PdfAnnotation>> GetAllAnnotationsAsync(CancellationToken token)
    {
        return Task.Run(async () =>
        {
            var all = new List<PdfAnnotation>();
            for (int i = 0; i < PageCount; i++)
            {
                int index = i;
                all.AddRange(await PdfWorker.Run(() => ReadAnnotations(index), WorkPriority.Background, token).ConfigureAwait(false));
            }
            return all;
        }, token);
    }
}
