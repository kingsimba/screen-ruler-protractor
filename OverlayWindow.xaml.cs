using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Protractor;

using Point = System.Windows.Point;
using Vector = System.Windows.Vector;
using Size = System.Windows.Size;

public partial class OverlayWindow : Window
{
    private Point _vertex;
    private Point _end1;
    private Point _end2;

    // Click-through support: the overlay is transparent to mouse input except
    // when the cursor is near a control point (or a drag is in progress).
    private IntPtr _hwnd;
    private bool _interactive = true;
    private bool _dragging;
    private DispatcherTimer? _hitTimer;

    // How close (in DIPs) the cursor must be to a handle center to grab it.
    private const double HitRadius = 18;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Cover the whole virtual desktop (all monitors).
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        ResetToCenter();

        _hwnd = new WindowInteropHelper(this).Handle;

        // Keep interactive while dragging a handle even if the poll thinks the
        // cursor moved away from the center.
        VertexThumb.DragStarted += (_, _) => _dragging = true;
        End1Thumb.DragStarted += (_, _) => _dragging = true;
        End2Thumb.DragStarted += (_, _) => _dragging = true;
        VertexThumb.DragCompleted += (_, _) => _dragging = false;
        End1Thumb.DragCompleted += (_, _) => _dragging = false;
        End2Thumb.DragCompleted += (_, _) => _dragging = false;

        // Start click-through; the timer flips it on when needed.
        SetClickThrough(true);

        _hitTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _hitTimer.Tick += HitTimer_Tick;
        _hitTimer.Start();
    }

    private void HitTimer_Tick(object? sender, EventArgs e)
    {
        if (!GetCursorPos(out POINT p))
            return;

        // Convert the screen cursor position into this window's DIP coordinates.
        Point cursor = PointFromScreen(new Point(p.X, p.Y));

        bool nearHandle = Near(cursor, _vertex) || Near(cursor, _end1) || Near(cursor, _end2);
        bool wantInteractive = _dragging || nearHandle;

        if (wantInteractive != _interactive)
            SetClickThrough(!wantInteractive);
    }

    private static bool Near(Point a, Point b)
        => (a - b).Length <= HitRadius;

    /// <summary>Place the protractor in the middle of the primary screen.</summary>
    public void ResetToCenter()
    {
        double cx = ActualWidth > 0 ? ActualWidth / 2 : 400;
        double cy = ActualHeight > 0 ? ActualHeight / 2 : 300;

        _vertex = new Point(cx, cy);
        _end1 = new Point(cx + 200, cy);
        _end2 = new Point(cx + 140, cy - 140);

        UpdateVisuals();
    }

    private void VertexThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // Move the whole protractor (vertex + both endpoints).
        var d = new Vector(e.HorizontalChange, e.VerticalChange);
        _vertex += d;
        _end1 += d;
        _end2 += d;
        UpdateVisuals();
    }

    private void End1Thumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _end1 += new Vector(e.HorizontalChange, e.VerticalChange);
        UpdateVisuals();
    }

    private void End2Thumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _end2 += new Vector(e.HorizontalChange, e.VerticalChange);
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        // Draw the two arms as rays starting at the vertex and extending
        // past the grabbed endpoint, so they are easy to align with screen features.
        DrawRay(Line1, _vertex, _end1);
        DrawRay(Line2, _vertex, _end2);

        PlaceThumb(VertexThumb, _vertex);
        PlaceThumb(End1Thumb, _end1);
        PlaceThumb(End2Thumb, _end2);

        double angle = ComputeAngle();
        AngleText.Text = angle.ToString("0.0") + "°";

        UpdateArc(angle);
        PlaceReadout();
    }

    private static void DrawRay(Line line, Point from, Point through)
    {
        var dir = through - from;
        if (dir.Length < 0.0001)
            dir = new Vector(1, 0);
        dir.Normalize();

        // Extend the visible line well beyond the endpoint.
        Point end = through + dir * 600;

        line.X1 = from.X;
        line.Y1 = from.Y;
        line.X2 = end.X;
        line.Y2 = end.Y;
    }

    private static void PlaceThumb(Thumb thumb, Point center)
    {
        Canvas.SetLeft(thumb, center.X - thumb.Width / 2);
        Canvas.SetTop(thumb, center.Y - thumb.Height / 2);
    }

    private double ComputeAngle()
    {
        var v1 = _end1 - _vertex;
        var v2 = _end2 - _vertex;
        if (v1.Length < 0.0001 || v2.Length < 0.0001)
            return 0;

        double dot = v1.X * v2.X + v1.Y * v2.Y;
        double cos = dot / (v1.Length * v2.Length);
        cos = Math.Clamp(cos, -1.0, 1.0);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }

    private void UpdateArc(double angle)
    {
        const double radius = 50;

        var v1 = _end1 - _vertex;
        var v2 = _end2 - _vertex;
        if (v1.Length < 0.0001 || v2.Length < 0.0001)
        {
            ArcPath.Data = null;
            return;
        }
        v1.Normalize();
        v2.Normalize();

        Point p1 = _vertex + v1 * radius;
        Point p2 = _vertex + v2 * radius;

        // Cross product (screen coords, y down): >0 means sweeping from v1 to v2 is clockwise.
        double cross = v1.X * v2.Y - v1.Y * v2.X;
        SweepDirection sweep = cross >= 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;

        var figure = new PathFigure { StartPoint = p1, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = p2,
            Size = new Size(radius, radius),
            SweepDirection = sweep,
            IsLargeArc = angle > 180  // measured angle is always <= 180, so always false
        });

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        ArcPath.Data = geo;
    }

    private void PlaceReadout()
    {
        // Position the readout near the vertex, offset so it does not cover the handle.
        Canvas.SetLeft(ReadoutBorder, _vertex.X + 24);
        Canvas.SetTop(ReadoutBorder, _vertex.Y + 24);
    }

    // ------------------------------------------------------------------
    // Click-through (mouse pass-through) handling via Win32 extended styles.
    // ------------------------------------------------------------------

    private void SetClickThrough(bool enabled)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;

        if (enabled)
            ex |= WS_EX_TRANSPARENT;   // mouse events fall through to windows below
        else
            ex &= ~WS_EX_TRANSPARENT;  // this window receives mouse input

        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        _interactive = !enabled;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

