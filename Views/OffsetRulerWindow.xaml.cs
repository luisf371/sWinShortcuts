using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using sWinShortcuts.Interop;
using sWinShortcuts.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Windows.Size;
using WinForms = System.Windows.Forms;

namespace sWinShortcuts.Views;

// Measure ruler: one window per monitor, each inside its own monitor's logical bounds, so one window never has
// to map two monitors with different scaling. Drawing uses logical coordinates; the distance uses physical ones.
public partial class OffsetRulerWindow : Window
{
    internal static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(30);

    private readonly Rectangle _monitor;
    private readonly System.Drawing.Point _startLogical;
    private readonly (int X, int Y) _start;
    private readonly bool _raw;
    private readonly TaskCompletionSource<(int X, int Y)?> _result;
    private IntPtr _hwnd;
    private double _scale = 1;
    private System.Drawing.Point? _lastCursor;
    private bool _leftDown;
    private bool _rightDown;

    internal OffsetRulerWindow(Rectangle monitor, System.Drawing.Point startLogical, (int X, int Y) start, bool raw,
        TaskCompletionSource<(int X, int Y)?> result)
    {
        InitializeComponent();
        _monitor = monitor;
        _startLogical = startLogical;
        _start = start;
        _raw = raw;
        _result = result;
        CompositionTarget.Rendering += OnRendering;
        // Alt+F4 closes one ruler. Ending the measurement then closes the rest.
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            _result.TrySetResult(null);
        };
    }

    /// <summary>
    /// Shows the ruler from the cursor's current spot. A left click returns the physical distance from that spot;
    /// Esc, a right click, closing a ruler or the time limit return null.
    /// </summary>
    public static async Task<(int X, int Y)?> MeasureAsync(bool raw, CancellationToken token)
    {
        // The line starts at the logical reading and the distance at the physical one. Reading again until the logical
        // position holds across the physical read keeps both on one spot.
        System.Drawing.Point startLogical;
        int startX, startY;
        do
        {
            startLogical = WinForms.Cursor.Position;
            if (!WindowsInputSender.TryGetPhysicalCursorPosition(out startX, out startY)) return null;
        }
        while (WinForms.Cursor.Position != startLogical);
        var result = new TaskCompletionSource<(int X, int Y)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = WinForms.Screen.AllScreens
            .Select(screen => new OffsetRulerWindow(screen.Bounds, startLogical, (startX, startY), raw, result)).ToArray();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeLimit);
        using var registration = limit.Token.Register(() => result.TrySetResult(null));
        try
        {
            foreach (var window in windows) window.Show();
            // The window under the cursor takes focus so it receives Esc.
            Array.Find(windows, window => window._monitor.Contains(startLogical))?.Activate();
            var distance = await result.Task;
            token.ThrowIfCancellationRequested();
            return distance;
        }
        finally
        {
            foreach (var window in windows) window.Close();
        }
    }

    internal static Point ToDip(int logicalX, int logicalY, Rectangle monitor, double scale) =>
        new((logicalX - monitor.X) / scale, (logicalY - monitor.Y) / scale);

    // Raw distance depends on the game's sensitivity, so it is shown as approximate.
    internal static string FormatDistance(int x, int y, bool raw) => raw
        ? FormattableString.Invariant($"≈ {x:+0;-0;0}, {y:+0;-0;0}")
        : FormattableString.Invariant($"X {x:+0;-0;0}  Y {y:+0;-0;0} px");

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | NativeMethods.WS_EX_TOOLWINDOW));
        _scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        Width = _monitor.Width / _scale;
        Height = _monitor.Height / _scale;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height,
            NativeMethods.SWP_NOACTIVATE);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_hwnd == IntPtr.Zero) return;
        var cursor = WinForms.Cursor.Position;
        if (cursor == _lastCursor) return;
        _lastCursor = cursor;

        var start = ToDip(_startLogical.X, _startLogical.Y, _monitor, _scale);
        var end = ToDip(cursor.X, cursor.Y, _monitor, _scale);
        SetLine(Distance, start, end);
        SetLine(LegX, start, new Point(end.X, start.Y));
        SetLine(LegY, new Point(end.X, start.Y), end);
        Canvas.SetLeft(StartMarker, start.X - StartMarker.Width / 2);
        Canvas.SetTop(StartMarker, start.Y - StartMarker.Height / 2);

        if (!_monitor.Contains(cursor) || !WindowsInputSender.TryGetPhysicalCursorPosition(out var x, out var y))
        {
            Label.Visibility = Visibility.Collapsed;
            return;
        }

        LabelText.Text = FormatDistance(x - _start.X, y - _start.Y, _raw);
        Label.Visibility = Visibility.Visible;
        Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var (labelX, labelY) = CursorCountdownWindow.PlaceBeside(cursor.X, cursor.Y,
            (int)Math.Ceiling(Label.DesiredSize.Width * _scale), (int)Math.Ceiling(Label.DesiredSize.Height * _scale), _monitor);
        var labelPosition = ToDip(labelX, labelY, _monitor, _scale);
        Canvas.SetLeft(Label, labelPosition.X);
        Canvas.SetTop(Label, labelPosition.Y);
    }

    private static void SetLine(System.Windows.Shapes.Line line, Point from, Point to)
    {
        line.X1 = from.X;
        line.Y1 = from.Y;
        line.X2 = to.X;
        line.Y2 = to.Y;
    }

    // Both edges of a click land on this window, so the app below never sees half a click. Capture keeps the
    // release here even when it happens over another monitor's ruler window.
    protected override void OnMouseLeftButtonDown(MouseEventArgs e)
    {
        _leftDown = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseEventArgs e)
    {
        e.Handled = true;
        if (!_leftDown) return;
        _result.TrySetResult(WindowsInputSender.TryGetPhysicalCursorPosition(out var x, out var y)
            ? (x - _start.X, y - _start.Y)
            : null);
    }

    protected override void OnMouseRightButtonDown(MouseEventArgs e)
    {
        _rightDown = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseEventArgs e)
    {
        e.Handled = true;
        if (_rightDown) _result.TrySetResult(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        _result.TrySetResult(null);
    }
}
