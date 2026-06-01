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
using Color = System.Windows.Media.Color;
using Application = System.Windows.Application;

public partial class OverlayWindow : Window
{
    private enum ToolMode { Protractor, Ruler }

    private ToolMode _mode = ToolMode.Protractor;

    // Protractor state (independent from the ruler).
    private Point _pVertex;
    private Point _pEnd1;
    private Point _pEnd2;

    // Ruler state (independent from the protractor).
    private Point _rEnd1;
    private Point _rEnd2;

    // Visual handle centers (may differ from the logical points, e.g. in ruler
    // mode the handles are pushed to the side opposite the ticks).
    private Point _vertexHandle;
    private Point _end1Handle;
    private Point _end2Handle;

    // Dynamically created tick-number labels for the ruler.
    private readonly List<TextBlock> _rulerLabels = new();

    // Ruler body polygon (for click-through hit testing) and drag state.
    private Point[] _rulerPoly = Array.Empty<Point>();
    private bool _rulerDragging;
    private Point _rulerDragLast;

    // Hysteresis state for the ruler's tick-side orientation (prevents the ticks
    // from flipping sides / jittering when the ruler is near vertical).
    private bool _rulerFlip;

    // Click-through support: the overlay is transparent to mouse input except
    // when the cursor is near a control point (or a drag is in progress).
    private IntPtr _hwnd;
    private bool _interactive = true;
    private bool _dragging;
    private DispatcherTimer? _hitTimer;

    // How close (in DIPs) the cursor must be to a handle center to grab it.
    private const double HitRadius = 18;

    // Where the tool state is persisted between runs.
    private static readonly string SettingsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScreenProtractor", "state.json");

    private bool _loaded;

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

        // Seed defaults, then overlay any saved state.
        SeedDefaults();
        LoadState();
        _loaded = true;

        _hwnd = new WindowInteropHelper(this).Handle;

        // Keep interactive while dragging a handle even if the poll thinks the
        // cursor moved away from the center.
        VertexThumb.DragStarted += (_, _) => _dragging = true;
        End1Thumb.DragStarted += (_, _) => _dragging = true;
        End2Thumb.DragStarted += (_, _) => _dragging = true;
        VertexThumb.DragCompleted += (_, _) => { _dragging = false; SaveState(); };
        End1Thumb.DragCompleted += (_, _) => { _dragging = false; SaveState(); };
        End2Thumb.DragCompleted += (_, _) => { _dragging = false; SaveState(); };

        // Start click-through; the timer flips it on when needed.
        SetClickThrough(true);

        ApplyMode();

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

        bool nearHandle;
        if (_mode == ToolMode.Ruler)
        {
            // In ruler mode only the two endpoints are handles; the whole body
            // is draggable, so being inside the polygon also counts.
            nearHandle = Near(cursor, _end1Handle) || Near(cursor, _end2Handle)
                         || PointInPolygon(cursor, _rulerPoly);
        }
        else
        {
            nearHandle = Near(cursor, _vertexHandle) || Near(cursor, _end1Handle) || Near(cursor, _end2Handle);
        }

        bool wantInteractive = _dragging || _rulerDragging || nearHandle;

