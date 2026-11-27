using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using sWinShortcuts.Interop;
using sWinShortcuts.Services;
using Point = System.Windows.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Windows.Size;
using WinForms = System.Windows.Forms;

namespace sWinShortcuts.Views;

// A small badge that follows the cursor while a coordinate pick counts down. Placement uses the UI thread's
// logical coordinates (Cursor.Position, Screen bounds, SetWindowPos) like CrosshairWindow; only the
// captured numbers are physical pixels.
public partial class CursorCountdownWindow : Window
{
    internal static readonly TimeSpan CountdownDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MessageDuration = TimeSpan.FromMilliseconds(600);
    // Logical pixels between the cursor hotspot and the badge, enough to clear the arrow cursor.
    internal const int CURSOR_GAP = 20;
    private const double RING_CENTER = 13;
    private const double RING_RADIUS = 11.5;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _duration;
    // Live badge only: runs before each placement and may hide the badge.
    private readonly Action? _onFrame;
    private IntPtr _hwnd;
    private System.Drawing.Point? _lastCursor;
    private double _placedWidth;
    private bool _rendering;

    private CursorCountdownWindow(string caption, TimeSpan duration, Action? onFrame = null)
    {
        InitializeComponent();
        Caption.Text = caption;
        _duration = duration;
        _onFrame = onFrame;
        if (duration == TimeSpan.Zero) Dial.Visibility = Visibility.Collapsed;
        if (onFrame is null) SetRendering(true);
        Closed += (_, _) => SetRendering(false);
    }

    /// <summary>A badge that stays open between uses and follows the cursor only while shown.</summary>
    internal static CursorCountdownWindow CreateLiveBadge(Action onFrame) => new(string.Empty, TimeSpan.Zero, onFrame);

    internal void ShowLive(string text)
    {
        Caption.Text = text;
        if (IsVisible) return;
        _lastCursor = null;
        Opacity = 0;
        SetRendering(true);
        Show();
    }

    internal void HideLive()
    {
        SetRendering(false);
        Opacity = 0;
        Hide();
    }

    private void SetRendering(bool on)
    {
        if (on == _rendering) return;
        _rendering = on;
        if (on) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>Shows the badge for the countdown, then returns the physical cursor position.</summary>
    public static async Task<(int X, int Y)?> CountdownAsync(string caption, CancellationToken token)
    {
        var badge = new CursorCountdownWindow(caption, CountdownDuration);
        badge.Show();
        try
        {
            await Task.Delay(CountdownDuration, token);
            return WindowsInputSender.TryGetPhysicalCursorPosition(out var x, out var y) ? (x, y) : null;
        }
        finally
        {
            badge.Close();
        }
    }

    /// <summary>Shows a short message beside the cursor, such as the captured position.</summary>
    public static void ShowMessage(string text)
    {
        var badge = new CursorCountdownWindow(text, TimeSpan.Zero);
        badge.Show();
        var timer = new DispatcherTimer { Interval = MessageDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            badge.Close();
        };
        timer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    // Below and right of the cursor; flips left or up where the badge would cross the monitor's edge.
    internal static (int X, int Y) PlaceBeside(int cursorX, int cursorY, int width, int height, Rectangle monitor)
    {
        var x = cursorX + CURSOR_GAP;
        if (x + width > monitor.Right) x = cursorX - CURSOR_GAP - width;
        var y = cursorY + CURSOR_GAP;
        if (y + height > monitor.Bottom) y = cursorY - CURSOR_GAP - height;
        return (Math.Max(monitor.Left, x), Math.Max(monitor.Top, y));
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        _onFrame?.Invoke();
        if (!_rendering || _hwnd == IntPtr.Zero || ActualWidth == 0) return;
        if (_duration > TimeSpan.Zero) DrawDial();

        var cursor = WinForms.Cursor.Position;
        // A wider caption near the right edge must flip left even while the cursor stays still.
        if (cursor == _lastCursor && ActualWidth == _placedWidth) return;
        _lastCursor = cursor;
        _placedWidth = ActualWidth;
        var device = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var (x, y) = PlaceBeside(cursor.X, cursor.Y, (int)Math.Ceiling(ActualWidth * device.M11),
            (int)Math.Ceiling(ActualHeight * device.M22), WinForms.Screen.FromPoint(cursor).Bounds);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE);
        Opacity = 1;
    }

    // The ring empties clockwise from the top as the countdown runs; the digit shows the whole seconds left.
    private void DrawDial()
    {
        var remaining = Math.Clamp(1 - _clock.Elapsed / _duration, 0, 1);
        Digit.Text = Math.Max(1, (int)Math.Ceiling(remaining * _duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        if (remaining >= 0.999)
        {
            Ring.Data = new EllipseGeometry(new Point(RING_CENTER, RING_CENTER), RING_RADIUS, RING_RADIUS);
            return;
        }

        var angle = remaining * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(RING_CENTER, RING_CENTER - RING_RADIUS), isFilled: false, isClosed: false);
            context.ArcTo(new Point(RING_CENTER + RING_RADIUS * Math.Sin(angle), RING_CENTER - RING_RADIUS * Math.Cos(angle)),
                new Size(RING_RADIUS, RING_RADIUS), 0, isLargeArc: remaining > 0.5, SweepDirection.Clockwise,
                isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        Ring.Data = geometry;
    }
}
