using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using sWinShortcuts.Interop;
using WinForms = System.Windows.Forms;

namespace sWinShortcuts.Views;

// 2-second toast for the global color-preset toggle key (driven by ColorProfileToastService): names
// the preset that was just flipped to ("Color Profile: Primary/Secondary"). Top-left anchored on the
// cursor's monitor, offset right of the Rapid Fire status dot, click-through, always-on-top. Every
// press restarts a fresh 2s window (the SERVICE owns that timer; this window is stateless). All
// focus-stealing vectors are closed off (ShowActivated=false, WS_EX_NOACTIVATE, SWP_NOACTIVATE,
// WS_EX_TRANSPARENT, Focusable=false, IsHitTestVisible=false).
public partial class ColorProfileToastWindow : Window
{
    // Raw-pixel geometry shared with RapidFireStatusWindow's dot placement: the dot sits at
    // MarginRawPixels + 8 px, so the toast starts 6 px to its right. Fixed anchor regardless of
    // whether the dot is currently visible.
    private const int MarginRawPixels = 12;
    private const int DotRawPixels = 8;
    private const int GapRawPixels = 6;
    private const int AnchorRawX = MarginRawPixels + DotRawPixels + GapRawPixels;
    // Vertical center of the dot (margin + half the dot), in raw pixels.
    private const int DotCenterRawY = MarginRawPixels + DotRawPixels / 2;

    private IntPtr _hwnd;

    public ColorProfileToastWindow()
    {
        InitializeComponent();
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

    // Called on the UI dispatcher by ColorProfileToastService — once per press (deliberately no
    // dedup: each press restarts the visibility window). Sizes the window from the measured text
    // BEFORE the first Show (so it never flashes at 0,0 or resizes on screen), then positions on
    // the monitor the CURSOR is on — same acceptance as the status dot: anchored at apply time, no
    // cross-window tracking if the dot later moves.
    public void ShowToast(string text)
    {
        // Create the HWND while still hidden so ex-styles are in place before the first Show.
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
        }

        ToastText.Text = text;

        // Managed size FIRST, in DIPs: measure the badge at infinity and pin the window size so
        // the Show pass never lays the toast out at a stale/zero size.
        ToastBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Width = ToastBorder.DesiredSize.Width;
        Height = ToastBorder.DesiredSize.Height;

        var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position);

        // Vertically center on the dot's raw-pixel center. The measured height is DIPs — convert
        // via the per-axis device transform, then round and CLAMP to the monitor's top edge: at
        // 150% DPI a ~22-DIP toast is ~33 raw px and 16 - 16.5 would otherwise push its top above
        // the screen. Zero-scale guard falls back to identity (same acceptance as the dot).
        var device = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                     ?? Matrix.Identity;
        var scale = device.M22 > 0 ? device.M22 : 1.0;
        var rawHeight = Height * scale;
        var y = Math.Max(
            screen.Bounds.Y,
            screen.Bounds.Y + DotCenterRawY - (int)Math.Round(rawHeight / 2));

        // SWP_NOSIZE is mandatory: cx/cy are 0 here and sizing was done via the WPF Width/Height.
        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOPMOST,
            screen.Bounds.X + AnchorRawX, y,
            0, 0,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE);

        Show();
    }

    public void HideToast() => Hide();
}
