using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using WinForms = System.Windows.Forms;

namespace sWinShortcuts.Views;

// Port of the AHK crosshair overlay: click-through, always-on-top, ~70% opacity image centered on
// the game window's monitor. All focus-stealing vectors are closed off (ShowActivated=false,
// WS_EX_NOACTIVATE, SWP_NOACTIVATE, WS_EX_TRANSPARENT, Focusable=false, IsHitTestVisible=false).
public partial class CrosshairWindow : Window
{
    private const string DefaultImagePackUri = "pack://application:,,,/Icons/Crosshair.png";

    private IntPtr _hwnd;

    public CrosshairWindow()
    {
        InitializeComponent();
        Opacity = CrosshairSettings.DefaultOpacity;
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

    // Called on the UI dispatcher by CrosshairService. targetHwnd is the game's foreground window;
    // IntPtr.Zero positions on the primary screen. Never throws — a bad path decodes to the
    // bundled default instead.
    public void ApplyConfiguration(IntPtr targetHwnd, string? imagePath)
    {
        // Create the HWND while still hidden so ex-styles are in place before the first Show.
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
        }

        var image = LoadImage(imagePath);
        CrosshairImage.Source = image;

        // Center on the game window's monitor, in RAW pixels (Screen.Bounds, not DIPs).
        var screen = targetHwnd != IntPtr.Zero
            ? WinForms.Screen.FromHandle(targetHwnd)
            : WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0];
        var bounds = screen.Bounds;
        var x = bounds.X + ((bounds.Width - image.PixelWidth) / 2);
        var y = bounds.Y + ((bounds.Height - image.PixelHeight) / 2);
        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE);

        // Size in DIPs so the overlay occupies exactly PixelWidth x PixelHeight physical pixels
        // (100% scale at any display DPI). Stretch=Fill + explicit element size ignores any DPI
        // metadata embedded in the PNG, which would otherwise rescale a "natural size" render.
        var device = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                     ?? Matrix.Identity;
        var width = image.PixelWidth / device.M11;
        var height = image.PixelHeight / device.M22;
        Width = width;
        Height = height;
        CrosshairImage.Width = width;
        CrosshairImage.Height = height;
    }

    public void ShowOverlay() => Show();

    public void HideOverlay() => Hide();

    private static BitmapImage LoadImage(string? imagePath)
    {
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            try
            {
                // StreamSource (not UriSource): the WPF URI image cache would serve the previously
                // decoded copy when the same path is re-picked after an edit. OnLoad fully reads the
                // stream at EndInit, so the file handle is released immediately — no lock on the PNG.
                using var stream = System.IO.File.OpenRead(imagePath);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                if (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
                {
                    return bitmap;
                }
            }
            catch (Exception)
            {
                // Missing/locked/undecodable file: fall through to the bundled default.
            }
        }

        var fallback = new BitmapImage();
        fallback.BeginInit();
        fallback.CacheOption = BitmapCacheOption.OnLoad;
        fallback.UriSource = new Uri(DefaultImagePackUri);
        fallback.EndInit();
        fallback.Freeze();
        return fallback;
    }
}
