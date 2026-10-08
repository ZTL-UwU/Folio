using System.ComponentModel;
using System.Runtime.CompilerServices;
using Folio.Controls;
using Folio.Pdf;
using Folio.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Folio;

public abstract class ObservableItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RecentItem(RecentDocument entry) : ObservableItem
{
    private ImageSource? _thumbnail;

    public RecentDocument Entry { get; } = entry;
    public string Name => string.IsNullOrWhiteSpace(Entry.Title) ? Entry.FileName : Entry.Title;
    public string Path => Entry.Path;

    public string Details => Humanize(Entry.LastOpened);

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            Set(ref _thumbnail, value);
            Raise(nameof(PlaceholderVisibility));
        }
    }

    public Visibility PlaceholderVisibility => _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    private static string Humanize(DateTimeOffset time)
    {
        var span = DateTimeOffset.Now - time;
        if (span.TotalMinutes < 1) return "Just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (time.Date == DateTime.Today) return $"Today, {time.LocalDateTime:t}";
        if (time.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        if (span.TotalDays < 7) return $"{(int)Math.Ceiling(span.TotalDays)} days ago";
        return time.LocalDateTime.ToString("d");
    }
}

public sealed class ThumbnailItem : ObservableItem
{
    private ImageSource? _image;
    private Brush _paper = new SolidColorBrush(Colors.White);
    private bool _isCurrent;

    public required int Index { get; init; }
    public required string Label { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public CancellationTokenSource? Pending { get; set; }

    public ImageSource? Image
    {
        get => _image;
        set => Set(ref _image, value);
    }

    public Brush Paper
    {
        get => _paper;
        set => Set(ref _paper, value);
    }

    /// <summary>Whether this is the page the viewer is on.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    public static Windows.UI.Text.FontWeight LabelWeight(bool current) => current ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
}

public sealed class AnnotationItem(PdfAnnotation annotation, string pageLabel)
{
    public PdfAnnotation Annotation { get; } = annotation;
    public string Kind => DocumentView.KindName(Annotation.Kind);
    public string Page => $"Page {pageLabel}";
    public string Contents => Annotation.Contents.Trim();
    public Visibility ContentsVisibility => string.IsNullOrWhiteSpace(Annotation.Contents) ? Visibility.Collapsed : Visibility.Visible;
    public Brush ColorBrush => new SolidColorBrush(Annotation.Color);

    public string Details => string.Join(" · ", new[]
    {
        string.IsNullOrWhiteSpace(Annotation.Author) ? null : Annotation.Author,
        Annotation.Modified?.LocalDateTime.ToString("g"),
    }.Where(s => s is not null));
}

public sealed class AttachmentItem(PdfAttachment attachment)
{
    public PdfAttachment Attachment { get; } = attachment;
    public string Name => Attachment.Name;
    public string Size => FormatSize(Attachment.Size);

    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} bytes" : $"{value:0.#} {units[unit]}";
    }
}

public sealed class SearchItem(SearchHit hit, string pageLabel)
{
    public SearchHit Hit { get; } = hit;
    public string Page { get; } = pageLabel;
}
