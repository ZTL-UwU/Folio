using System.Threading.Channels;

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

    /// <summary>Every page's annotations. A page that can't be loaded has none, so it doesn't hide the others'.</summary>
    public Task<List<PdfAnnotation>> GetAllAnnotationsAsync(CancellationToken token)
    {
        return Task.Run(async () =>
        {
            var all = new List<PdfAnnotation>();
            for (int i = 0; i < PageCount; i++)
            {
                int index = i;
                all.AddRange(await PdfWorker.Run(() =>
                {
                    try
                    {
                        return ReadAnnotations(index);
                    }
                    catch (InvalidDataException)
                    {
                        return [];
                    }
                }, WorkPriority.Background, token).ConfigureAwait(false));
            }
            return all;
        }, token);
    }

    /// <summary>
    /// Reads every page's search text ahead of a search, in the order searches go, while the worker has
    /// nothing else to do.
    /// </summary>
    public Task ReadSearchTextAsync(CancellationToken token)
    {
        return Task.Run(async () =>
        {
            for (int i = 0; i < PageCount; i++)
                await GetSearchTextAsync(i, token, WorkPriority.Idle).ConfigureAwait(false);
        }, token);
    }

    /// <summary>
    /// Searches every page in order, off the calling thread, passing on each page's hits (none, for most pages)
    /// as soon as it's searched. Faults with the exception that stopped it, if any.
    /// </summary>
    public ChannelReader<(int Page, List<SearchHit> Hits)> Search(string query, SearchOptions options, CancellationToken token)
    {
        var channel = Channel.CreateUnbounded<(int, List<SearchHit>)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < PageCount; i++)
                {
                    string text = await GetSearchTextAsync(i, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    channel.Writer.TryWrite((i, TextSearch.Find(text, i, query, options)));
                }
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        });
        return channel.Reader;
    }
}
