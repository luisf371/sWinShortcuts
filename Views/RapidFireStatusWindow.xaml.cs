using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using sWinShortcuts.Interop;
using WinForms = System.Windows.Forms;

namespace sWinShortcuts.Views;

// Status dot for the sticky Rapid Fire arm (driven by RapidFireStatusService). Top-left anchored
// on the cursor's monitor, click-through, always-on-top, ~2 mm (8 raw pixels @96 dpi; approximate
// at other DPIs — same acceptance as the crosshair overlay). Green = Ready, gray = ArmedNotReady;
// hidden entirely while Off. All focus-stealing vectors are closed off (ShowActivated=false,
// WS_EX_NOACTIVATE, SWP_NOACTIVATE, WS_EX_TRANSPARENT, Focusable=false, IsHitTestVisible=false).
public partial class RapidFireStatusWindow : Window
{
    private const double DotRawPixels = 8.0;
    private const int MarginRawPixels = 12;

    private static readonly SolidColorBrush ReadyBrush = Frozen(0x2F, 0xBF, 0x2F);
    private static readonly SolidColorBrush NotReadyBrush = Frozen(0x9E, 0x9E, 0x9E);

    private IntPtr _hwnd;

    public RapidFireStatusWindow()
    {
        InitializeComponent();
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        // WS_EX_LAYERED is already set by WPF for AllowsTransparency windows; only OR in the
        // overlay bits. Click-through + no-activate + hidden from alt-tab.
        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    // Called on the UI dispatcher by RapidFireStatusService, only when the visual state actually
    // changed (the service dedups). Positions on the monitor the CURSOR is on — keyboard alt-tab
    // can move focus without the cursor — at the moment the state lands. That is the documented
    // contract: no mouse tracking, and switching between two non-owner apps (gray -> gray) does
    // not reposition.
    public void ApplyState(bool ready)
    {
        // Create the HWND while still hidden so ex-styles are in place before the first Show.
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
        }

        // Managed size FIRST: 8 raw pixels expressed in DIPs via the per-axis device transform.
        // Zero-scale guard falls back to the raw value (identity-class transform).
        var device = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                     ?? Matrix.Identity;
        if (device.M11 > 0)
        {
            Width = DotRawPixels / device.M11;
        }

        if (device.M22 > 0)
        {
            Height = DotRawPixels / device.M22;
        }

        var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position);
        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOPMOST,
            screen.Bounds.X + MarginRawPixels, screen.Bounds.Y + MarginRawPixels,
            0, 0,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE);

        StatusDot.Fill = ready ? ReadyBrush : NotReadyBrush;

        Show();
    }

    public void HideOverlay() => Hide();
}
