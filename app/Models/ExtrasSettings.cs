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

    /// <summary>Moves the active window to the next monitor. Default Ctrl+Alt+Shift+M.</summary>
    [JsonPropertyName("moveHotkeyModifiers")] public uint MoveHotkeyModifiers { get; set; } = 0x7;
    [JsonPropertyName("moveHotkeyKey")] public uint MoveHotkeyKey { get; set; } = 0x4D;

    /// <summary>Toggles recording mode. Default Ctrl+Alt+Shift+R.</summary>
    [JsonPropertyName("recordHotkeyModifiers")] public uint RecordHotkeyModifiers { get; set; } = 0x7;
    [JsonPropertyName("recordHotkeyKey")] public uint RecordHotkeyKey { get; set; } = 0x52;

    /// <summary>Ctrl+Alt+Shift+1..9 apply profiles 1..9.</summary>
    [JsonPropertyName("profileHotkeysEnabled")] public bool ProfileHotkeysEnabled { get; set; } = true;

    /// <summary>Primary monitor under a split: "segment1".."segmentN" or a real monitor's device name.</summary>
    [JsonPropertyName("primary")] public string Primary { get; set; } = "segment1";

    /// <summary>
    /// Recording mode: primary (notifications, Start menu, most pop-ups) moves to
    /// <see cref="RecordingPrimary"/> so the recorded segments stay clean.
    /// </summary>
    [JsonPropertyName("recordingMode")] public bool RecordingMode { get; set; }
    [JsonPropertyName("recordingPrimary")] public string RecordingPrimary { get; set; } = string.Empty;

    [JsonPropertyName("borderEnabled")] public bool BorderEnabled { get; set; }
    /// <summary>1-based segment the coloured frame marks.</summary>
    [JsonPropertyName("borderSegment")] public int BorderSegment { get; set; } = 1;
    [JsonPropertyName("borderColor")] public string BorderColor { get; set; } = "#E53935";
    [JsonPropertyName("dividerEnabled")] public bool DividerEnabled { get; set; }

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

    /// <summary>Window layout restored after this profile is applied.</summary>
    [JsonPropertyName("windows")]
    public List<SavedWindow> Windows { get; set; } = new();

    public override string ToString() => Name;
}

/// <summary>One window's saved place: monitor key ("segment1", or a device name) + rect relative to it.</summary>
public sealed class SavedWindow
{
    [JsonPropertyName("process")] public string Process { get; set; } = string.Empty;
    [JsonPropertyName("class")] public string ClassName { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("monitor")] public string Monitor { get; set; } = string.Empty;
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("w")] public int Width { get; set; }
    [JsonPropertyName("h")] public int Height { get; set; }
    [JsonPropertyName("maximized")] public bool Maximized { get; set; }
}
