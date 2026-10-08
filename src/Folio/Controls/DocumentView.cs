using Folio.Pdf;
using Folio.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using ZoomMode = Folio.Services.ZoomMode;

namespace Folio.Controls;

/// <summary>
/// The scrolling, zoomable document surface. Pages are laid out at 100% (1pt = 96/72 DIP) and
/// the ScrollViewer's zoom factor is the user-visible zoom level; pages are re-rendered at the
/// effective resolution whenever zooming settles.
/// </summary>
public sealed partial class DocumentView : UserControl
{
    public const double BaseScale = 96.0 / 72.0;
    public const double MinZoom = 0.1;
    public const double MaxZoom = 10;

    private sealed record Row(int[] Pages, double Top, double Bottom);

    private readonly record struct TextPosition(int Page, int Caret) : IComparable<TextPosition>
    {
        public int CompareTo(TextPosition other) => Page != other.Page ? Page.CompareTo(other.Page) : Caret.CompareTo(other.Caret);
    }

    private readonly Grid _root = new();
    private readonly ScrollViewer _scroller;
    private readonly Canvas _host = new() { Background = new SolidColorBrush(Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _statusPill;
    private readonly TextBlock _statusText = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 };
    private readonly Dictionary<int, PageView> _views = [];
    private readonly Dictionary<int, PageText> _texts = [];
    private readonly HashSet<int> _textLoading = [];
    private readonly Dictionary<int, List<SearchHit>> _searchHits = [];
    private List<Row> _rows = [];
    private Rect[] _pageRects = [];
    private PdfDocument? _document;

    private int _rotation;
    private bool _continuous = true;
    private bool _dual;
    private bool _oddPagesLeft;
    private bool _inverted;
    private bool _presentation;
    private (bool Continuous, bool Dual, ZoomMode Mode, double Zoom) _beforePresentation;
    private ZoomMode _zoomMode = ZoomMode.FitWidth;
    private int _currentPage;
    private int _currentRow;
    private double _lastZoom = 1;
    private float? _pendingZoom;
    /// <summary>Position (in unzoomed content coordinates) of a scroll request that hasn't been applied yet.</summary>
    private double? _pendingContentX, _pendingContentY;
    private SearchHit? _currentHit;

    private TextPosition? _selectionAnchor;
    private TextPosition? _selectionFocus;
    private bool _pointerDown;
    private bool _dragging;
    private Point _pressPoint;
    private PdfLink? _pressedLink;
    private InputSystemCursorShape _cursor = InputSystemCursorShape.Arrow;

    // Middle-click scrolling: the view scrolls towards the pointer, faster the further it is from
    // where the button was pressed. A click leaves it on until the next click or key press;
    // holding the button and moving scrolls until it's released.
    private const double MiddleScrollDeadZone = 12;
    private const double MiddleScrollMarkerSize = 32;
    private readonly Canvas _middleScrollMarker = new() { Width = MiddleScrollMarkerSize, Height = MiddleScrollMarkerSize, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private bool _middleScroll;
    private bool _middleScrollHeld;
    private bool _swallowRightTap;
    private Point _middleScrollOrigin, _middleScrollPointer;
    private double _middleScrollX, _middleScrollY;
    private long _middleScrollTime;

    private readonly Brush _selectionBrush;
    private readonly Brush _searchBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xD0, 0x00));
    private readonly Brush _currentSearchBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0x80, 0x00));

    public DocumentView()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var a) && a is Color c ? c : Color.FromArgb(255, 0, 103, 192);
        _selectionBrush = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));

        _scroller = new ScrollViewer
        {
            ZoomMode = Microsoft.UI.Xaml.Controls.ZoomMode.Enabled,
            MinZoomFactor = (float)MinZoom,
            MaxZoomFactor = (float)MaxZoom,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            IsTabStop = false,
            Content = _host,
        };
        _scroller.ViewChanged += OnViewChanged;
        _scroller.SizeChanged += (_, _) => OnViewportSizeChanged();
        ScrollBarHideFix.Attach(_scroller);
        _root.Children.Add(_scroller);
        _root.Children.Add(_middleScrollMarker);

        _statusPill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12),
            Padding = new Thickness(10, 5, 10, 6),
            CornerRadius = new CornerRadius(6),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _statusText,
            Style = (Style)Application.Current.Resources["FloatingBorderStyle"],
        };
        _statusText.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _root.Children.Add(_statusPill);
        Content = _root;

        _host.PointerPressed += OnPointerPressed;
        _host.PointerMoved += OnPointerMoved;
        _host.PointerReleased += OnPointerReleased;
        _host.PointerCaptureLost += (_, _) => { _pointerDown = false; _dragging = false; };
        _host.PointerExited += (_, _) => { if (!_pointerDown && !_middleScroll) { SetStatus(null); SetCursor(InputSystemCursorShape.Arrow); } };
        _host.RightTapped += OnRightTapped;
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), true);
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnMiddleScrollPointerPressed), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnMiddleScrollPointerMoved), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnMiddleScrollPointerReleased), true);
        PointerCaptureLost += (_, _) => { if (_middleScrollHeld) StopMiddleScroll(); };
        LostFocus += (_, _) => StopMiddleScroll();
        Unloaded += (_, _) => StopMiddleScroll();

        Loaded += (_, _) =>
        {
            if (XamlRoot is not null) XamlRoot.Changed += (_, _) => RefreshRendering();
        };
    }

    // ---------------------------------------------------------------- public surface

    public event EventHandler? CurrentPageChanged;
    public event EventHandler? ZoomChanged;
    public event EventHandler? ViewSettingsChanged;
    public event EventHandler? SelectionChanged;
    /// <summary>Raised when the user edited annotations.</summary>
    public event EventHandler<int>? AnnotationsChanged;
    /// <summary>Raised when the user asked to search for the selected text.</summary>
    public event EventHandler<string>? SearchRequested;
    /// <summary>Raised with a message when an annotation edit couldn't be made.</summary>
    public event EventHandler<string>? EditFailed;

    public PdfDocument? Document => _document;
    public int CurrentPage => _currentPage;
    public double Zoom => _pendingZoom ?? _scroller.ZoomFactor;
    public ZoomMode ZoomMode => _zoomMode;
    public int PageRotation => _rotation;
    public bool IsContinuous => _continuous;
    public bool IsDual => _dual;
    public bool OddPagesLeft => _oddPagesLeft;
    public bool IsInverted => _inverted;
    public bool IsPresenting => _presentation;
    public bool HasSelection => GetSelection() is { } s && s.Start.CompareTo(s.End) < 0;
    /// <summary>Whether an open note has text that isn't in the document yet.</summary>
    public bool HasPendingEdits => _noteEdits.Any(e => e.IsChanged);

    public void SetDocument(PdfDocument? document, RecentDocument? restore = null)
    {
        StopMiddleScroll();
        _noteEdits.Clear();
        foreach (var view in _views.Values) view.Release();
        _views.Clear();
        _host.Children.Clear();
        _texts.Clear();
        _textLoading.Clear();
        _searchHits.Clear();
        _currentHit = null;
        ClearSelection();
        _document = document;
        _currentPage = 0;
        _currentRow = 0;
        _rotation = 0;
        UpdateAutomationName();
        if (document is null)
        {
            Relayout();
            return;
        }

        var prefs = AppState.Preferences;
        _continuous = restore?.Continuous ?? prefs.Continuous;
        _dual = restore?.Dual ?? prefs.Dual;
        _oddPagesLeft = prefs.OddPagesLeft;
        _inverted = prefs.Inverted;
        _zoomMode = restore?.ZoomMode ?? ZoomMode.FitWidth;
        _rotation = restore?.Rotation ?? 0;
        int page = Math.Clamp(restore?.Page ?? 0, 0, Math.Max(0, document.PageCount - 1));
        _currentPage = page;
        UpdateAutomationName();
        Relayout();
        UpdateLayout();
        if (_zoomMode == ZoomMode.Custom) SetZoom(restore?.Zoom ?? 1, keepMode: true);
        else ApplyZoomMode();
        GoToPage(page);
        UpdateRealization(true);
    }

    public void SetContinuous(bool value)
    {
        if (_continuous == value) return;
        _continuous = value;
        int page = _currentPage;
        _currentRow = RowIndexOf(page);
        Relayout();
        GoToPage(page);
        ViewSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetDual(bool value)
    {
        if (_dual == value) return;
        _dual = value;
        RelayoutKeepingPage();
    }

    public void SetOddPagesLeft(bool value)
    {
        if (_oddPagesLeft == value) return;
        _oddPagesLeft = value;
        RelayoutKeepingPage();
    }

    public void SetInverted(bool value)
    {
        if (_inverted == value) return;
        _inverted = value;
        foreach (var view in _views.Values) view.SetPaperColor(value ? Color.FromArgb(255, 30, 30, 30) : Colors.White);
        RefreshRendering();
        ViewSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Rotate(int delta)
    {
        _rotation = ((_rotation + delta) % 360 + 360) % 360;
        RelayoutKeepingPage();
    }

    private void RelayoutKeepingPage()
    {
        int page = _currentPage;
        Relayout();
        UpdateLayout();
        ApplyZoomMode();
        GoToPage(page);
        ViewSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetZoomMode(ZoomMode mode)
    {
        _zoomMode = mode;
        ApplyZoomMode();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomIn() => SetZoom(NextZoomStep(Zoom, +1));
    public void ZoomOut() => SetZoom(NextZoomStep(Zoom, -1));

    private static readonly double[] ZoomSteps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6, 8, 10];

    private static double NextZoomStep(double current, int direction)
    {
        if (direction > 0) return ZoomSteps.FirstOrDefault(s => s > current * 1.01, MaxZoom);
        return ZoomSteps.LastOrDefault(s => s < current * 0.99, MinZoom);
    }

    public void SetZoom(double zoom, Point? anchor = null, bool keepMode = false)
    {
        if (!keepMode) _zoomMode = ZoomMode.Custom;
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        double vw = _scroller.ViewportWidth, vh = _scroller.ViewportHeight;
        var a = anchor ?? new Point(vw / 2, vh / 2);
        double z = _scroller.ZoomFactor;
        double cx = (_scroller.HorizontalOffset + a.X) / z;
        double cy = (_scroller.VerticalOffset + a.Y) / z;
        _pendingZoom = (float)zoom;
        ScrollTo(Math.Max(0, cx * zoom - a.X), Math.Max(0, cy * zoom - a.Y), (float)zoom, true);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public void GoToPage(int index, Point? location = null)
    {
        if (_document is null) return;
        index = Math.Clamp(index, 0, _document.PageCount - 1);
        if (!_continuous || _presentation)
        {
            int row = RowIndexOf(index);
            if (row != _currentRow || _pageRects[index].IsEmpty)
            {
                _currentRow = row;
                Relayout();
                UpdateLayout();
                if (_zoomMode != ZoomMode.Custom) ApplyZoomMode();
            }
        }
        var rect = _pageRects[index];
        if (rect.IsEmpty) return;
        double z = Zoom;
        double? h = null;
        double v;
        if (location is { } loc)
        {
            var p = RotatedLayoutPoint(index, loc);
            v = (rect.Top + p.Y) * z - 12;
            if (rect.Width * z > _scroller.ViewportWidth)
                h = Math.Max(0, (rect.Left + p.X) * z - 24);
        }
        else
        {
            // First row: scroll to the very top so the full top margin shows.
            v = (rect.Top <= TopMargin + 1 ? 0 : rect.Top - (_presentation ? 0 : Spacing)) * z;
        }
        ScrollTo(h, Math.Max(0, v), null, true);
        SetCurrentPage(index);
    }

    public async void GoToDestination(PdfDestination destination)
    {
        if (_document is null) return;
        if (!destination.IsInternal)
        {
            if (destination.Uri is { } uri) await LaunchUriAsync(uri);
            return;
        }
        if (destination.UserY is null && destination.UserX is null)
        {
            GoToPage(destination.PageIndex);
            return;
        }
        try
        {
            var t = await _document.GetTransformAsync(destination.PageIndex);
            var p = t.Apply(destination.UserX ?? 0, destination.UserY ?? 0);
            var size = _document.PageSizes[destination.PageIndex];
            GoToPage(destination.PageIndex, new Point(
                destination.UserX is null ? 0 : Math.Clamp(p.X, 0, size.Width),
                destination.UserY is null ? 0 : Math.Clamp(p.Y, 0, size.Height)));
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void NextPage()
    {
        if (_document is null) return;
        if (_dual && (!_continuous || _presentation))
        {
            if (_currentRow + 1 < _rows.Count) GoToPage(_rows[_currentRow + 1].Pages[0]);
            return;
        }
        int next = _dual ? _rows[Math.Min(_rows.Count - 1, RowIndexOf(_currentPage) + 1)].Pages[0] : _currentPage + 1;
        if (next < _document.PageCount) GoToPage(next);
    }

    public void PreviousPage()
    {
        if (_document is null) return;
        if (_dual)
        {
            int row = RowIndexOf(_currentPage);
            if (row > 0) GoToPage(_rows[row - 1].Pages[0]);
            else GoToPage(0);
            return;
        }
        if (_currentPage > 0) GoToPage(_currentPage - 1);
        else GoToPage(0);
    }

    public void SetPresentation(bool value)
    {
        if (_presentation == value || _document is null) return;
        StopMiddleScroll();
        int page = _currentPage;
        if (value)
        {
            _beforePresentation = (_continuous, _dual, _zoomMode, Zoom);
            _presentation = true;
            _zoomMode = ZoomMode.FitPage;
            _dual = false;
            _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            _host.VerticalAlignment = VerticalAlignment.Center;
            _root.Background = new SolidColorBrush(Colors.Black);
            ClearSelection();
        }
        else
        {
            _presentation = false;
            (_continuous, _dual, _zoomMode, double zoom) = _beforePresentation;
            _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _host.VerticalAlignment = VerticalAlignment.Top;
            _root.Background = null;
            _lastZoom = zoom;
        }
        foreach (var view in _views.Values) view.Release();
        _views.Clear();
        _host.Children.Clear();
        _currentRow = RowIndexOf(page);
        Relayout();
        UpdateLayout();
        if (_zoomMode == ZoomMode.Custom) SetZoom(_lastZoom, keepMode: true);
        else ApplyZoomMode();
        GoToPage(page);
        Focus(FocusState.Programmatic);
    }

    /// <summary>Called after the document was saved/reloaded or annotations changed on a page.</summary>
    public void InvalidatePage(int index)
    {
        _texts.Remove(index);
        if (_views.TryGetValue(index, out var view))
        {
            view.Invalidate();
            EnsureText(index);
        }
        RefreshRendering();
    }

    // ---------------------------------------------------------------- search

    public void AddSearchResults(IEnumerable<SearchHit> hits)
    {
        var touched = new HashSet<int>();
        foreach (var hit in hits)
        {
            if (!_searchHits.TryGetValue(hit.PageIndex, out var list)) _searchHits[hit.PageIndex] = list = [];
            list.Add(hit);
            touched.Add(hit.PageIndex);
        }
        foreach (var page in touched) UpdateOverlay(page);
    }

    public void ClearSearch()
    {
        _searchHits.Clear();
        _currentHit = null;
        UpdateAllOverlays();
    }

    public async void ShowSearchHit(SearchHit hit)
    {
        var previous = _currentHit;
        _currentHit = hit;
        if (previous is not null) UpdateOverlay(previous.PageIndex);
        var text = await GetTextAsync(hit.PageIndex);
        if (text is null || _currentHit != hit) return;
        var bounds = Union(HitRects(text, hit.CharIndex, hit.CharIndex + hit.Length));
        EnsurePageVisible(hit.PageIndex, bounds);
        UpdateOverlay(hit.PageIndex);
    }

    private void EnsurePageVisible(int index, Rect displayBounds)
    {
        if (!_continuous || _presentation) GoToPage(index);
        var rect = _pageRects[index];
        if (rect.IsEmpty || displayBounds.IsEmpty) { GoToPage(index); return; }
        var tl = RotatedLayoutPoint(index, new Point(displayBounds.Left, displayBounds.Top));
        var br = RotatedLayoutPoint(index, new Point(displayBounds.Right, displayBounds.Bottom));
        var target = new Rect(new Point(rect.Left + tl.X, rect.Top + tl.Y), new Point(rect.Left + br.X, rect.Top + br.Y));
        double z = Zoom;
        double vw = _scroller.ViewportWidth / z, vh = _scroller.ViewportHeight / z;
        double left = _scroller.HorizontalOffset / z, top = _scroller.VerticalOffset / z;
        double? h = null, v = null;
        if (target.Top < top + 20 || target.Bottom > top + vh - 20) v = (target.Top - vh / 3) * z;
        if (target.Left < left || target.Right > left + vw) h = (target.Left - vw / 3) * z;
        if (h is not null || v is not null) ScrollTo(h is null ? null : Math.Max(0, h.Value), v is null ? null : Math.Max(0, v.Value), null, true);
        SetCurrentPage(index);
    }

    // ---------------------------------------------------------------- layout

    // Space around the pages (in 100% layout DIPs, so it scales with zoom).
    private double SideMargin => _presentation ? 0 : 48;
    private double TopMargin => _presentation ? 0 : 28;
    private double Spacing => _presentation ? 0 : 14;

    private Size LayoutSize(int index)
    {
        var s = _document!.PageSizes[index];
        return _rotation is 90 or 270 ? new Size(s.Height * BaseScale, s.Width * BaseScale) : new Size(s.Width * BaseScale, s.Height * BaseScale);
    }

    private Point RotatedLayoutPoint(int index, Point display)
    {
        var s = _document!.PageSizes[index];
        var q = _rotation switch
        {
            90 => new Point(s.Height - display.Y, display.X),
            180 => new Point(s.Width - display.X, s.Height - display.Y),
            270 => new Point(display.Y, s.Width - display.X),
            _ => display,
        };
        return new Point(q.X * BaseScale, q.Y * BaseScale);
    }

    private List<int[]> BuildRowPages()
    {
        var rows = new List<int[]>();
        int n = _document?.PageCount ?? 0;
        if (!_dual)
        {
            for (int i = 0; i < n; i++) rows.Add([i]);
            return rows;
        }
        int start = 0;
        if (!_oddPagesLeft && n > 0)
        {
            rows.Add([0]);
            start = 1;
        }
        for (int i = start; i < n; i += 2)
            rows.Add(i + 1 < n ? [i, i + 1] : [i]);
        return rows;
    }

    private bool IsLeftPage(int index) => _oddPagesLeft ? index % 2 == 0 : index % 2 == 1;

    private int RowIndexOf(int page)
    {
        if (!_dual) return page;
        if (_oddPagesLeft) return page / 2;
        return page == 0 ? 0 : (page + 1) / 2;
    }

    private void Relayout()
    {
        if (_document is null)
        {
            _rows = [];
            _pageRects = [];
            _host.Width = 0;
            _host.Height = 0;
            return;
        }
        int n = _document.PageCount;
        var rowPages = BuildRowPages();
        _pageRects = new Rect[n];
        for (int i = 0; i < n; i++) _pageRects[i] = Rect.Empty;

        // Widths of the left and right columns so that spreads share a common spine.
        double maxLeft = 0, maxRight = 0, maxSingle = 0;
        foreach (var pages in rowPages)
        {
            foreach (var p in pages)
            {
                var s = LayoutSize(p);
                if (!_dual) maxSingle = Math.Max(maxSingle, s.Width);
                else if (IsLeftPage(p)) maxLeft = Math.Max(maxLeft, s.Width);
                else maxRight = Math.Max(maxRight, s.Width);
            }
        }
        double contentWidth = _dual ? SideMargin * 2 + maxLeft + Spacing + maxRight : SideMargin * 2 + maxSingle;
        double spine = SideMargin + maxLeft + Spacing / 2;

        _currentRow = Math.Clamp(_currentRow, 0, Math.Max(0, rowPages.Count - 1));
        var rows = new List<Row>(rowPages.Count);
        double y = TopMargin;
        for (int r = 0; r < rowPages.Count; r++)
        {
            var pages = rowPages[r];
            bool laidOut = (_continuous && !_presentation) || r == _currentRow;
            double height = pages.Max(p => LayoutSize(p).Height);
            if (!laidOut)
            {
                rows.Add(new Row(pages, double.NaN, double.NaN));
                continue;
            }
            foreach (var p in pages)
            {
                var s = LayoutSize(p);
                double x = !_dual ? (contentWidth - s.Width) / 2 : IsLeftPage(p) ? spine - Spacing / 2 - s.Width : spine + Spacing / 2;
                _pageRects[p] = new Rect(Math.Round(x), Math.Round(y + (height - s.Height) / 2), s.Width, s.Height);
            }
            rows.Add(new Row(pages, y, y + height));
            y += height + Spacing;
        }
        _rows = rows;
        _host.Width = Math.Ceiling(contentWidth);
        _host.Height = Math.Ceiling(y - Spacing + TopMargin);

        foreach (var (index, view) in _views.ToList())
        {
            if (_pageRects[index].IsEmpty)
            {
                RemoveView(index);
                continue;
            }
            view.Arrange(_pageRects[index], BaseScale, _rotation);
            UpdateOverlay(index);
        }
        // In paged modes the scroll offset often stays the same, so no ViewChanged will follow.
        UpdateRealization(true);
    }

    private double FitWidthZoom()
    {
        if (_document is null || _host.Width <= 0) return 1;
        double vw = _scroller.ViewportWidth > 0 ? _scroller.ViewportWidth : ActualWidth;
        return Math.Clamp(vw / _host.Width, MinZoom, MaxZoom);
    }

    private double FitPageZoom()
    {
        if (_document is null || _rows.Count == 0) return 1;
        double vh = _scroller.ViewportHeight > 0 ? _scroller.ViewportHeight : ActualHeight;
        var row = _rows[Math.Clamp(RowIndexOf(_currentPage), 0, _rows.Count - 1)];
        double height = row.Pages.Max(p => LayoutSize(p).Height) + TopMargin * 2;
        return Math.Clamp(Math.Min(FitWidthZoom(), vh / height), MinZoom, MaxZoom);
    }

    private void ApplyZoomMode()
    {
        if (_document is null || _zoomMode == ZoomMode.Custom || _scroller.ViewportWidth <= 0) return;
        double target = _zoomMode == ZoomMode.FitWidth ? FitWidthZoom() : FitPageZoom();
        if (Math.Abs(target - Zoom) < 0.0005) return;
        // Keep the top of the current viewport stable.
        double cy = _pendingContentY ?? _scroller.VerticalOffset / _scroller.ZoomFactor;
        _pendingZoom = (float)target;
        ScrollTo(0, cy * target, (float)target, true);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewportSizeChanged()
    {
        StopMiddleScroll();
        if (_zoomMode != ZoomMode.Custom) ApplyZoomMode();
        UpdateRealization(true);
    }

    // ---------------------------------------------------------------- realization

    private void ScrollTo(double? h, double? v, float? zoom, bool disableAnimation)
    {
        // A later ChangeView without a zoom factor would cancel a pending zoom change.
        zoom ??= _pendingZoom;
        float factor = _scroller.ZoomFactor;
        bool sameZoom = Math.Abs((zoom ?? factor) - factor) < 1e-4;
        bool sameH = h is null || Math.Abs(Math.Clamp(h.Value, 0, _scroller.ScrollableWidth) - _scroller.HorizontalOffset) < 0.5;
        bool sameV = v is null || Math.Abs(Math.Clamp(v.Value, 0, _scroller.ScrollableHeight) - _scroller.VerticalOffset) < 0.5;
        if (sameZoom && sameH && sameV)
        {
            // Nothing changes, so no ViewChanged would arrive to clear the pending view.
            _pendingZoom = null;
            _pendingContentX = _pendingContentY = null;
        }
        else
        {
            if (h is { } x) _pendingContentX = x / (zoom ?? factor);
            if (v is { } y) _pendingContentY = y / (zoom ?? factor);
        }
        _scroller.ChangeView(h, v, zoom, disableAnimation);
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        float zoom = _scroller.ZoomFactor;
        if (!e.IsIntermediate)
        {
            _pendingContentX = _pendingContentY = null;
            if (_pendingZoom is not null)
            {
                _pendingZoom = null;
            }
            else if (Math.Abs(zoom - _lastZoom) > 0.001 && _zoomMode != ZoomMode.Custom)
            {
                // The user zoomed with a gesture or Ctrl+wheel.
                _zoomMode = ZoomMode.Custom;
            }
        }
        if (Math.Abs(zoom - _lastZoom) > 0.0001)
        {
            StopMiddleScroll();
            _lastZoom = zoom;
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
        UpdateRealization(!e.IsIntermediate);
        UpdateCurrentPage();
    }

    private void RefreshRendering() => UpdateRealization(true);

    /// <summary>
    /// The part of the content in view, or that will be once a pending scroll or zoom lands, so
    /// pages aren't rendered for a view that's about to be replaced (such as the first one on open).
    /// </summary>
    private Rect VisibleContentRect()
    {
        double factor = Math.Max(0.01, _scroller.ZoomFactor);
        double z = Math.Max(0.01, Zoom);
        double vw = (_scroller.ViewportWidth > 0 ? _scroller.ViewportWidth : ActualWidth) / z;
        double vh = (_scroller.ViewportHeight > 0 ? _scroller.ViewportHeight : ActualHeight) / z;
        // When the content is narrower than the viewport it's centred, offsets are 0 then.
        double x = Math.Clamp(_pendingContentX ?? _scroller.HorizontalOffset / factor, 0, Math.Max(0, _host.Width - vw));
        double y = Math.Clamp(_pendingContentY ?? _scroller.VerticalOffset / factor, 0, Math.Max(0, _host.Height - vh));
        return new Rect(x, y, vw, vh);
    }

    private IEnumerable<int> PagesIn(Rect area)
    {
        if (_document is null) yield break;
        // Rows are sorted by Top; binary search the first candidate.
        int lo = 0, hi = _rows.Count - 1, first = _rows.Count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var row = _rows[mid];
            if (double.IsNaN(row.Bottom) || row.Bottom >= area.Top) { first = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        if (!_continuous || _presentation) first = 0;
        for (int r = first; r < _rows.Count; r++)
        {
            var row = _rows[r];
            if (double.IsNaN(row.Top)) continue;
            if (row.Top > area.Bottom) break;
            foreach (var p in row.Pages)
            {
                var rect = _pageRects[p];
                if (!rect.IsEmpty && rect.Right >= area.Left && rect.Left <= area.Right) yield return p;
            }
        }
    }

    private void UpdateRealization(bool allowRescale)
    {
        if (_document is null || XamlRoot is null) return;
        var visible = VisibleContentRect();
        var prefetch = new Rect(visible.X, visible.Y - visible.Height * 0.75, visible.Width, visible.Height * 2.5);
        var keep = new Rect(visible.X - visible.Width, visible.Y - visible.Height * 1.5, visible.Width * 3, visible.Height * 4);
        var wanted = PagesIn(prefetch).Take(80).ToHashSet();
        var keepSet = PagesIn(keep).ToHashSet();

        foreach (var index in _views.Keys.ToList())
        {
            if (!keepSet.Contains(index)) RemoveView(index);
        }

        double pixelScale = Zoom * XamlRoot.RasterizationScale;
        foreach (var index in wanted)
        {
            if (!_views.TryGetValue(index, out var view))
            {
                view = new PageView(_document, index, framed: !_presentation);
                if (_inverted) view.SetPaperColor(Color.FromArgb(255, 30, 30, 30));
                view.Arrange(_pageRects[index], BaseScale, _rotation);
                _views[index] = view;
                _host.Children.Add(view);
                EnsureText(index);
                UpdateOverlay(index);
            }
            var rect = _pageRects[index];
            var inter = RectHelper.Intersect(rect, visible);
            bool isVisible = !inter.IsEmpty && inter.Width > 0 && inter.Height > 0;
            var local = isVisible ? new Rect(inter.X - rect.X, inter.Y - rect.Y, inter.Width, inter.Height) : Rect.Empty;
            if (allowRescale || !HasBitmap(view)) view.Render(pixelScale, local, _inverted, isVisible);
        }
        if (allowRescale) SnapPages();
    }

    private void SnapPages()
    {
        if (XamlRoot is null || _views.Count == 0) return;
        double raster = XamlRoot.RasterizationScale;
        double pixelScale = _scroller.ZoomFactor * raster;
        try
        {
            // TransformToVisual on the content ignores the scroller's zoom and offsets, so place the
            // pages by hand. The compositor rounds both the centring of content smaller than the
            // viewport and the scroll offsets to whole pixels.
            var origin = _scroller.TransformToVisual(null).TransformPoint(new Point(0, 0));
            double z = _scroller.ZoomFactor;
            double cx = Math.Max(0, (_scroller.ViewportWidth - _host.ActualWidth * z) / 2);
            double cy = _host.VerticalAlignment == VerticalAlignment.Center ? Math.Max(0, (_scroller.ViewportHeight - _host.ActualHeight * z) / 2) : 0;
            double ox = Math.Floor(cx * raster + 0.5) - Math.Round(_scroller.HorizontalOffset * raster);
            double oy = Math.Floor(cy * raster + 0.5) - Math.Round(_scroller.VerticalOffset * raster);
            foreach (var (index, view) in _views)
            {
                var r = _pageRects[index];
                double px = (origin.X + r.X * z) * raster + ox;
                double py = (origin.Y + r.Y * z) * raster + oy;
                view.SnapToPixels(new Point(px, py), pixelScale);
            }
        }
        catch (ArgumentException)
        {
        }
    }

    private static bool HasBitmap(PageView view) => view.Children.OfType<Image>().Any(i => i.Source is not null);

    private void RemoveView(int index)
    {
        if (!_views.Remove(index, out var view)) return;
        view.Release();
        _host.Children.Remove(view);
    }

    private void UpdateCurrentPage()
    {
        if (_document is null) return;
        var visible = VisibleContentRect();
        int best = -1;
        double bestArea = 0;
        foreach (var p in PagesIn(visible))
        {
            var inter = RectHelper.Intersect(_pageRects[p], visible);
            if (inter.IsEmpty) continue;
            // Prefer the page that fills most of the viewport height; ties go to the earlier page.
            double score = inter.Height * Math.Min(1, inter.Width / Math.Max(1, _pageRects[p].Width));
            if (score > bestArea + 1) { bestArea = score; best = p; }
        }
        if (best >= 0) SetCurrentPage(best);
    }

    private void SetCurrentPage(int index)
    {
        if (_currentPage == index) return;
        _currentPage = index;
        UpdateAutomationName();
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Gives screen readers at least the position in the document. Page text isn't exposed yet.</summary>
    private void UpdateAutomationName()
    {
        AutomationProperties.SetName(this, _document is null
            ? "Document"
            : $"Document, page {_document.GetPageLabel(_currentPage)}, {_currentPage + 1} of {_document.PageCount}");
    }

    // ---------------------------------------------------------------- text data

    private async void EnsureText(int index)
    {
        if (_document is null || _texts.ContainsKey(index) || !_textLoading.Add(index)) return;
        var document = _document;
        try
        {
            var text = await document.GetPageTextAsync(index);
            if (document != _document) return;
            _texts[index] = text;
            if (_texts.Count > 64)
            {
                foreach (var key in _texts.Keys.Where(k => !_views.ContainsKey(k)).Take(_texts.Count - 48).ToList()) _texts.Remove(key);
            }
            UpdateOverlay(index);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException)
        {
        }
        finally
        {
            _textLoading.Remove(index);
        }
    }

    private async Task<PageText?> GetTextAsync(int index)
    {
        if (_document is null) return null;
        if (_texts.TryGetValue(index, out var text)) return text;
        var document = _document;
        try
        {
            text = await document.GetPageTextAsync(index, WorkPriority.Visible);
            if (document != _document) return null;
            _texts[index] = text;
            return text;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- overlays

    private void UpdateAllOverlays()
    {
        foreach (var index in _views.Keys) UpdateOverlay(index);
    }

    private void UpdateOverlay(int index)
    {
        if (!_views.TryGetValue(index, out var view)) return;
        var shapes = new List<(Rect, Brush)>();
        if (_texts.TryGetValue(index, out var text))
        {
            if (_searchHits.TryGetValue(index, out var hits))
            {
                foreach (var hit in hits)
                {
                    var brush = _currentHit is { } c && c.PageIndex == hit.PageIndex && c.CharIndex == hit.CharIndex ? _currentSearchBrush : _searchBrush;
                    foreach (var r in HitRects(text, hit.CharIndex, hit.CharIndex + hit.Length)) shapes.Add((Inflate(r, 1), brush));
                }
            }
            if (GetSelection() is { } sel && index >= sel.Start.Page && index <= sel.End.Page)
            {
                int start = index == sel.Start.Page ? sel.Start.Caret : 0;
                int end = index == sel.End.Page ? sel.End.Caret : text.Count;
                foreach (var r in HitRects(text, start, end)) shapes.Add((r, _selectionBrush));
            }
        }
        view.SetOverlay(shapes);
    }

    private static Rect Inflate(Rect r, double d) => new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    private static Rect Union(IEnumerable<Rect> rects)
    {
        var result = Rect.Empty;
        foreach (var r in rects) result = result.IsEmpty ? r : RectHelper.Union(result, r);
        return result;
    }

    /// <summary>Merges character boxes in [start, end) into one rectangle per line.</summary>
    private static List<Rect> HitRects(PageText text, int start, int end)
    {
        var result = new List<Rect>();
        Rect current = Rect.Empty;
        for (int i = Math.Max(0, start); i < Math.Min(end, text.Count); i++)
        {
            var box = text.Boxes[i];
            if (box.IsEmpty) continue;
            if (current.IsEmpty)
            {
                current = box;
                continue;
            }
            double overlap = Math.Min(current.Bottom, box.Bottom) - Math.Max(current.Top, box.Top);
            bool sameLine = overlap > Math.Min(current.Height, box.Height) * 0.5 && box.Left >= current.Left - 2;
            if (sameLine) current = RectHelper.Union(current, box);
            else
            {
                result.Add(current);
                current = box;
            }
        }
        if (!current.IsEmpty) result.Add(current);
        return result;
    }

    // ---------------------------------------------------------------- selection & pointer

    private (TextPosition Start, TextPosition End)? GetSelection()
    {
        if (_selectionAnchor is not { } a || _selectionFocus is not { } f) return null;
        return a.CompareTo(f) <= 0 ? (a, f) : (f, a);
    }

    public void ClearSelection()
    {
        _selectionFlyout?.Hide();
        _selectionFlyout = null;
        bool had = _selectionAnchor is not null;
        var old = GetSelection();
        _selectionAnchor = null;
        _selectionFocus = null;
        if (old is { } s)
        {
            for (int p = s.Start.Page; p <= s.End.Page; p++) UpdateOverlay(p);
        }
        if (had) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll()
    {
        if (_document is null) return;
        _selectionAnchor = new TextPosition(0, 0);
        _selectionFocus = new TextPosition(_document.PageCount - 1, int.MaxValue);
        UpdateAllOverlays();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<string> GetSelectedTextAsync()
    {
        if (GetSelection() is not { } sel) return "";
        var sb = new System.Text.StringBuilder();
        for (int p = sel.Start.Page; p <= sel.End.Page; p++)
        {
            var text = await GetTextAsync(p);
            if (text is null) continue;
            int start = p == sel.Start.Page ? sel.Start.Caret : 0;
            int end = p == sel.End.Page ? sel.End.Caret : text.Count;
            sb.Append(text.GetText(start, end));
            if (p != sel.End.Page && sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
        }
        return sb.ToString();
    }

    public async void CopySelection()
    {
        string text = await GetSelectedTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private (int Page, Point Display)? HitPage(Point content)
    {
        foreach (var (index, view) in _views)
        {
            var rect = _pageRects[index];
            if (!rect.IsEmpty && rect.Contains(content))
                return (index, view.FromView(new Point(content.X - rect.X, content.Y - rect.Y)));
        }
        return null;
    }

    /// <summary>Finds the page nearest to a point, clamping the point into the page.</summary>
    private (int Page, Point Display)? HitPageNearest(Point content)
    {
        if (HitPage(content) is { } hit) return hit;
        int best = -1;
        double bestDistance = double.MaxValue;
        foreach (var (index, _) in _views)
        {
            var r = _pageRects[index];
            double dx = Math.Max(0, Math.Max(r.Left - content.X, content.X - r.Right));
            double dy = Math.Max(0, Math.Max(r.Top - content.Y, content.Y - r.Bottom));
            double d = dx * dx + dy * dy;
            if (d < bestDistance) { bestDistance = d; best = index; }
        }
        if (best < 0) return null;
        var rect = _pageRects[best];
        var clamped = new Point(Math.Clamp(content.X, rect.Left, rect.Right), Math.Clamp(content.Y, rect.Top, rect.Bottom));
        return (best, _views[best].FromView(new Point(clamped.X - rect.X, clamped.Y - rect.Y)));
    }

    private static int CharAt(PageText text, Point p, double tolerance = 0)
    {
        for (int i = 0; i < text.Count; i++)
        {
            var b = text.Boxes[i];
            if (b.IsEmpty) continue;
            if (p.X >= b.Left - tolerance && p.X <= b.Right + tolerance && p.Y >= b.Top - tolerance && p.Y <= b.Bottom + tolerance) return i;
        }
        return -1;
    }

    private static int CaretAt(PageText text, Point p)
    {
        int index = CharAt(text, p);
        if (index < 0)
        {
            // Nearest character, weighting vertical distance so we stay on the same line.
            double best = double.MaxValue;
            for (int i = 0; i < text.Count; i++)
            {
                var b = text.Boxes[i];
                if (b.IsEmpty) continue;
                double dx = Math.Max(0, Math.Max(b.Left - p.X, p.X - b.Right));
                double dy = Math.Max(0, Math.Max(b.Top - p.Y, p.Y - b.Bottom));
                double d = dx + dy * 4;
                if (d < best) { best = d; index = i; }
            }
            if (index < 0) return 0;
        }
        var box = text.Boxes[index];
        return p.X > box.Left + box.Width / 2 ? index + 1 : index;
    }

    private PdfLink? LinkAt(int page, Point display)
    {
        if (!_texts.TryGetValue(page, out var text)) return null;
        foreach (var link in text.Links)
        {
            if (link.Bounds.Contains(display)) return link;
        }
        return null;
    }

    private PdfAnnotation? AnnotationAt(int page, Point display)
    {
        if (!_texts.TryGetValue(page, out var text)) return null;
        // Topmost first.
        for (int i = text.Annotations.Length - 1; i >= 0; i--)
        {
            var a = text.Annotations[i];
            if (a.Kind is AnnotationKind.Note or AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut or AnnotationKind.Squiggly or AnnotationKind.FreeText
                && a.Bounds.Contains(display)) return a;
        }
        return null;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        // OnMiddleScrollPointerPressed ends middle-click scrolling instead.
        if (_middleScroll) return;
        var point = e.GetCurrentPoint(_host);
        if (point.PointerDeviceType == PointerDeviceType.Touch) return;
        if (_presentation)
        {
            if (point.Properties.IsLeftButtonPressed) NextPage();
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        _selectUnit = 0;
        int clicks = CountClick(point.Position);
        if (clicks >= 2 && (e.KeyModifiers & VirtualKeyModifiers.Shift) == 0 && HitPage(point.Position) is { } unit
            && LinkAt(unit.Page, unit.Display) is null && SelectUnit(unit.Page, unit.Display, clicks))
        {
            // Double click selects a word, triple click a line; dragging then extends by that unit.
            _pressPoint = point.Position;
            _pointerDown = true;
            _dragging = false;
            _pressedLink = null;
            _host.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }
        _pressPoint = point.Position;
        _pointerDown = true;
        _dragging = false;
        _pressedLink = null;
        if (HitPage(point.Position) is { } hit)
        {
            _pressedLink = LinkAt(hit.Page, hit.Display);
            if (_pressedLink is null && _texts.TryGetValue(hit.Page, out var text))
            {
                bool extend = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0 && _selectionAnchor is not null;
                var pos = new TextPosition(hit.Page, CaretAt(text, hit.Display));
                if (extend) SetSelectionFocus(pos);
                else
                {
                    ClearSelection();
                    _selectionAnchor = pos;
                }
            }
        }
        else
        {
            ClearSelection();
        }
        _host.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_middleScroll) return;
        var point = e.GetCurrentPoint(_host);
        if (point.PointerDeviceType == PointerDeviceType.Touch || _presentation) return;

        if (_pointerDown)
        {
            var delta = new Point(point.Position.X - _pressPoint.X, point.Position.Y - _pressPoint.Y);
            if (!_dragging && Math.Abs(delta.X) + Math.Abs(delta.Y) > 4 / Zoom) _dragging = true;
            if (_dragging && _selectionAnchor is not null && HitPageNearest(point.Position) is { } hit && _texts.TryGetValue(hit.Page, out var text))
            {
                if (_selectUnit > 0) ExtendUnitSelection(hit.Page, text, CaretAt(text, hit.Display));
                else SetSelectionFocus(new TextPosition(hit.Page, CaretAt(text, hit.Display)));
                AutoScroll(e.GetCurrentPoint(_scroller).Position);
            }
            return;
        }

        var cursor = InputSystemCursorShape.Arrow;
        string? status = null;
        if (HitPage(point.Position) is { } h)
        {
            if (LinkAt(h.Page, h.Display) is { } link)
            {
                cursor = InputSystemCursorShape.Hand;
                status = link.Destination.Uri is { } uri
                    ? LaunchPolicy.ParseExternalLink(uri) is { } parsed ? LaunchPolicy.DescribeLink(parsed) : "This link can't be opened"
                    : $"Go to page {_document!.GetPageLabel(link.Destination.PageIndex)}";
            }
            else if (AnnotationAt(h.Page, h.Display) is { } annotation)
            {
                cursor = InputSystemCursorShape.Hand;
                status = string.IsNullOrWhiteSpace(annotation.Contents) ? null : annotation.Contents.ReplaceLineEndings(" ");
            }
            else if (_texts.TryGetValue(h.Page, out var text) && CharAt(text, h.Display, 1) >= 0)
            {
                cursor = InputSystemCursorShape.IBeam;
            }
        }
        SetCursor(cursor);
        SetStatus(status);
    }

    private void AutoScroll(Point viewportPoint)
    {
        double dy = 0, dx = 0;
        if (viewportPoint.Y < 0) dy = viewportPoint.Y;
        else if (viewportPoint.Y > _scroller.ViewportHeight) dy = viewportPoint.Y - _scroller.ViewportHeight;
        if (viewportPoint.X < 0) dx = viewportPoint.X;
        else if (viewportPoint.X > _scroller.ViewportWidth) dx = viewportPoint.X - _scroller.ViewportWidth;
        if (dx != 0 || dy != 0)
            ScrollTo(Math.Max(0, _scroller.HorizontalOffset + dx), Math.Max(0, _scroller.VerticalOffset + dy), null, true);
    }

    // ---------------------------------------------------------------- middle-click scrolling

    private void OnMiddleScrollPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_root);
        _swallowRightTap = false;
        if (_middleScroll)
        {
            // Any click ends middle-click scrolling and does nothing else.
            _swallowRightTap = point.Properties.IsRightButtonPressed;
            StopMiddleScroll();
            e.Handled = true;
            return;
        }
        if (point.PointerDeviceType != PointerDeviceType.Mouse || !point.Properties.IsMiddleButtonPressed || _document is null || _presentation) return;
        Focus(FocusState.Pointer);
        if (!StartMiddleScroll(point.Position)) return;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMiddleScrollPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_middleScroll) return;
        var point = e.GetCurrentPoint(_root);
        _middleScrollPointer = point.Position;
        double distance = Math.Max(Math.Abs(point.Position.X - _middleScrollOrigin.X), Math.Abs(point.Position.Y - _middleScrollOrigin.Y));
        if (point.Properties.IsMiddleButtonPressed && distance > MiddleScrollDeadZone) _middleScrollHeld = true;
    }

    private void OnMiddleScrollPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // A plain click leaves scrolling on; releasing after scrolling with the button held ends it.
        if (_middleScroll && _middleScrollHeld && e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased)
            StopMiddleScroll();
    }

    private bool StartMiddleScroll(Point origin)
    {
        bool vertical = _scroller.ScrollableHeight > 0, horizontal = _scroller.ScrollableWidth > 0;
        if (!vertical && !horizontal) return false;
        _middleScroll = true;
        _middleScrollHeld = false;
        _middleScrollOrigin = _middleScrollPointer = origin;
        _middleScrollX = _pendingContentX is { } x ? x * Zoom : _scroller.HorizontalOffset;
        _middleScrollY = _pendingContentY is { } y ? y * Zoom : _scroller.VerticalOffset;
        _middleScrollTime = System.Diagnostics.Stopwatch.GetTimestamp();
        DrawMiddleScrollMarker(vertical, horizontal);
        _middleScrollMarker.Margin = new Thickness(origin.X - MiddleScrollMarkerSize / 2, origin.Y - MiddleScrollMarkerSize / 2, 0, 0);
        _middleScrollMarker.Visibility = Visibility.Visible;
        SetStatus(null);
        SetCursor(vertical && horizontal ? InputSystemCursorShape.SizeAll : vertical ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
        CompositionTarget.Rendering += OnMiddleScrollFrame;
        return true;
    }

    /// <summary>Ends middle-click scrolling, if it's on.</summary>
    public void StopMiddleScroll()
    {
        if (!_middleScroll) return;
        _middleScroll = false;
        _middleScrollHeld = false;
        CompositionTarget.Rendering -= OnMiddleScrollFrame;
        _middleScrollMarker.Visibility = Visibility.Collapsed;
        ReleasePointerCaptures();
        SetCursor(InputSystemCursorShape.Arrow);
    }

    private void OnMiddleScrollFrame(object? sender, object e)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double seconds = Math.Min(0.1, (now - _middleScrollTime) / (double)System.Diagnostics.Stopwatch.Frequency);
        _middleScrollTime = now;
        double x = Math.Clamp(_middleScrollX + MiddleScrollSpeed(_middleScrollPointer.X - _middleScrollOrigin.X) * seconds, 0, _scroller.ScrollableWidth);
        double y = Math.Clamp(_middleScrollY + MiddleScrollSpeed(_middleScrollPointer.Y - _middleScrollOrigin.Y) * seconds, 0, _scroller.ScrollableHeight);
        if (x == _middleScrollX && y == _middleScrollY) return;
        // Offsets are tracked here, since the scroller's lag behind ChangeView.
        _middleScrollX = x;
        _middleScrollY = y;
        ScrollTo(x, y, null, true);
    }

    /// <summary>Scroll speed (DIP per second) for a pointer <paramref name="distance"/> DIP from the origin.</summary>
    private static double MiddleScrollSpeed(double distance)
    {
        double excess = Math.Abs(distance) - MiddleScrollDeadZone;
        return excess <= 0 ? 0 : Math.Sign(distance) * Math.Pow(excess, 1.5) * 1.2;
    }

    /// <summary>Draws the origin marker, with arrows for the directions that can scroll.</summary>
    private void DrawMiddleScrollMarker(bool vertical, bool horizontal)
    {
        var resources = Application.Current.Resources;
        var glyphStyle = (Style)resources["MiddleScrollGlyphStyle"];
        const double s = MiddleScrollMarkerSize, c = s / 2;
        _middleScrollMarker.Children.Clear();
        _middleScrollMarker.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = s,
            Height = s,
            Style = (Style)resources["MiddleScrollMarkerStyle"],
        });
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 4, Height = 4, Style = glyphStyle };
        Canvas.SetLeft(dot, c - 2);
        Canvas.SetTop(dot, c - 2);
        _middleScrollMarker.Children.Add(dot);

        // (dx, dy) is the unit direction the arrow points in.
        void Arrow(double tipX, double tipY, double dx, double dy)
        {
            var arrow = new Microsoft.UI.Xaml.Shapes.Polygon { Style = glyphStyle };
            arrow.Points.Add(new Point(tipX, tipY));
            arrow.Points.Add(new Point(tipX - dx * 4 - dy * 4, tipY - dy * 4 - dx * 4));
            arrow.Points.Add(new Point(tipX - dx * 4 + dy * 4, tipY - dy * 4 + dx * 4));
            _middleScrollMarker.Children.Add(arrow);
        }
        if (vertical)
        {
            Arrow(c, 5, 0, -1);
            Arrow(c, s - 5, 0, 1);
        }
        if (horizontal)
        {
            Arrow(5, c, -1, 0);
            Arrow(s - 5, c, 1, 0);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;
        // Releasing capture raises PointerCaptureLost synchronously, which resets the drag state.
        bool dragging = _dragging;
        _pointerDown = false;
        _dragging = false;
        _host.ReleasePointerCaptures();
        if (_selectUnit > 0)
        {
            // Keep the word/line selection made by the double/triple click.
            _selectUnit = 0;
            e.Handled = true;
            return;
        }
        var point = e.GetCurrentPoint(_host);
        if (!dragging)
        {
            if (_pressedLink is { } link && HitPage(point.Position) is { } h && LinkAt(h.Page, h.Display) == link)
            {
                GoToDestination(link.Destination);
            }
            else if (HitPage(point.Position) is { } hit && AnnotationAt(hit.Page, hit.Display) is { } annotation && (e.KeyModifiers & VirtualKeyModifiers.Shift) == 0)
            {
                ClearSelection();
                ShowAnnotationFlyout(annotation, point.Position);
            }
            else if ((e.KeyModifiers & VirtualKeyModifiers.Shift) == 0)
            {
                ClearSelection();
            }
        }
        _dragging = false;
        e.Handled = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private long _lastClickTime;
    private Point _lastClickPoint;
    private int _clickCount;

    /// <summary>Counts consecutive clicks at roughly the same spot (1 = single, 2 = double, 3 = triple).</summary>
    private int CountClick(Point position)
    {
        long now = Environment.TickCount64;
        double distance = Math.Abs(position.X - _lastClickPoint.X) + Math.Abs(position.Y - _lastClickPoint.Y);
        bool continues = now - _lastClickTime <= GetDoubleClickTime() && distance * Zoom <= 6;
        _clickCount = continues ? Math.Min(_clickCount + 1, 3) : 1;
        _lastClickTime = now;
        _lastClickPoint = position;
        return _clickCount;
    }

    // After a double/triple click, dragging extends the selection by whole words/lines
    // around the unit that was clicked first.
    private int _selectUnit;
    private TextPosition _unitStart;
    private TextPosition _unitEnd;

    /// <summary>Bounds [start, end) of the word (unit 2) or line (unit 3) containing character <paramref name="i"/>.</summary>
    private static (int Start, int End) UnitBounds(PageText text, int i, int unit)
    {
        i = Math.Clamp(i, 0, Math.Max(0, text.Count - 1));
        int start = i, end = Math.Min(i + 1, text.Count);
        if (unit >= 3)
        {
            static bool IsBreak(int cp) => cp is '\r' or '\n';
            while (start > 0 && !IsBreak(text.CodePoints[start - 1])) start--;
            while (end < text.Count && !IsBreak(text.CodePoints[end])) end++;
        }
        else if (text.IsWordChar(i))
        {
            while (start > 0 && text.IsWordChar(start - 1)) start--;
            while (end < text.Count && text.IsWordChar(end)) end++;
        }
        return (start, end);
    }

    /// <summary>Selects the word (clicks == 2) or line (clicks == 3) under a point.</summary>
    private bool SelectUnit(int page, Point display, int clicks)
    {
        if (!_texts.TryGetValue(page, out var text)) return false;
        int i = CharAt(text, display, 1);
        if (i < 0) return false;
        var (start, end) = UnitBounds(text, i, clicks);
        ClearSelection();
        _selectUnit = clicks;
        _unitStart = new TextPosition(page, start);
        _unitEnd = new TextPosition(page, end);
        _selectionAnchor = _unitStart;
        SetSelectionFocus(_unitEnd);
        return true;
    }

    /// <summary>Extends a unit-wise selection to the word/line under a caret position.</summary>
    private void ExtendUnitSelection(int page, PageText text, int caret)
    {
        var position = new TextPosition(page, caret);
        bool forward = position.CompareTo(_unitStart) > 0;
        int c = forward ? caret - 1 : caret;
        var (start, end) = UnitBounds(text, c, _selectUnit);
        if (forward)
        {
            _selectionAnchor = _unitStart;
            var focus = new TextPosition(page, end);
            SetSelectionFocus(focus.CompareTo(_unitEnd) < 0 ? _unitEnd : focus);
        }
        else
        {
            _selectionAnchor = _unitEnd;
            SetSelectionFocus(new TextPosition(page, start));
        }
    }

    private void SetSelectionFocus(TextPosition focus)
    {
        var old = GetSelection();
        _selectionFocus = focus;
        var now = GetSelection();
        int from = Math.Min(old?.Start.Page ?? focus.Page, now?.Start.Page ?? focus.Page);
        int to = Math.Max(old?.End.Page ?? focus.Page, now?.End.Page ?? focus.Page);
        for (int p = from; p <= to; p++) UpdateOverlay(p);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetCursor(InputSystemCursorShape shape)
    {
        if (_cursor == shape) return;
        _cursor = shape;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    private void SetStatus(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _statusPill.Visibility = Visibility.Collapsed;
            return;
        }
        _statusText.Text = text.Length > 300 ? text[..300] + "…" : text;
        _statusPill.Visibility = Visibility.Visible;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        StopMiddleScroll();
        if (_document is null || (_continuous && !_presentation)) return;
        if ((e.KeyModifiers & VirtualKeyModifiers.Control) != 0) return;
        // Single page mode: flip pages when scrolling past the edges.
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        bool atBottom = _scroller.VerticalOffset >= _scroller.ScrollableHeight - 1;
        bool atTop = _scroller.VerticalOffset <= 1;
        if (delta < 0 && atBottom) NextPage();
        else if (delta > 0 && atTop && _currentPage > 0)
        {
            PreviousPage();
            ScrollTo(null, _scroller.ScrollableHeight, null, true);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_middleScroll)
        {
            StopMiddleScroll();
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                return;
            }
        }
        if (_document is null || e.OriginalSource is TextBox) return;
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool paged = !_continuous || _presentation;
        bool fitsWidth = _scroller.ScrollableWidth <= 1;
        switch (e.Key)
        {
            case VirtualKey.C when ctrl:
                CopySelection();
                e.Handled = true;
                break;
            case VirtualKey.A when ctrl:
                SelectAll();
                e.Handled = true;
                break;
            case VirtualKey.Escape when HasSelection:
                ClearSelection();
                e.Handled = true;
                break;
            case VirtualKey.PageDown or VirtualKey.Space or VirtualKey.Enter when paged:
            case VirtualKey.Right or VirtualKey.Down when _presentation:
            case VirtualKey.Right when fitsWidth && !ctrl:
                NextPage();
                e.Handled = true;
                break;
            case VirtualKey.PageUp or VirtualKey.Back when paged:
            case VirtualKey.Left or VirtualKey.Up when _presentation:
            case VirtualKey.Left when fitsWidth && !ctrl:
                PreviousPage();
                e.Handled = true;
                break;
            case VirtualKey.Home when !ctrl && paged:
            case VirtualKey.Home when ctrl:
                GoToPage(0);
                e.Handled = true;
                break;
            case VirtualKey.End when !ctrl && paged:
            case VirtualKey.End when ctrl:
                GoToPage(_document.PageCount - 1);
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- flyouts & annotations

    private static readonly (string Name, Color Color)[] HighlightColors =
    [
        ("Yellow", Color.FromArgb(255, 255, 221, 51)),
        ("Green", Color.FromArgb(255, 125, 214, 115)),
        ("Blue", Color.FromArgb(255, 110, 180, 255)),
        ("Pink", Color.FromArgb(255, 255, 128, 190)),
        ("Purple", Color.FromArgb(255, 190, 140, 255)),
    ];

    private CommandBarFlyout? _selectionFlyout;

    /// <summary>Whether a point (display points on <paramref name="page"/>) lies on the selected text.</summary>
    private bool IsInSelection(int page, Point display)
    {
        if (GetSelection() is not { } sel || page < sel.Start.Page || page > sel.End.Page || !_texts.TryGetValue(page, out var text)) return false;
        int start = page == sel.Start.Page ? sel.Start.Caret : 0;
        int end = page == sel.End.Page ? sel.End.Caret : text.Count;
        return HitRects(text, start, end).Any(r => Inflate(r, 2).Contains(display));
    }

    private void ShowSelectionFlyout(Point at)
    {
        _selectionFlyout?.Hide();
        var flyout = _selectionFlyout = new CommandBarFlyout { AlwaysExpanded = true };
        var copy = new AppBarButton { Label = "Copy", Icon = new SymbolIcon(Symbol.Copy) };
        ToolTipService.SetToolTip(copy, "Copy (Ctrl+C)");
        copy.Click += (_, _) => { CopySelection(); flyout.Hide(); };
        var highlight = new AppBarButton { Label = "Highlight", Icon = new FontIcon { Glyph = "\uE7E6" } };
        ToolTipService.SetToolTip(highlight, "Highlight (Ctrl+H)");
        highlight.Click += (_, _) => { HighlightSelection(HighlightColors[0].Color); flyout.Hide(); };
        var note = new AppBarButton { Label = "Add note", Icon = new FontIcon { Glyph = "\uE70B" } };
        ToolTipService.SetToolTip(note, "Highlight and add a note");
        note.Click += (_, _) => { HighlightSelection(HighlightColors[0].Color, openNote: true); flyout.Hide(); };
        flyout.PrimaryCommands.Add(copy);
        flyout.PrimaryCommands.Add(highlight);
        flyout.PrimaryCommands.Add(note);

        foreach (var (name, color) in HighlightColors)
        {
            var item = new AppBarButton
            {
                Label = $"Highlight {name.ToLowerInvariant()}",
                Icon = new FontIcon { Glyph = "\uE91F", Foreground = new SolidColorBrush(color) },
            };
            item.Click += (_, _) => { HighlightSelection(color); flyout.Hide(); };
            flyout.SecondaryCommands.Add(item);
        }
        flyout.SecondaryCommands.Add(new AppBarSeparator());
        var search = new AppBarButton { Label = "Search for selection", Icon = new SymbolIcon(Symbol.Find) };
        search.Click += async (_, _) =>
        {
            flyout.Hide();
            var text = (await GetSelectedTextAsync()).ReplaceLineEndings(" ").Trim();
            if (text.Length > 0) SearchRequested?.Invoke(this, text.Length > 200 ? text[..200] : text);
        };
        flyout.SecondaryCommands.Add(search);
        // Shown as the context menu for selected text, like Papers.
        flyout.ShowAt(_host, new FlyoutShowOptions
        {
            Position = at,
            ShowMode = FlyoutShowMode.Standard,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
        });
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_swallowRightTap)
        {
            // The right click ended middle-click scrolling.
            _swallowRightTap = false;
            e.Handled = true;
            return;
        }
        if (_document is null) return;
        if (_presentation)
        {
            PreviousPage();
            e.Handled = true;
            return;
        }
        var position = e.GetPosition(_host);
        var hit = HitPage(position);
        if (hit is { } onText && IsInSelection(onText.Page, onText.Display))
        {
            ShowSelectionFlyout(position);
            e.Handled = true;
            return;
        }
        var menu = new MenuFlyout();
        if (hit is { } h)
        {
            if (LinkAt(h.Page, h.Display) is { Destination.Uri: { } uri })
            {
                if (LaunchPolicy.ParseExternalLink(uri) is not null) AddItem(menu, "Open link", "\uE8A7", () => _ = LaunchUriAsync(uri));
                AddItem(menu, "Copy link address", "\uE71B", () =>
                {
                    var package = new DataPackage();
                    package.SetText(uri);
                    Clipboard.SetContent(package);
                });
                menu.Items.Add(new MenuFlyoutSeparator());
            }
            if (AnnotationAt(h.Page, h.Display) is { } annotation)
            {
                AddItem(menu, "Edit note", "\uE70F", () => ShowAnnotationFlyout(annotation, position));
                AddItem(menu, "Delete annotation", "\uE74D", () => DeleteAnnotation(annotation));
                menu.Items.Add(new MenuFlyoutSeparator());
            }
            AddItem(menu, "Add note here", "\uE70B", () => AddNote(h.Page, h.Display, position));
        }
        AddItem(menu, "Select all", "\uE8B3", SelectAll, VirtualKey.A);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(menu, "Rotate left", "\uE7AD", () => Rotate(-90));
        ((MenuFlyoutItem)menu.Items[^1]).Icon = new FontIcon { Glyph = "\uE7AD", RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform { ScaleX = -1 } };
        AddItem(menu, "Rotate right", "\uE7AD", () => Rotate(90));
        menu.ShowAt(_host, position);
        e.Handled = true;
    }

    private static void AddItem(MenuFlyout menu, string text, string glyph, Action action, VirtualKey? key = null)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        if (key is { } k)
        {
            item.KeyboardAcceleratorTextOverride = $"Ctrl+{k}";
        }
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    public async void HighlightSelection(Color? color = null, bool openNote = false)
    {
        if (_document is null || GetSelection() is not { } sel) return;
        var document = _document;
        var pages = new List<int>();
        try
        {
            for (int p = sel.Start.Page; p <= sel.End.Page; p++)
            {
                var text = await GetTextAsync(p);
                if (document != _document) return;
                if (text is null) continue;
                int start = p == sel.Start.Page ? sel.Start.Caret : 0;
                int end = p == sel.End.Page ? sel.End.Caret : text.Count;
                var rects = HitRects(text, start, end);
                if (rects.Count == 0) continue;
                await document.AddHighlightAsync(p, rects, color ?? HighlightColors[0].Color);
                pages.Add(p);
            }
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            // Pages highlighted before the failure still get refreshed below.
            ReportEditFailure(document, ex, "The text couldn't be highlighted.");
        }
        if (document != _document) return;
        ClearSelection();
        foreach (var p in pages)
        {
            InvalidatePage(p);
            AnnotationsChanged?.Invoke(this, p);
        }
        if (openNote && pages.Count > 0)
        {
            int page = pages[0];
            var text = await GetTextAsync(page);
            var annotation = text?.Annotations.LastOrDefault(a => a.Kind == AnnotationKind.Highlight);
            if (annotation is not null)
            {
                var r = _pageRects[page];
                var p = RotatedLayoutPoint(page, new Point(annotation.Bounds.Left, annotation.Bounds.Bottom));
                ShowAnnotationFlyout(annotation, new Point(r.X + p.X, r.Y + p.Y));
            }
        }
    }

    private async void AddNote(int page, Point display, Point hostPosition)
    {
        if (_document is null) return;
        var document = _document;
        try
        {
            await document.AddNoteAsync(page, display, HighlightColors[0].Color, "");
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            ReportEditFailure(document, ex, "The note couldn't be added.");
            return;
        }
        if (document != _document) return;
        InvalidatePage(page);
        AnnotationsChanged?.Invoke(this, page);
        var text = await GetTextAsync(page);
        var note = text?.Annotations.LastOrDefault(a => a.Kind == AnnotationKind.Note);
        if (note is not null) ShowAnnotationFlyout(note, hostPosition);
    }

    public async void DeleteAnnotation(PdfAnnotation annotation)
    {
        if (_document is null) return;
        var document = _document;
        try
        {
            await document.RemoveAnnotationAsync(annotation);
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            ReportEditFailure(document, ex, "The annotation couldn't be deleted.");
            return;
        }
        if (document != _document) return;
        InvalidatePage(annotation.PageIndex);
        AnnotationsChanged?.Invoke(this, annotation.PageIndex);
    }

    /// <summary>What a PDFium edit throws when the document closed meanwhile or the page couldn't be changed.</summary>
    private static bool IsEditFailure(Exception ex) => ex is ObjectDisposedException or InvalidOperationException or InvalidDataException;

    private void ReportEditFailure(PdfDocument document, Exception ex, string message)
    {
        // A document that was closed or replaced meanwhile has nobody left to tell.
        if (ex is ObjectDisposedException || document != _document) return;
        EditFailed?.Invoke(this, message);
    }

    public async void RevealAnnotation(PdfAnnotation annotation)
    {
        var text = await GetTextAsync(annotation.PageIndex);
        EnsurePageVisible(annotation.PageIndex, annotation.Bounds);
        await Task.Delay(50);
        var current = text?.Annotations.FirstOrDefault(a => a.Index == annotation.Index) ?? annotation;
        var r = _pageRects[annotation.PageIndex];
        if (r.IsEmpty) return;
        var p = RotatedLayoutPoint(annotation.PageIndex, new Point(current.Bounds.Left, current.Bounds.Bottom));
        ShowAnnotationFlyout(current, new Point(r.X + p.X, r.Y + p.Y));
    }

    private void ShowAnnotationFlyout(PdfAnnotation annotation, Point at)
    {
        if (_document is null) return;
        var document = _document;
        var header = new TextBlock
        {
            Text = string.Join(" · ", new[] { string.IsNullOrEmpty(annotation.Author) ? null : annotation.Author, annotation.Modified?.LocalDateTime.ToString("g") }.Where(s => s is not null)),
            Style = (Style)Application.Current.Resources["SecondaryCaptionTextBlockStyle"],
        };
        var box = new TextBox
        {
            Text = annotation.Contents,
            PlaceholderText = "Add a note",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 96,
            MaxHeight = 280,
            Width = 300,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var delete = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "\uE74D", FontSize = 14 }, new TextBlock { Text = "Delete" } } },
        };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new Grid { Children = { new TextBlock { Text = KindName(annotation.Kind), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] } } },
                    header,
                    box,
                    delete,
                },
            },
        };
        if (string.IsNullOrEmpty(header.Text)) header.Visibility = Visibility.Collapsed;
        var edit = new NoteEdit(document, annotation, box);
        _noteEdits.Add(edit);
        delete.Click += (_, _) =>
        {
            _noteEdits.Remove(edit);
            flyout.Hide();
            DeleteAnnotation(annotation);
        };
        flyout.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        flyout.Closed += async (_, _) =>
        {
            if (!_noteEdits.Remove(edit)) return;
            await CommitNoteAsync(edit);
        };
        flyout.ShowAt(_host, new FlyoutShowOptions { Position = at, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    /// <summary>The text box of an open note flyout, whose text is written to the document when the flyout closes.</summary>
    private sealed class NoteEdit(PdfDocument document, PdfAnnotation annotation, TextBox box)
    {
        public PdfDocument Document { get; } = document;
        public PdfAnnotation Annotation { get; } = annotation;
        public string Text => box.Text;
        /// <summary>The contents last written to the document.</summary>
        public string Saved { get; set; } = annotation.Contents;
        public bool IsChanged => box.Text != Saved;
    }

    private readonly List<NoteEdit> _noteEdits = [];

    /// <summary>
    /// Writes the text of open note flyouts to the document. Closing the window, the document or
    /// reloading doesn't close the flyout first, so without this the text typed into it is lost.
    /// </summary>
    public async Task CommitPendingEditsAsync()
    {
        foreach (var edit in _noteEdits.ToList()) await CommitNoteAsync(edit);
    }

    private async Task CommitNoteAsync(NoteEdit edit)
    {
        if (!edit.IsChanged || edit.Document != _document) return;
        // Typing can go on while the flyout stays open; that's written when it closes.
        string text = edit.Text;
        edit.Saved = text;
        int page = edit.Annotation.PageIndex;
        try
        {
            await edit.Document.SetAnnotationContentsAsync(edit.Annotation, text);
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            ReportEditFailure(edit.Document, ex, "The note couldn't be changed.");
            return;
        }
        if (edit.Document != _document) return;
        _texts.Remove(page);
        EnsureText(page);
        AnnotationsChanged?.Invoke(this, page);
    }

    public static string KindName(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Note => "Note",
        AnnotationKind.Highlight => "Highlight",
        AnnotationKind.Underline => "Underline",
        AnnotationKind.StrikeOut => "Strikethrough",
        AnnotationKind.Squiggly => "Squiggly underline",
        AnnotationKind.FreeText => "Text box",
        AnnotationKind.Shape => "Shape",
        AnnotationKind.Ink => "Drawing",
        AnnotationKind.Stamp => "Stamp",
        AnnotationKind.Attachment => "Attachment",
        _ => "Annotation",
    };

    private static async Task LaunchUriAsync(string uri)
    {
        if (LaunchPolicy.ParseExternalLink(uri) is { } parsed) await Launcher.LaunchUriAsync(parsed);
    }
}
