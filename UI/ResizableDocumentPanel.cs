using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EricWorkHub;

public sealed class ResizableDocumentPanel : Grid
{
    private const double EdgeWidth = 5;
    private InputSystemCursor? _resizeCursor, _arrowCursor;
    private uint? _pointer;
    private double _lastX, _startX;
    internal event EventHandler<double>? ResizeRequested;

    public ResizableDocumentPanel()
    {
        PointerEntered += PointerMovedOnPanel;
        PointerMoved += PointerMovedOnPanel;
        PointerPressed += PointerPressedOnPanel;
        PointerReleased += PointerReleasedOnPanel;
        PointerCanceled += PointerLostOnPanel;
        PointerCaptureLost += PointerLostOnPanel;
        PointerExited += (_, _) => { if (_pointer == null) SetCursor(false); };
        Unloaded += (_, _) =>
        {
            EndDrag();
            ReleasePointerCaptures();
            ProtectedCursor = null;
            _resizeCursor?.Dispose();
            _arrowCursor?.Dispose();
            _resizeCursor = _arrowCursor = null;
        };
    }

    private bool AtEdge(PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(this).Position;
        return point.X >= 0 && point.X <= EdgeWidth && point.Y >= 0 && point.Y <= ActualHeight;
    }

    private void SetCursor(bool resizing)
    {
        _resizeCursor ??= InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        _arrowCursor ??= InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        ProtectedCursor = resizing ? _resizeCursor : _arrowCursor;
    }

    private void PointerPressedOnPanel(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != null || !AtEdge(args) || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CapturePointer(args.Pointer)) return;
        _pointer = args.Pointer.PointerId;
        _lastX = _startX = args.GetCurrentPoint(XamlRoot.Content).Position.X;
        SetCursor(true);
        args.Handled = true;
        LogService.Write("UI", "Document left border resize started");
    }

    private void PointerMovedOnPanel(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer == args.Pointer.PointerId)
        {
            double x = args.GetCurrentPoint(XamlRoot.Content).Position.X;
            double delta = x - _lastX;
            _lastX = x;
            ResizeRequested?.Invoke(this, delta);
            args.Handled = true;
        }
        SetCursor(_pointer != null || AtEdge(args));
    }

    private void PointerReleasedOnPanel(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != args.Pointer.PointerId) return;
        EndDrag();
        ReleasePointerCapture(args.Pointer);
        SetCursor(AtEdge(args));
        args.Handled = true;
    }

    private void PointerLostOnPanel(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != args.Pointer.PointerId) return;
        EndDrag();
        SetCursor(false);
    }

    private void EndDrag()
    {
        if (_pointer == null) return;
        _pointer = null;
        LogService.Write("UI", $"Document left border resize ended delta={_lastX - _startX:F1}");
    }
}
