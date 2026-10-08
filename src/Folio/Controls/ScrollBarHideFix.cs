using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.UI.ViewManagement;

namespace Folio.Controls;

/// <summary>
/// Works around a WinUI bug (microsoft/microsoft-ui-xaml#12094): if a ScrollViewer's hide delay runs
/// out while the pointer is over one of its scroll bars, leaving the bar doesn't start it again, so the
/// scroll bar stays visible for good. Easy to hit by scrolling a sidebar list and then moving the pointer
/// out across its scroll bar. This restarts the delayed hide when the pointer leaves a bar, as the
/// upstream fix (#12096) does. Remove once the app is on a Windows App SDK release with that fix.
/// </summary>
internal static class ScrollBarHideFix
{
    private static readonly UISettings Settings = new();

    /// <summary>Hooks the ScrollViewer that is, or is inside, <paramref name="host"/> once its template is applied.</summary>
    public static void Attach(FrameworkElement host)
    {
        bool attached = false;
        void TryAttach()
        {
            if (attached) return;
            if ((host as ScrollViewer ?? host.FindDescendant<ScrollViewer>()) is not { } scroller) return;
            var bars = new[] { "VerticalScrollBar", "HorizontalScrollBar" }
                .Select(name => scroller.FindDescendant<ScrollBar>(name))
                .OfType<ScrollBar>()
                .ToList();
            if (bars.Count == 0) return;
            attached = true;
            foreach (var bar in bars)
                bar.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, e) => OnBarExited(scroller, e)), true);
        }

        // Lists that start collapsed only get their template once they're first measured.
        host.Loaded += (_, _) => TryAttach();
        host.SizeChanged += (_, _) => TryAttach();
        if (host.IsLoaded) TryAttach();
    }

    private static void OnBarExited(ScrollViewer scroller, PointerRoutedEventArgs e)
    {
        // With "Always show scrollbars" on, the bars aren't supposed to hide. And while a thumb is being
        // dragged, the ScrollViewer starts the hide itself once the drag ends.
        if (!Settings.AutoHideScrollBars || e.Pointer.IsInContact) return;

        // The states the ScrollViewer goes to when its indicators time out. The template's transition
        // delays the actual fade, and pointer movement over the content cancels it as usual.
        VisualStateManager.GoToState(scroller, "NoIndicator", true);
        VisualStateManager.GoToState(scroller, "ScrollBarSeparatorCollapsed", true);
    }
}
