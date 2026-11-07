using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

/// <summary>
/// Decides where the virtual monitors (and the covered physical monitor) go in the virtual desktop.
///
/// Default: the split takes the physical monitor's place among the other real monitors, and the
/// covered physical monitor moves to the far right edge. With a monitor left of the split one,
/// the cursor then crosses straight from it into segment 1 instead of hitting the covered area.
///
/// If the user arranged the monitors in Windows display settings and saved that arrangement,
/// it is reused on every apply of the same monitor + layout.
/// </summary>
public static class SplitArrangement
{
    public const string PhysicalKey = "physical";
    public static string SegmentKey(int index) => $"segment{index + 1}";

    /// <summary>Call before a split is built. Records the un-split desktop when no split is active.</summary>
    public static void RecordHome(IReadOnlyList<MonitorInfo> before, MonitorInfo physical, Action<string>? log = null)
    {
        try
        {
            if (before.Any(m => m.IsDeskSplitVirtual)) return; // only an un-split desktop is "home"
            MonitorInfo? reference = PickReference(before, physical.DeviceName, preferred: null);
            if (reference is null) return;

            var home = new MonitorHome
            {
                PhysicalDevice = physical.DeviceName,
                ReferenceDevice = reference.DeviceName,
                Monitors = before.Select(m => Relative(m.DeviceName, m, reference)).ToList(),
            };

            ExtrasSettings extras = ExtrasStore.Load();
            extras.Homes.RemoveAll(h => Same(h.PhysicalDevice, physical.DeviceName));
            extras.Homes.Add(home);
            ExtrasStore.Save(extras);
        }
        catch (Exception ex)
        {
            log?.Invoke("Note: could not record the monitor arrangement: " + ex.Message);
        }
    }

    /// <summary>Full desktop layout (re-based to <paramref name="primaryDevice"/>) for the split.</summary>
    public static IReadOnlyList<DisplayArranger.MonitorPlacement> Compose(
        IReadOnlyList<MonitorInfo> current,
        MonitorInfo physicalNow,
        LayoutDefinition layout,
        IReadOnlyList<MonitorInfo> virtualMonitors,
        IReadOnlyList<(int Width, int Height)> resolutions,
        int virtualRefreshHz,
        string primaryDevice,
        Action<string> log)
    {
        ExtrasSettings extras;
        try { extras = ExtrasStore.Load(); }
        catch { extras = new ExtrasSettings(); }

        var others = current
            .Where(m => !m.IsDeskSplitVirtual && !Same(m.DeviceName, physicalNow.DeviceName))
            .ToList();

        // Nothing else connected: keep the original behaviour (split right of the physical one).
        if (others.Count == 0)
        {
            var original = DisplayArranger.ComputePlacements(physicalNow, layout, virtualMonitors, resolutions);
            return DisplayArranger.ComposeLayout(current, original, virtualRefreshHz, primaryDevice);
        }

        var placements = TrySaved(extras, current, others, physicalNow, layout, virtualMonitors, resolutions,
                                  virtualRefreshHz, log)
                         ?? Default(extras, others, physicalNow, layout, virtualMonitors, resolutions,
                                    virtualRefreshHz, log);

        // Anything we did not place (e.g. another vendor's virtual monitor) keeps its rect.
        foreach (MonitorInfo m in current)
        {
            if (placements.Any(p => Same(p.DeviceName, m.DeviceName))) continue;
            placements.Add(new DisplayArranger.MonitorPlacement(
                m.DeviceName, m.CurrentWidth, m.CurrentHeight, m.PositionX, m.PositionY, 0, false));
        }

        return DisplayArranger.RebaseToPrimary(placements, primaryDevice);
    }

    /// <summary>Snapshot of the live split arrangement, for "Save arrangement".</summary>
    public static SavedArrangement? CaptureLive(string physicalDevice, LayoutKind layout, out string error)
    {
        error = string.Empty;
        IReadOnlyList<MonitorInfo> current = DisplayConfig.Enumerate();
        MonitorInfo? physical = current.FirstOrDefault(m => Same(m.DeviceName, physicalDevice));
        var virtuals = current.Where(m => m.IsDeskSplitVirtual && m.DeskSplitMonitorIndex >= 0)
                              .OrderBy(m => m.DeskSplitMonitorIndex).ToList();
        if (virtuals.Count == 0)
        {
            error = "The split is not active — apply it first, arrange it in Windows, then save.";
            return null;
        }
        if (physical is null)
        {
            error = "The split monitor is not part of the desktop (hidden?) — nothing to save.";
            return null;
        }
        MonitorInfo? reference = PickReference(current, physicalDevice, preferred: null);
        if (reference is null)
        {
            error = "No other monitor is connected, so there is no arrangement to save.";
            return null;
        }

        var saved = new SavedArrangement
        {
            PhysicalDevice = physicalDevice,
            ReferenceDevice = reference.DeviceName,
            Layout = layout,
        };
        saved.Rects.Add(Relative(PhysicalKey, physical, reference));
        foreach (MonitorInfo v in virtuals)
            saved.Rects.Add(Relative(SegmentKey(v.DeskSplitMonitorIndex), v, reference));
        return saved;
    }

    // ------------------------------------------------------------------ strategies