        if (wantInteractive != _interactive)
            SetClickThrough(!wantInteractive);
    }

    private static bool Near(Point a, Point b)
        => (a - b).Length <= HitRadius;

    private static bool PointInPolygon(Point pt, Point[] poly)
    {
        if (poly.Length < 3)
            return false;

        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            if ((poly[i].Y > pt.Y) != (poly[j].Y > pt.Y) &&
                pt.X < (poly[j].X - poly[i].X) * (pt.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>Seed both tools with default positions around screen center.</summary>
    private void SeedDefaults()
    {
        double cx = ActualWidth > 0 ? ActualWidth / 2 : 400;
        double cy = ActualHeight > 0 ? ActualHeight / 2 : 300;

        _pVertex = new Point(cx, cy);
        _pEnd1 = new Point(cx + 200, cy);
        _pEnd2 = new Point(cx + 140, cy - 140);

        _rEnd1 = new Point(cx - 150, cy);
        _rEnd2 = new Point(cx + 150, cy);
    }

    /// <summary>Reset the currently active tool to a default position.</summary>
    public void ResetToCenter()
    {
        double cx = ActualWidth > 0 ? ActualWidth / 2 : 400;
        double cy = ActualHeight > 0 ? ActualHeight / 2 : 300;

        if (_mode == ToolMode.Ruler)
        {
            _rEnd1 = new Point(cx - 150, cy);
            _rEnd2 = new Point(cx + 150, cy);
        }
        else
        {
            _pVertex = new Point(cx, cy);
            _pEnd1 = new Point(cx + 200, cy);
            _pEnd2 = new Point(cx + 140, cy - 140);
        }

        UpdateVisuals();
        SaveState();
    }

    private void VertexThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // Move the whole protractor (vertex + both endpoints).
        var d = new Vector(e.HorizontalChange, e.VerticalChange);
        _pVertex += d;
        _pEnd1 += d;
        _pEnd2 += d;
        UpdateVisuals();
    }

    private void End1Thumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var d = new Vector(e.HorizontalChange, e.VerticalChange);
        if (_mode == ToolMode.Ruler)
            _rEnd1 += d;
        else
            _pEnd1 += d;
        UpdateVisuals();
    }

    private void End2Thumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var d = new Vector(e.HorizontalChange, e.VerticalChange);
        if (_mode == ToolMode.Ruler)
            _rEnd2 += d;
        else
            _pEnd2 += d;
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        if (_mode == ToolMode.Ruler)
            UpdateRuler();
        else
            UpdateProtractor();
    }

    private void UpdateProtractor()
    {
        // Draw the two arms as rays starting at the vertex and extending
        // past the grabbed endpoint, so they are easy to align with screen features.
        DrawRay(Line1, _pVertex, _pEnd1);
        DrawRay(Line2, _pVertex, _pEnd2);

        _vertexHandle = _pVertex;
        _end1Handle = _pEnd1;
        _end2Handle = _pEnd2;
        PlaceThumb(VertexThumb, _vertexHandle);
        PlaceThumb(End1Thumb, _end1Handle);
        PlaceThumb(End2Thumb, _end2Handle);

        double angle = ComputeAngle();
        AngleText.Text = angle.ToString("0.0") + "°";

        UpdateArc(angle);
        PlaceReadout(_pVertex);
    }

    private void UpdateRuler()
    {
        var dir = _rEnd2 - _rEnd1;
        double length = dir.Length;
        if (length < 0.0001)
            dir = new Vector(1, 0);
        else
            dir.Normalize();

        // Perpendicular (screen coords). Force a consistent orientation so that
        // swapping the two endpoints does not flip the ruler to its other face
        // (which would mirror the ticks and numbers). Near vertical the raw
        // decision value passes through zero, so use hysteresis (two thresholds)
        // to avoid rapid flip/jitter: only switch sides once the value clearly
        // crosses the opposite threshold; keep the previous side inside the band.
        var normal = new Vector(dir.Y, -dir.X);
        const double flipThreshold = 0.20;
        if (normal.Y > flipThreshold)
            _rulerFlip = false;
        else if (normal.Y < -flipThreshold)
            _rulerFlip = true;
        if (_rulerFlip)
            normal = -normal;
        const double bodyWidth = 44;

        Point a = _rEnd1;
        Point b = _rEnd2;
        Point a2 = a + normal * bodyWidth;
        Point b2 = b + normal * bodyWidth;

        RulerBody.Points = new PointCollection { a, b, b2, a2 };
        _rulerPoly = new[] { a, b, b2, a2 };

        double dipPerCm = GetDipPerCm();
        var geo = new PathGeometry();
        ClearRulerLabels();

        // Keep label text upright/readable regardless of drag direction.
        double angleDeg = Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI;
        double textAngle = angleDeg;
        if (textAngle > 90) textAngle -= 180;
        else if (textAngle < -90) textAngle += 180;

        if (dipPerCm > 0)
        {
            // Real centimeter scale: minor ticks every mm, major every cm with a label.
            double dipPerMm = dipPerCm / 10.0;
            int i = 0;
            for (double d = 0; d <= length + 0.5; d += dipPerMm, i++)
            {
                bool whole = i % 10 == 0;
                bool half = i % 5 == 0;
                double tickLen = whole ? 16 : (half ? 11 : 6);
                Point p1 = a + dir * d;
                Point p2 = p1 + normal * tickLen;
                AddTick(geo, p1, p2);

                if (whole && i > 0)
                    AddLabel((i / 10).ToString(), p1 + normal * (tickLen + 2), textAngle);
            }
            AddLabel("cm", a + dir * length + normal * 18, textAngle);
        }
        else
        {
            // Fallback: pixel scale.
            int i = 0;
            for (double d = 0; d <= length + 0.5; d += 10, i++)
            {
                bool major = i % 5 == 0;
                double tickLen = major ? 14 : 7;
                Point p1 = a + dir * d;
                Point p2 = p1 + normal * tickLen;
                AddTick(geo, p1, p2);

                if (major && i > 0)
                    AddLabel((i * 10).ToString(), p1 + normal * (tickLen + 2), textAngle);
            }
        }
        RulerTicks.Data = geo;

        // Place the handles on the ruler's far (plain) edge, opposite the ticks,
        // so they sit on the ruler body instead of floating off to the side.
        _end1Handle = a + normal * bodyWidth;
        _end2Handle = b + normal * bodyWidth;
        PlaceThumb(End1Thumb, _end1Handle);
        PlaceThumb(End2Thumb, _end2Handle);

        double cm = dipPerCm > 0 ? length / dipPerCm : 0;
        AngleText.Text = cm > 0
            ? $"{cm:0.00} cm  ({length:0} px)"
            : $"{length:0} px";
        PlaceReadout(new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2));
    }

    private static void AddTick(PathGeometry geo, Point p1, Point p2)
    {
        var fig = new PathFigure { StartPoint = p1, IsClosed = false };
        fig.Segments.Add(new LineSegment(p2, true));
        geo.Figures.Add(fig);
    }

    private void AddLabel(string text, Point at, double angleDeg)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x90, 0xFF)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            RenderTransform = new RotateTransform(angleDeg)
        };
        Canvas.SetLeft(tb, at.X);
        Canvas.SetTop(tb, at.Y);
        Root.Children.Add(tb);
        _rulerLabels.Add(tb);
    }

    private void ClearRulerLabels()
    {
        foreach (var tb in _rulerLabels)
            Root.Children.Remove(tb);
        _rulerLabels.Clear();
    }

    /// <summary>Switch between protractor and ruler tools.</summary>
    public void ToggleMode()
    {
        _mode = _mode == ToolMode.Protractor ? ToolMode.Ruler : ToolMode.Protractor;
        ApplyMode();
        SaveState();
    }

    private void ApplyMode()
    {
        bool ruler = _mode == ToolMode.Ruler;

        Line1.Visibility = ruler ? Visibility.Collapsed : Visibility.Visible;
        Line2.Visibility = ruler ? Visibility.Collapsed : Visibility.Visible;
        ArcPath.Visibility = ruler ? Visibility.Collapsed : Visibility.Visible;
        RulerBody.Visibility = ruler ? Visibility.Visible : Visibility.Collapsed;
        RulerTicks.Visibility = ruler ? Visibility.Visible : Visibility.Collapsed;

        // The center handle is only used by the protractor; the ruler is dragged
        // by its body instead.
        VertexThumb.Visibility = ruler ? Visibility.Collapsed : Visibility.Visible;

        if (!ruler)
            ClearRulerLabels();

        ReadoutBorder.BorderBrush = ruler
            ? new SolidColorBrush(Color.FromRgb(0x1E, 0x90, 0xFF))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));

        HintText.Text = ruler
            ? "拖动橙色点移动 · 拖动蓝色点旋转/缩放"
            : "拖动橙色点移动 · 拖动蓝色点旋转";

        UpdateVisuals();
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
        var v1 = _pEnd1 - _pVertex;
        var v2 = _pEnd2 - _pVertex;
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

        var v1 = _pEnd1 - _pVertex;
        var v2 = _pEnd2 - _pVertex;
        if (v1.Length < 0.0001 || v2.Length < 0.0001)
        {
            ArcPath.Data = null;
            return;
        }
        v1.Normalize();
        v2.Normalize();

        Point p1 = _pVertex + v1 * radius;
        Point p2 = _pVertex + v2 * radius;

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

    private void PlaceReadout(Point anchor)
    {
        // Position the readout near the anchor, offset so it does not cover the handle.
        Canvas.SetLeft(ReadoutBorder, anchor.X + 24);
        Canvas.SetTop(ReadoutBorder, anchor.Y + 24);
    }

    /// <summary>
    /// Device-independent pixels that correspond to one physical centimeter on the
    /// monitor under the ruler, using the raw (EDID-based) DPI. Returns 0 if unknown.
    /// </summary>
    private double GetDipPerCm()
    {
        if (_hwnd == IntPtr.Zero)
            return 0;

        try
        {
            // Find the monitor that contains the ruler's center.
            Point center = new Point((_rEnd1.X + _rEnd2.X) / 2, (_rEnd1.Y + _rEnd2.Y) / 2);
            Point screenPt = PointToScreen(center);
            var pt = new POINT { X = (int)screenPt.X, Y = (int)screenPt.Y };
            IntPtr mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero)
                return 0;

            // Effective DPI = the scaling applied to logical units on this monitor.
            // Raw DPI = the monitor's true physical pixel density (from EDID size).
            if (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint effX, out _) != 0 || effX == 0)
                return 0;
            if (GetDpiForMonitor(mon, MDT_RAW_DPI, out uint rawX, out _) != 0 || rawX == 0)
                return 0;

            // 1 inch = 2.54 cm. dipPerCm = rawDpi * (96/effDpi) / 2.54.
            return rawX * 96.0 / (effX * 2.54);
        }
        catch
        {
            return 0;
        }
    }

    // ------------------------------------------------------------------
    // Right-click context menu on the center control point.
    // ------------------------------------------------------------------

    private void HandleMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && menu.Items.Count > 0 && menu.Items[0] is MenuItem modeItem)
            modeItem.Header = _mode == ToolMode.Protractor ? "切换为直尺" : "切换为量角器";
    }

    private void MenuToggleMode_Click(object sender, RoutedEventArgs e) => ToggleMode();

    private void MenuReset_Click(object sender, RoutedEventArgs e) => ResetToCenter();

    private void MenuHide_Click(object sender, RoutedEventArgs e) => Hide();

    private void MenuExit_Click(object sender, RoutedEventArgs e)
        => (Application.Current as App)?.ExitApp();

    // ------------------------------------------------------------------
    // Persisting tool state.
    // ------------------------------------------------------------------

    private sealed class ToolState
    {
        public string Mode { get; set; } = "Protractor";
        public double[] Protractor { get; set; } = Array.Empty<double>();
        public double[] Ruler { get; set; } = Array.Empty<double>();
    }

    public void SaveState()
    {
        if (!_loaded)
            return;

        try
        {
            var state = new ToolState
            {
                Mode = _mode.ToString(),
                Protractor = new[] { _pVertex.X, _pVertex.Y, _pEnd1.X, _pEnd1.Y, _pEnd2.X, _pEnd2.Y },
                Ruler = new[] { _rEnd1.X, _rEnd1.Y, _rEnd2.X, _rEnd2.Y }
            };

            string? dir = System.IO.Path.GetDirectoryName(SettingsPath);
            if (dir is not null)
                System.IO.Directory.CreateDirectory(dir);

            string json = System.Text.Json.JsonSerializer.Serialize(state,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Persistence is best-effort; ignore IO/serialization failures.
        }
    }

    private void LoadState()
    {
        try
        {
            if (!System.IO.File.Exists(SettingsPath))
                return;

            string json = System.IO.File.ReadAllText(SettingsPath);
            var state = System.Text.Json.JsonSerializer.Deserialize<ToolState>(json);
            if (state is null)
                return;

            if (Enum.TryParse<ToolMode>(state.Mode, out var mode))
                _mode = mode;

            if (state.Protractor.Length == 6)
            {
                _pVertex = new Point(state.Protractor[0], state.Protractor[1]);
                _pEnd1 = new Point(state.Protractor[2], state.Protractor[3]);
                _pEnd2 = new Point(state.Protractor[4], state.Protractor[5]);
            }

            if (state.Ruler.Length == 4)
            {
                _rEnd1 = new Point(state.Ruler[0], state.Ruler[1]);
                _rEnd2 = new Point(state.Ruler[2], state.Ruler[3]);
            }
        }
        catch
        {
            // Corrupt/unreadable state: fall back to seeded defaults.
        }
    }

    // ------------------------------------------------------------------
    // Dragging the whole ruler by its body.
    // ------------------------------------------------------------------

    private void RulerBody_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _rulerDragging = true;
        _rulerDragLast = e.GetPosition(Root);
        RulerBody.CaptureMouse();
        e.Handled = true;
    }

    private void RulerBody_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_rulerDragging)
            return;

        Point now = e.GetPosition(Root);
        var d = now - _rulerDragLast;
        _rulerDragLast = now;

        _rEnd1 += d;
        _rEnd2 += d;
        UpdateVisuals();
    }

    private void RulerBody_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_rulerDragging)
            return;

        _rulerDragging = false;
        RulerBody.ReleaseMouseCapture();
        SaveState();
        e.Handled = true;
    }



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

    // --- Monitor DPI lookup for physical (cm) measurement ---

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const int MDT_RAW_DPI = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}

