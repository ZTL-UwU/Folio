using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Folio.Controls;

/// <summary>A thin vertical grip for resizing an adjacent column.</summary>
public sealed partial class SizerGrip : Grid
{
    private bool _dragging;
    private double _startX;
    private double _startWidth;

    public SizerGrip()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => Finish();
    }

    public ColumnDefinition? Target { get; set; }
    public double MinTargetWidth { get; set; } = 150;
    public double MaxTargetWidth { get; set; } = 520;

    public event EventHandler<double>? Resized;

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Target is null || Parent is not Microsoft.UI.Xaml.UIElement parent) return;
        _dragging = true;
        _startX = e.GetCurrentPoint(parent.XamlRoot?.Content).Position.X;
        _startWidth = Target.ActualWidth;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || Target is null) return;
        double x = e.GetCurrentPoint(XamlRoot?.Content).Position.X;
        double width = Math.Clamp(_startWidth + x - _startX, MinTargetWidth, MaxTargetWidth);
        Target.Width = new Microsoft.UI.Xaml.GridLength(width);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCaptures();
        Finish();
    }

    private void Finish()
    {
        if (!_dragging) return;
        _dragging = false;
        // A click without movement shouldn't pin the width.
        if (Target is not null && Math.Abs(Target.ActualWidth - _startWidth) > 1) Resized?.Invoke(this, Target.ActualWidth);
    }
}