    private static List<DisplayArranger.MonitorPlacement>? TrySaved(
        ExtrasSettings extras, IReadOnlyList<MonitorInfo> current, List<MonitorInfo> others,
        MonitorInfo physicalNow, LayoutDefinition layout, IReadOnlyList<MonitorInfo> virtualMonitors,
        IReadOnlyList<(int Width, int Height)> resolutions, int refreshHz, Action<string> log)
    {
        SavedArrangement? saved = extras.Arrangements.FirstOrDefault(
            a => Same(a.PhysicalDevice, physicalNow.DeviceName) && a.Layout == layout.Kind);
        if (saved is null) return null;

        MonitorInfo? reference = others.FirstOrDefault(m => Same(m.DeviceName, saved.ReferenceDevice));
        RelativeRect? phys = saved.Rects.FirstOrDefault(r => r.Key == PhysicalKey);
        if (reference is null || phys is null) return null;

        var result = others.Select(Keep).ToList();
        for (int i = 0; i < virtualMonitors.Count; i++)
        {
            RelativeRect? r = saved.Rects.FirstOrDefault(x => x.Key == SegmentKey(i));
            (int w, int h) = resolutions[i];
            if (r is null || r.Width != w || r.Height != h)
            {
                log("Saved arrangement does not match the current segment sizes — using the automatic one.");
                return null;
            }
            result.Add(new DisplayArranger.MonitorPlacement(virtualMonitors[i].DeviceName, w, h,
                reference.PositionX + r.Dx, reference.PositionY + r.Dy, refreshHz, false));
        }
        result.Add(new DisplayArranger.MonitorPlacement(physicalNow.DeviceName,
            physicalNow.CurrentWidth, physicalNow.CurrentHeight,
            reference.PositionX + phys.Dx, reference.PositionY + phys.Dy, 0, false));

        log("Using your saved monitor arrangement.");
        return result;
    }

    private static List<DisplayArranger.MonitorPlacement> Default(
        ExtrasSettings extras, List<MonitorInfo> others, MonitorInfo physicalNow, LayoutDefinition layout,
        IReadOnlyList<MonitorInfo> virtualMonitors, IReadOnlyList<(int Width, int Height)> resolutions,
        int refreshHz, Action<string> log)
    {
        // Where things belong: the recorded un-split desktop if we have one (Windows may already
        // have shuffled monitors around while the virtual ones arrived), else the live positions.
        MonitorHome? home = extras.Homes.FirstOrDefault(h => Same(h.PhysicalDevice, physicalNow.DeviceName));
        MonitorInfo? reference = home is null ? null : others.FirstOrDefault(m => Same(m.DeviceName, home.ReferenceDevice));

        (int X, int Y) HomeOf(MonitorInfo m)
        {
            RelativeRect? r = home?.Monitors.FirstOrDefault(x => Same(x.Key, m.DeviceName));
            return reference is not null && r is not null
                ? (reference.PositionX + r.Dx, reference.PositionY + r.Dy)
                : (m.PositionX, m.PositionY);
        }

        (int ax, int ay) = HomeOf(physicalNow);
        int physRight = ax + physicalNow.CurrentWidth;

        LayoutGeometry.Tracks tracks = LayoutGeometry.ComputeTracks(layout, resolutions);
        int blockWidth = tracks.ColumnWidths.Sum();
        int shift = blockWidth - physicalNow.CurrentWidth;

        var result = new List<DisplayArranger.MonitorPlacement>();
        foreach (MonitorInfo o in others)
        {
            (int x, int y) = HomeOf(o);
            if (x >= physRight) x += shift; // monitors right of the split one make room for a wider block
            result.Add(new DisplayArranger.MonitorPlacement(o.DeviceName, o.CurrentWidth, o.CurrentHeight, x, y, 0, false));
        }

        for (int i = 0; i < virtualMonitors.Count; i++)
        {
            LayoutCell cell = layout.Cells[i];
            (int w, int h) = resolutions[i];
            int x = ax;
            for (int c = 0; c < cell.Col; c++) x += tracks.ColumnWidths[c];
            int y = ay;
            for (int r = 0; r < cell.Row; r++) y += tracks.RowHeights[r];
            result.Add(new DisplayArranger.MonitorPlacement(virtualMonitors[i].DeviceName, w, h, x, y, refreshHz, false));
        }

        // The covered physical monitor goes to the far right, top-aligned with the split.
        int farRight = result.Max(p => p.Right);
        result.Add(new DisplayArranger.MonitorPlacement(physicalNow.DeviceName,
            physicalNow.CurrentWidth, physicalNow.CurrentHeight, farRight, ay, 0, false));

        log("Placed the split where the monitor was; the covered physical monitor moves to the far right.");
        return result;
    }

    // ------------------------------------------------------------------ helpers

    private static DisplayArranger.MonitorPlacement Keep(MonitorInfo m)
        => new(m.DeviceName, m.CurrentWidth, m.CurrentHeight, m.PositionX, m.PositionY, 0, false);

    private static MonitorInfo? PickReference(IReadOnlyList<MonitorInfo> monitors, string physicalDevice, string? preferred)
    {
        var candidates = monitors
            .Where(m => !m.IsDeskSplitVirtual && !Same(m.DeviceName, physicalDevice))
            .OrderBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return candidates.FirstOrDefault(m => preferred is not null && Same(m.DeviceName, preferred))
               ?? candidates.FirstOrDefault();
    }

    private static RelativeRect Relative(string key, MonitorInfo m, MonitorInfo reference) => new()
    {
        Key = key,
        Dx = m.PositionX - reference.PositionX,
        Dy = m.PositionY - reference.PositionY,
        Width = m.CurrentWidth,
        Height = m.CurrentHeight,
    };

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
