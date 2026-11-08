using System.Diagnostics;
using System.Windows;

namespace DesktopSplitter.Services;

/// <summary>
/// Emergency off. A second, tiny DesktopSplitter process ("--watchdog &lt;pid&gt;") that owns
/// Ctrl+Alt+Shift+End. Because it is a separate process, the hotkey still works when the main
/// app hangs: it ends the main app and reverts the split. It exits together with the main app.
/// </summary>
public static class EmergencyWatchdog
{
    public const string Argument = "--watchdog";
    private const uint VK_END = 0x23;

    /// <summary>Starts the watchdog for the current process. Never throws.</summary>
    public static void Launch()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null) return;
            Process.Start(new ProcessStartInfo(exe, $"{Argument} {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
        }
        catch
        {
            // No watchdog is not worth failing startup over.
        }
    }

    /// <summary>True if <paramref name="args"/> ask for watchdog mode; then runs it.</summary>
    public static bool TryRun(Application app, string[] args)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;

        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Process? parent = null;
        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int pid))
        {
            try { parent = Process.GetProcessById(pid); } catch { parent = null; }
        }
        if (parent is null)
        {
            app.Shutdown();
            return true;
        }

        var hotkeys = new GlobalHotkeyService();
        bool firing = false;

        parent.EnableRaisingEvents = true;
        parent.Exited += (_, _) => app.Dispatcher.InvokeAsync(() =>
        {
            if (firing) return; // we killed it ourselves; finish the revert first
            hotkeys.Dispose();
            app.Shutdown();
        });

        hotkeys.Pressed += async (_, _) =>
        {
            if (firing) return;
            firing = true;
            try
            {
                try { if (!parent.HasExited) parent.Kill(); } catch { /* already gone */ }
                try { parent.WaitForExit(5000); } catch { /* ignore */ }

                await Task.Run(() =>
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { ApplyService.Revert(new Progress<string>(_ => { }), cts.Token); }
                    catch { /* best effort: also stop the compositor below */ }
                    try { CompositorLauncher.StopAndWait(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
                    try { ApplyService.MarkSplitInactive(deliberate: true); } catch { /* ignore */ }
                });
            }
            finally
            {
                hotkeys.Dispose();
                app.Shutdown();
            }
        };

        const uint mods = GlobalHotkeyService.MOD_CONTROL | GlobalHotkeyService.MOD_ALT | GlobalHotkeyService.MOD_SHIFT;
        if (!hotkeys.Register("panic", mods, VK_END))
        {
            hotkeys.Dispose();
            app.Shutdown();
        }
        else if (parent.HasExited)
        {
            hotkeys.Dispose();
            app.Shutdown();
        }
        return true;
    }
}
