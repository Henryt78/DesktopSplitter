using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;
using WpfBorder = System.Windows.Controls.Border;

namespace DesktopSplitter.Services;

/// <summary>
/// Click-through, always-on-top overlays on the segments: a coloured frame around one segment
/// (e.g. the one you share or record) and thin divider lines between neighbouring segments.
/// They sit on the segments themselves, so a capture of that segment includes them.
/// UI thread only.
/// </summary>
public sealed class OverlayService : IDisposable
{
    private readonly List<Window> _windows = new();

    public void Refresh(ExtrasSettings settings)
    {
        Clear();
        if (!settings.BorderEnabled && !settings.DividerEnabled) return;

        List<MonitorInfo> segments;
        try
        {
            segments = DisplayConfig.Enumerate()
                .Where(m => m.IsDeskSplitVirtual && m.DeskSplitMonitorIndex >= 0)
                .OrderBy(m => m.DeskSplitMonitorIndex)
                .ToList();
        }
        catch { return; }
        if (segments.Count == 0) return;

        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(settings.BorderColor); }
        catch { color = Color.FromRgb(0xE5, 0x39, 0x35); }

        if (settings.BorderEnabled)
        {
            MonitorInfo? s = segments.FirstOrDefault(m => m.DeskSplitMonitorIndex == settings.BorderSegment - 1);
            if (s is not null)
                Show(s.PositionX, s.PositionY, s.CurrentWidth, s.CurrentHeight,
                     new WpfBorder { BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(4) });
        }

        if (settings.DividerEnabled)
        {
            var line = new SolidColorBrush(Color.FromArgb(0xC0, 0x80, 0x80, 0x80));
            const int thickness = 2;
            foreach (MonitorInfo a in segments)
            foreach (MonitorInfo b in segments)
            {
                if (ReferenceEquals(a, b)) continue;
                // b directly right of a, overlapping vertically: line on a's right edge.
                if (b.PositionX == a.PositionX + a.CurrentWidth)
                {
                    int top = Math.Max(a.PositionY, b.PositionY);
                    int bottom = Math.Min(a.PositionY + a.CurrentHeight, b.PositionY + b.CurrentHeight);
                    if (bottom > top)
                        Show(a.PositionX + a.CurrentWidth - thickness, top, thickness, bottom - top,
                             new WpfBorder { Background = line });
                }
                // b directly below a: line on a's bottom edge.
                if (b.PositionY == a.PositionY + a.CurrentHeight)
                {
                    int left = Math.Max(a.PositionX, b.PositionX);
                    int right = Math.Min(a.PositionX + a.CurrentWidth, b.PositionX + b.CurrentWidth);
                    if (right > left)
                        Show(left, a.PositionY + a.CurrentHeight - thickness, right - left, thickness,
                             new WpfBorder { Background = line });
                }
            }
        }
    }

    public void Clear()
    {
        foreach (Window w in _windows) w.Close();
        _windows.Clear();
    }

    public void Dispose() => Clear();

    /// <summary>Shows a transparent, click-through window at a rect in physical desktop pixels.</summary>
    private void Show(int x, int y, int width, int height, UIElement content)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            ResizeMode = ResizeMode.NoResize,
            Content = content,
            Left = -32000,
            Top = -32000,
            Width = 1,
            Height = 1,
        };
        w.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            // Physical pixels, independent of WPF's DPI scaling.
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        };
        w.Show();
        // A DPI change on arrival can rescale the window; pin it again.
        IntPtr h = new WindowInteropHelper(w).Handle;
        SetWindowPos(h, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _windows.Add(w);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20;
    private const long WS_EX_LAYERED = 0x80000;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
