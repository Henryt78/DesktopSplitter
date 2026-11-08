using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

/// <summary>
/// Moves top-level windows between monitors and saves/restores window layouts.
/// The covered physical monitor of an active split is never a target.
/// All coordinates are physical desktop pixels (the app is per-monitor DPI aware).
/// </summary>
public static class WindowMover
{
    /// <summary>A monitor windows can live on, with a stable key for saving layouts.</summary>
    public sealed record Target(string Key, string DeviceName, int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
    }

    /// <summary>Monitors windows may go to, left-to-right then top-to-bottom.</summary>
    public static IReadOnlyList<Target> Targets()
    {
        IReadOnlyList<MonitorInfo> monitors;
        try { monitors = DisplayConfig.Enumerate(); }
        catch { return Array.Empty<Target>(); }

        bool split = monitors.Any(m => m.IsDeskSplitVirtual);
        string covered = split ? SettingsStore.Load()?.PhysicalDevice ?? string.Empty : string.Empty;

        return monitors
            .Where(m => !string.Equals(m.DeviceName, covered, StringComparison.OrdinalIgnoreCase))
            .Select(m => new Target(
                m.IsDeskSplitVirtual && m.DeskSplitMonitorIndex >= 0
                    ? SplitArrangement.SegmentKey(m.DeskSplitMonitorIndex)
                    : m.DeviceName,
                m.DeviceName, m.PositionX, m.PositionY, m.CurrentWidth, m.CurrentHeight))
            .OrderBy(t => t.X).ThenBy(t => t.Y)
            .ToList();
    }

    // ------------------------------------------------------------------ move foreground window

    /// <summary>Moves the foreground window to the next monitor (cyclic). Returns a status line.</summary>
    public static string MoveForegroundToNext()
    {
        IntPtr hwnd = GetAncestor(GetForegroundWindow(), GA_ROOT);
        if (hwnd == IntPtr.Zero || !IsMovable(hwnd)) return "No movable window in front.";

        var targets = Targets();
        if (targets.Count < 2) return "Only one monitor — nothing to move to.";

        if (!GetWindowRect(hwnd, out RECT r)) return "Could not read the window position.";
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        int current = -1;
        for (int i = 0; i < targets.Count; i++)
            if (targets[i].Contains(cx, cy)) { current = i; break; }

        Target from = current >= 0 ? targets[current] : targets[0];
        Target to = targets[(current + 1 + targets.Count) % targets.Count];
        bool maximized = IsZoomed(hwnd);
        if (maximized) ShowWindow(hwnd, SW_RESTORE);
        if (!GetWindowRect(hwnd, out r)) return "Could not read the window position.";

        // Same relative position and size, scaled to the target, clamped inside it.
        double sx = (double)to.Width / from.Width, sy = (double)to.Height / from.Height;
        int w = Math.Min(to.Width, Math.Max(200, (int)((r.Right - r.Left) * sx)));
        int h = Math.Min(to.Height, Math.Max(150, (int)((r.Bottom - r.Top) * sy)));
        int x = to.X + (int)((r.Left - from.X) * sx);
        int y = to.Y + (int)((r.Top - from.Y) * sy);
        x = Math.Clamp(x, to.X, to.Right - w);
        y = Math.Clamp(y, to.Y, to.Bottom - h);

        Place(hwnd, x, y, w, h, maximized);
        return string.Empty;
    }

    // ------------------------------------------------------------------ window layouts

    /// <summary>Records the visible app windows and where they are.</summary>
    public static List<SavedWindow> CaptureLayout()
    {
        var targets = Targets();
        var result = new List<SavedWindow>();
        foreach (IntPtr hwnd in AppWindows())
        {
            if (IsIconic(hwnd) || !GetWindowRect(hwnd, out RECT r)) continue;
            bool maximized = IsZoomed(hwnd);

            // The centre picks the monitor (a maximized rect overhangs its monitor a little).
            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            Target? t = targets.FirstOrDefault(x => x.Contains(cx, cy));
            if (t is null) continue;

            if (maximized)
            {
                // Keep the restored size, centred on the monitor, so un-maximizing later lands sensibly.
                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (GetWindowPlacement(hwnd, ref wp))
                {
                    int nw = Math.Min(t.Width, wp.rcNormalPosition.Right - wp.rcNormalPosition.Left);
                    int nh = Math.Min(t.Height, wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top);
                    int left = t.X + (t.Width - nw) / 2, top = t.Y + (t.Height - nh) / 2;
                    r = new RECT { Left = left, Top = top, Right = left + nw, Bottom = top + nh };
                }
            }

            string process = ProcessName(hwnd);
            if (process.Length == 0) continue;
            result.Add(new SavedWindow
            {
                Process = process,
                ClassName = ClassName(hwnd),
                Title = Title(hwnd),
                Monitor = t.Key,
                X = r.Left - t.X,
                Y = r.Top - t.Y,
                Width = r.Right - r.Left,
                Height = r.Bottom - r.Top,
                Maximized = maximized,
            });
        }
        return result;
    }

    /// <summary>Moves running windows to their saved places. Returns how many were placed.</summary>
    public static int RestoreLayout(IReadOnlyList<SavedWindow> saved)
    {
        if (saved.Count == 0) return 0;
        var targets = Targets();
        var used = new HashSet<IntPtr>();
        var windows = AppWindows()
            .Select(h => (Handle: h, Process: ProcessName(h), Class: ClassName(h), Title: Title(h)))
            .ToList();

        int placed = 0;
        foreach (SavedWindow s in saved)
        {
            Target? t = targets.FirstOrDefault(x => string.Equals(x.Key, s.Monitor, StringComparison.OrdinalIgnoreCase));
            if (t is null) continue;

            var candidates = windows
                .Where(w => !used.Contains(w.Handle) && string.Equals(w.Process, s.Process, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(w => (w.Class == s.ClassName ? 2 : 0) + (w.Title == s.Title ? 1 : 0))
                .ToList();
            if (candidates.Count == 0) continue;

            IntPtr hwnd = candidates[0].Handle;
            used.Add(hwnd);

            int w = Math.Min(s.Width, t.Width), h = Math.Min(s.Height, t.Height);
            int x = Math.Clamp(t.X + s.X, t.X, t.Right - w);
            int y = Math.Clamp(t.Y + s.Y, t.Y, t.Bottom - h);
            if (IsZoomed(hwnd) || IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            Place(hwnd, x, y, w, h, s.Maximized);
            placed++;
        }
        return placed;
    }

    // ------------------------------------------------------------------ helpers

    private static void Place(IntPtr hwnd, int x, int y, int w, int h, bool maximize)
    {
        // Moving across monitors with different DPI can resize the window once it arrives;
        // a second SetWindowPos settles it at the requested size.
        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        if (maximize) ShowWindow(hwnd, SW_MAXIMIZE);
    }

    private static List<IntPtr> AppWindows()
    {
        var list = new List<IntPtr>();
        uint ownPid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (IsMovable(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != ownPid) list.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static bool IsMovable(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0 || (ex & WS_EX_NOACTIVATE) != 0) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if (GetWindowTextLength(hwnd) == 0) return false;
        string cls = ClassName(hwnd);
        return cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    private static string ProcessName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return string.Empty; }
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    private static string Title(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Win32

    private const uint GA_ROOT = 2;
    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
