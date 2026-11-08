using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace DesktopSplitter.Services;

/// <summary>
/// System-wide hotkeys via RegisterHotKey on a hidden message-only window.
/// Each hotkey has a caller-chosen name. Must be created and used on the UI thread.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;
    private const int FirstId = 0x5D51;

    private readonly HwndSource _source;
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _names = new();
    private readonly HashSet<int> _registered = new();
    private int _nextId = FirstId;
    private bool _disposed;

    public GlobalHotkeyService()
    {
        var parameters = new HwndSourceParameters("DesktopSplitterHotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Raised with the hotkey's name.</summary>
    public event EventHandler<string>? Pressed;

    /// <summary>(Re-)registers <paramref name="name"/>. False if another app already owns the combination.</summary>
    public bool Register(string name, uint modifiers, uint virtualKey)
    {
        Unregister(name);
        if (_disposed || virtualKey == 0 || modifiers == 0) return false;
        if (!_ids.TryGetValue(name, out int id))
        {
            id = _nextId++;
            _ids[name] = id;
            _names[id] = name;
        }
        bool ok = RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, virtualKey);
        if (ok) _registered.Add(id);
        return ok;
    }

    public void Unregister(string name)
    {
        if (!_ids.TryGetValue(name, out int id) || !_registered.Remove(id)) return;
        UnregisterHotKey(_source.Handle, id);
    }

    public void UnregisterWhere(Func<string, bool> predicate)
    {
        foreach (string name in _ids.Keys.Where(predicate).ToList()) Unregister(name);
    }

    public static string Describe(uint modifiers, uint virtualKey)
    {
        if (virtualKey == 0) return "(none)";
        var sb = new StringBuilder();
        if ((modifiers & MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((modifiers & MOD_ALT) != 0) sb.Append("Alt+");
        if ((modifiers & MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((modifiers & MOD_WIN) != 0) sb.Append("Win+");
        var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)virtualKey);
        string name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1..];
        sb.Append(name);
        return sb.ToString();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _names.TryGetValue(wParam.ToInt32(), out string? name))
        {
            handled = true;
            Pressed?.Invoke(this, name);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (int id in _registered) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
        _disposed = true;
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
