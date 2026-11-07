using System.Text.Json.Serialization;

namespace DesktopSplitter.Models;

/// <summary>
/// %APPDATA%\DesktopSplitter\extras.json — saved layout profiles, the global toggle hotkey and
/// per-app auto-switching. Kept in its own file on purpose: ApplyService rewrites settings.json
/// from scratch on every Apply, which would silently drop anything added there.
/// </summary>
public sealed class ExtrasSettings
{
    [JsonPropertyName("profiles")]
    public List<LayoutProfile> Profiles { get; set; } = new();

    [JsonPropertyName("hotkeyEnabled")]
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>RegisterHotKey modifier flags (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8).</summary>
    [JsonPropertyName("hotkeyModifiers")]
    public uint HotkeyModifiers { get; set; } = 0x2 | 0x1 | 0x4; // Ctrl+Alt+Shift

    /// <summary>Win32 virtual-key code. Default: D.</summary>
    [JsonPropertyName("hotkeyKey")]
    public uint HotkeyKey { get; set; } = 0x44;

    [JsonPropertyName("autoSwitchEnabled")]
    public bool AutoSwitchEnabled { get; set; }

    /// <summary>Where each split monitor sat among the other real monitors before any split.</summary>
    [JsonPropertyName("homes")]
    public List<MonitorHome> Homes { get; set; } = new();

    /// <summary>Desktop arrangements the user dragged in Windows display settings and saved.</summary>
    [JsonPropertyName("arrangements")]
    public List<SavedArrangement> Arrangements { get; set; } = new();
}

/// <summary>A monitor rect stored relative to a reference monitor's top-left corner.</summary>
public sealed class RelativeRect
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("dx")] public int Dx { get; set; }
    [JsonPropertyName("dy")] public int Dy { get; set; }
    [JsonPropertyName("w")] public int Width { get; set; }
    [JsonPropertyName("h")] public int Height { get; set; }
}

/// <summary>Un-split desktop around one physical monitor (all real monitors, relative to a reference).</summary>
public sealed class MonitorHome
{
    [JsonPropertyName("physicalDevice")] public string PhysicalDevice { get; set; } = string.Empty;
    [JsonPropertyName("referenceDevice")] public string ReferenceDevice { get; set; } = string.Empty;
    /// <summary>Key = device name of each real monitor (including the physical one).</summary>
    [JsonPropertyName("monitors")] public List<RelativeRect> Monitors { get; set; } = new();
}

/// <summary>User-saved arrangement: keys "segment1".."segmentN" and "physical".</summary>
public sealed class SavedArrangement
{
    [JsonPropertyName("physicalDevice")] public string PhysicalDevice { get; set; } = string.Empty;
    [JsonPropertyName("referenceDevice")] public string ReferenceDevice { get; set; } = string.Empty;
    [JsonPropertyName("layout")] public LayoutKind Layout { get; set; }
    [JsonPropertyName("rects")] public List<RelativeRect> Rects { get; set; } = new();
}

/// <summary>A named, re-appliable split configuration.</summary>
public sealed class LayoutProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("physicalDevice")]
    public string PhysicalDevice { get; set; } = string.Empty;

    [JsonPropertyName("physicalFriendlyName")]
    public string PhysicalFriendlyName { get; set; } = string.Empty;

    [JsonPropertyName("layout")]
    public LayoutKind Layout { get; set; } = LayoutKind.TwoVertical;

    [JsonPropertyName("segments")]
    public List<SegmentSize> Segments { get; set; } = new();

    /// <summary>Process names (e.g. "obs64", "reaper"); while any runs, auto-switch applies this profile.</summary>
    [JsonPropertyName("triggerApps")]
    public List<string> TriggerApps { get; set; } = new();

    public override string ToString() => Name;
}
