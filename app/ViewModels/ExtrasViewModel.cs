using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopSplitter.Models;
using DesktopSplitter.Services;

namespace DesktopSplitter.ViewModels;

/// <summary>
/// Named layout profiles, the global on/off hotkey and per-app auto-switching.
/// Lives on the UI thread; everything display-related is delegated to <see cref="MainViewModel"/>.
/// </summary>
public sealed class ExtrasViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ExtrasSettings _settings;
    private readonly DispatcherTimer _watchTimer;
    private GlobalHotkeyService? _hotkey;

    private LayoutProfile? _selectedProfile;
    private string _newProfileName = string.Empty;
    private string _triggerAppsText = string.Empty;
    private string _hotkeyStatus = string.Empty;
    private bool _watchTickRunning;
    private double _splitRatio = 50;
    private readonly OverlayService _overlays = new();

    // Auto-switch state.
    private LayoutProfile? _autoActive;          // profile auto-switch applied
    private LayoutProfile? _autoRestoreTo;       // what was running before (null = no split)
    private LayoutProfile? _autoSuppressed;      // user took over manually while this was active

    public ExtrasViewModel(MainViewModel main)
    {
        _main = main;
        _settings = ExtrasStore.Load();
        Profiles = new ObservableCollection<LayoutProfile>(_settings.Profiles);
        _selectedProfile = Profiles.FirstOrDefault();
        _triggerAppsText = FormatTriggers(_selectedProfile);

        SaveProfileCommand = new RelayCommand(SaveProfile, () => !_main.IsBusy);
        LoadProfileCommand = new RelayCommand(LoadProfile, () => SelectedProfile is not null && !_main.IsBusy);
        ApplyProfileCommand = new RelayCommand(async () => await ApplySelectedAsync().ConfigureAwait(true),
                                               () => SelectedProfile is not null && !_main.IsBusy);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedProfile is not null && !_main.IsBusy);
        OpenDisplaySettingsCommand = new RelayCommand(OpenDisplaySettings);
        SaveArrangementCommand = new RelayCommand(SaveArrangement, () => !_main.IsBusy);
        ResetArrangementCommand = new RelayCommand(ResetArrangement, () => !_main.IsBusy);
        _arrangementStatus = DescribeArrangement();
        SaveWindowsCommand = new RelayCommand(SaveWindows, () => SelectedProfile is not null);
        ClearWindowsCommand = new RelayCommand(ClearWindows, () => SelectedProfile is not null);
        ToggleRecordingCommand = new RelayCommand(async () => await ToggleRecordingAsync().ConfigureAwait(true),
                                                  () => !_main.IsBusy);

        // Recording mode never survives a restart of the app.
        if (_settings.RecordingMode)
        {
            _settings.RecordingMode = false;
            try { ExtrasStore.Save(_settings); } catch { /* best effort */ }
        }

        _main.SplitChanged += (_, _) => RefreshOverlays();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshOverlays));

        _main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedLayout))
                OnPropertyChanged(nameof(SplitRatioAvailable));
            if (e.PropertyName == nameof(MainViewModel.IsBusy))
            {
                RaiseCommands();
                if (!_main.IsBusy) ArrangementStatus = DescribeArrangement();
            }
        };

        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _watchTimer.Tick += async (_, _) => await WatchTickAsync().ConfigureAwait(true);
        if (_settings.AutoSwitchEnabled) _watchTimer.Start();
    }

    public event EventHandler? ProfilesChanged;

    // ------------------------------------------------------------------ profiles

    public ObservableCollection<LayoutProfile> Profiles { get; }

    public LayoutProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            _triggerAppsText = FormatTriggers(value);
            OnPropertyChanged(nameof(TriggerAppsText));
            if (value is not null) NewProfileName = value.Name;
            OnPropertyChanged(nameof(WindowsText));
            RaiseCommands();
        }
    }

    public string NewProfileName
    {
        get => _newProfileName;
        set => SetProperty(ref _newProfileName, value ?? string.Empty);
    }

    /// <summary>Comma-separated process names of the selected profile.</summary>
    public string TriggerAppsText
    {
        get => _triggerAppsText;
        set
        {
            if (!SetProperty(ref _triggerAppsText, value ?? string.Empty)) return;
            if (SelectedProfile is null) return;
            SelectedProfile.TriggerApps = ParseTriggers(_triggerAppsText);
            Persist();
        }
    }

    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand LoadProfileCommand { get; }
    public RelayCommand ApplyProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }

    private void SaveProfile()
    {
        string name = NewProfileName.Trim();
        if (name.Length == 0)
        {
            _main.AppendStatus("Enter a profile name first.");
            return;
        }

        LayoutProfile? captured = _main.CaptureEditor(name);
        if (captured is null)
        {
            _main.AppendStatus("Pick a monitor and layout before saving a profile.");
            return;
        }

        LayoutProfile? existing = Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            captured.TriggerApps = existing.TriggerApps;
            Profiles[Profiles.IndexOf(existing)] = captured;
            _main.AppendStatus($"Profile '{name}' updated.");
        }
        else
        {
            Profiles.Add(captured);
            _main.AppendStatus($"Profile '{name}' saved.");
        }
        SelectedProfile = captured;
        Persist();
    }

    private void LoadProfile()
    {
        LayoutProfile? p = SelectedProfile;
        if (p is null) return;
        if (!_main.LoadConfiguration(p.PhysicalDevice, p.PhysicalFriendlyName, p.Layout, p.Segments,
                                     $"Profile '{p.Name}' loaded into the editor."))
            _main.AppendStatus($"Profile '{p.Name}': monitor '{p.PhysicalFriendlyName}' is not connected.");
    }

    private Task ApplySelectedAsync()
        => SelectedProfile is null ? Task.CompletedTask : ApplyProfileByNameAsync(SelectedProfile.Name, prompt: true);

    /// <summary>Manual apply (UI or tray). Takes precedence over auto-switching until the trigger app exits.</summary>
    public async Task ApplyProfileByNameAsync(string name, bool prompt)
    {
        LayoutProfile? p = Profiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p is null) return;
        TakeOverFromAuto();
        await ApplyProfileWithWindowsAsync(p, prompt).ConfigureAwait(true);
    }

    private void DeleteProfile()
    {
        LayoutProfile? p = SelectedProfile;
        if (p is null) return;
        Profiles.Remove(p);
        if (ReferenceEquals(_autoActive, p)) _autoActive = null;
        SelectedProfile = Profiles.FirstOrDefault();
        _main.AppendStatus($"Profile '{p.Name}' deleted.");
        Persist();
    }

    // ------------------------------------------------------------------ arrangement

    private string _arrangementStatus = string.Empty;

    public RelayCommand OpenDisplaySettingsCommand { get; }
    public RelayCommand SaveArrangementCommand { get; }
    public RelayCommand ResetArrangementCommand { get; }

    public string ArrangementStatus
    {
        get => _arrangementStatus;
        private set => SetProperty(ref _arrangementStatus, value);
    }

    private static void OpenDisplaySettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true })?.Dispose(); }
        catch { /* nothing useful to do */ }
    }

    private void SaveArrangement()
    {
        AppSettings? last = SettingsStore.Load();
        if (last is null)
        {
            _main.AppendStatus("Apply a split first, then arrange it in Windows and save.");
            return;
        }
        try
        {
            SavedArrangement? saved = SplitArrangement.CaptureLive(last.PhysicalDevice, last.Layout, out string error);
            if (saved is null)
            {
                _main.AppendStatus(error);
                return;
            }
            _settings.Arrangements.RemoveAll(a => string.Equals(a.PhysicalDevice, saved.PhysicalDevice,
                                                                 StringComparison.OrdinalIgnoreCase)
                                                  && a.Layout == saved.Layout);
            _settings.Arrangements.Add(saved);
            Persist();
            _main.AppendStatus($"Arrangement saved for {last.PhysicalFriendlyName} ({last.Layout}). It is used on every apply.");
        }
        catch (Exception ex)
        {
            _main.AppendStatus("Could not read the monitor arrangement: " + ex.Message);
        }
        ArrangementStatus = DescribeArrangement();
    }

    private void ResetArrangement()
    {
        AppSettings? last = SettingsStore.Load();
        int removed = last is null
            ? 0
            : _settings.Arrangements.RemoveAll(a => string.Equals(a.PhysicalDevice, last.PhysicalDevice,
                                                                   StringComparison.OrdinalIgnoreCase)
                                                    && a.Layout == last.Layout);
        Persist();
        _main.AppendStatus(removed > 0
            ? "Saved arrangement removed — the next apply arranges the monitors automatically."
            : "No saved arrangement for the current monitor and layout.");
        ArrangementStatus = DescribeArrangement();
    }

    private string DescribeArrangement()
    {
        AppSettings? last = SettingsStore.Load();
        bool saved = last is not null && _settings.Arrangements.Any(
            a => string.Equals(a.PhysicalDevice, last.PhysicalDevice, StringComparison.OrdinalIgnoreCase)
                 && a.Layout == last.Layout);
        return saved
            ? "Using your saved arrangement for the current layout."
            : "Automatic: the segments take the monitor's place next to your other monitors.";
    }

    // ------------------------------------------------------------------ hotkeys

    public bool HotkeyEnabled
    {
        get => _settings.HotkeyEnabled;
        set
        {
            if (_settings.HotkeyEnabled == value) return;
            _settings.HotkeyEnabled = value;
            OnPropertyChanged();
            Persist();
            RegisterHotkeys();
        }
    }

    public bool ProfileHotkeysEnabled
    {
        get => _settings.ProfileHotkeysEnabled;
        set
        {
            if (_settings.ProfileHotkeysEnabled == value) return;
            _settings.ProfileHotkeysEnabled = value;
            OnPropertyChanged();
            Persist();
            RegisterHotkeys();
        }
    }

    public string HotkeyText => GlobalHotkeyService.Describe(_settings.HotkeyModifiers, _settings.HotkeyKey);
    public string MoveHotkeyText => GlobalHotkeyService.Describe(_settings.MoveHotkeyModifiers, _settings.MoveHotkeyKey);
    public string RecordHotkeyText => GlobalHotkeyService.Describe(_settings.RecordHotkeyModifiers, _settings.RecordHotkeyKey);

    public string HotkeyStatus
    {
        get => _hotkeyStatus;
        private set => SetProperty(ref _hotkeyStatus, value);
    }

    /// <summary>Profiles 1..9 and their hotkeys, for display.</summary>
    public string ProfileHotkeysText =>
        Profiles.Count == 0
            ? "Save a profile to get Ctrl+Alt+Shift+1…9."
            : string.Join("   ", Profiles.Take(9).Select((p, i) => $"{i + 1} = {p.Name}"));

    /// <summary>Called by the view once the UI thread is up.</summary>
    public void StartHotkey()
    {
        if (_hotkey is not null) return;
        _hotkey = new GlobalHotkeyService();
        _hotkey.Pressed += async (_, name) => await OnHotkeyAsync(name).ConfigureAwait(true);
        RegisterHotkeys();
    }

    private async Task OnHotkeyAsync(string name)
    {
        switch (name)
        {
            case "toggle":
                await ToggleAsync().ConfigureAwait(true);
                break;
            case "move":
                string msg = WindowMover.MoveForegroundToNext();
                if (msg.Length > 0) _main.AppendStatus("Move window: " + msg);
                break;
            case "record":
                await ToggleRecordingAsync().ConfigureAwait(true);
                break;
            default:
                if (name.StartsWith("profile", StringComparison.Ordinal)
                    && int.TryParse(name.AsSpan(7), out int n) && n >= 1 && n <= Profiles.Count)
                    await ApplyProfileByNameAsync(Profiles[n - 1].Name, prompt: false).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>Hotkey / tray toggle. A manual toggle overrides auto-switching until the trigger app exits.</summary>
    public Task ToggleAsync()
    {
        TakeOverFromAuto();
        return _main.ToggleSplitAsync();
    }

    /// <summary>Sets one of the configurable hotkeys: "toggle", "move" or "record".</summary>
    public void SetHotkey(string which, uint modifiers, uint virtualKey)
    {
        if (modifiers == 0 || virtualKey == 0) return;
        switch (which)
        {
            case "move":
                _settings.MoveHotkeyModifiers = modifiers;
                _settings.MoveHotkeyKey = virtualKey;
                OnPropertyChanged(nameof(MoveHotkeyText));
                break;
            case "record":
                _settings.RecordHotkeyModifiers = modifiers;
                _settings.RecordHotkeyKey = virtualKey;
                OnPropertyChanged(nameof(RecordHotkeyText));
                break;
            default:
                _settings.HotkeyModifiers = modifiers;
                _settings.HotkeyKey = virtualKey;
                OnPropertyChanged(nameof(HotkeyText));
                break;
        }
        Persist();
        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        if (_hotkey is null) return;
        _hotkey.UnregisterWhere(_ => true);
        if (!_settings.HotkeyEnabled)
        {
            HotkeyStatus = "Hotkeys off (the emergency hotkey Ctrl+Alt+Shift+End always works).";
            return;
        }

        var failed = new List<string>();
        if (!_hotkey.Register("toggle", _settings.HotkeyModifiers, _settings.HotkeyKey)) failed.Add(HotkeyText);
        if (!_hotkey.Register("move", _settings.MoveHotkeyModifiers, _settings.MoveHotkeyKey)) failed.Add(MoveHotkeyText);
        if (!_hotkey.Register("record", _settings.RecordHotkeyModifiers, _settings.RecordHotkeyKey)) failed.Add(RecordHotkeyText);
        if (_settings.ProfileHotkeysEnabled)
        {
            const uint mods = GlobalHotkeyService.MOD_CONTROL | GlobalHotkeyService.MOD_ALT | GlobalHotkeyService.MOD_SHIFT;
            for (int i = 1; i <= Math.Min(9, Profiles.Count); i++)
                if (!_hotkey.Register("profile" + i, mods, (uint)(0x30 + i)))
                    failed.Add("Ctrl+Alt+Shift+" + i);
        }

        HotkeyStatus = failed.Count == 0
            ? "All hotkeys active. Emergency off: Ctrl+Alt+Shift+End."
            : "Already used by another program: " + string.Join(", ", failed) + " — pick different combinations.";
    }

    // ------------------------------------------------------------------ window layouts per profile

    public RelayCommand SaveWindowsCommand { get; }
    public RelayCommand ClearWindowsCommand { get; }

    public string WindowsText => SelectedProfile is null
        ? string.Empty
        : SelectedProfile.Windows.Count == 0
            ? "No window layout saved for this profile."
            : $"{SelectedProfile.Windows.Count} window(s) are put back in place when this profile is applied.";

    private void SaveWindows()
    {
        if (SelectedProfile is null) return;
        try
        {
            SelectedProfile.Windows = WindowMover.CaptureLayout();
            Persist();
            _main.AppendStatus($"Profile '{SelectedProfile.Name}': saved the positions of {SelectedProfile.Windows.Count} window(s).");
        }
        catch (Exception ex)
        {
            _main.AppendStatus("Could not read the window positions: " + ex.Message);
        }
        OnPropertyChanged(nameof(WindowsText));
    }

    private void ClearWindows()
    {
        if (SelectedProfile is null) return;
        SelectedProfile.Windows = new();
        Persist();
        OnPropertyChanged(nameof(WindowsText));
    }

    /// <summary>Applies a profile, then puts its saved windows back once the displays have settled.</summary>
    private async Task<bool> ApplyProfileWithWindowsAsync(LayoutProfile profile, bool prompt)
    {
        bool ok = await _main.ApplyProfileAsync(profile, prompt).ConfigureAwait(true);
        if (!ok || profile.Windows.Count == 0) return ok;
        if (!await Task.Run(HidingStatus.IsSplitActive).ConfigureAwait(true)) return ok;

        await Task.Delay(1500).ConfigureAwait(true);
        try
        {
            int placed = WindowMover.RestoreLayout(profile.Windows);
            _main.AppendStatus($"Profile '{profile.Name}': moved {placed} of {profile.Windows.Count} saved window(s) into place.");
        }
        catch (Exception ex)
        {
            _main.AppendStatus("Could not restore the window layout: " + ex.Message);
        }
        return ok;
    }

    // ------------------------------------------------------------------ primary / recording mode

    public sealed record Choice(string Key, string Label);

    public IReadOnlyList<Choice> PrimaryChoices { get; } = new[]
    {
        new Choice("segment1", "Segment 1"),
        new Choice("segment2", "Segment 2"),
        new Choice("segment3", "Segment 3"),
        new Choice("segment4", "Segment 4"),
        new Choice("other", "Other monitor (e.g. the 27\")"),
    };

    public string PrimaryKey
    {
        get => _settings.Primary;
        set
        {
            if (string.IsNullOrEmpty(value) || _settings.Primary == value) return;
            _settings.Primary = value;
            OnPropertyChanged();
            Persist();
            _main.AppendStatus("Primary monitor choice saved — it takes effect the next time the split is applied.");
        }
    }

    public string RecordingPrimaryKey
    {
        get => string.IsNullOrEmpty(_settings.RecordingPrimary) ? "other" : _settings.RecordingPrimary;
        set
        {
            if (string.IsNullOrEmpty(value) || _settings.RecordingPrimary == value) return;
            _settings.RecordingPrimary = value;
            OnPropertyChanged();
            Persist();
        }
    }

    public bool RecordingMode => _settings.RecordingMode;

    public string RecordingText => _settings.RecordingMode
        ? $"Recording mode ON — notifications and pop-ups go to the chosen monitor. {RecordHotkeyText} to end."
        : $"Recording mode off. {RecordHotkeyText} moves notifications and pop-ups off the segments while you record.";

    public RelayCommand ToggleRecordingCommand { get; }

    public async Task ToggleRecordingAsync()
    {
        if (_main.IsBusy) return;
        _settings.RecordingMode = !_settings.RecordingMode;
        Persist();
        OnPropertyChanged(nameof(RecordingMode));
        OnPropertyChanged(nameof(RecordingText));
        _main.AppendStatus(_settings.RecordingMode ? "--- Recording mode on ---" : "--- Recording mode off ---");

        // Primary is chosen while applying, so re-apply the running split.
        if (await Task.Run(HidingStatus.IsSplitActive).ConfigureAwait(true))
            await _main.ReapplyLastAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ overlays

    public IReadOnlyList<int> SegmentNumbers { get; } = new[] { 1, 2, 3, 4 };

    public bool BorderEnabled
    {
        get => _settings.BorderEnabled;
        set { if (_settings.BorderEnabled == value) return; _settings.BorderEnabled = value; OverlaySettingChanged(); }
    }

    public int BorderSegment
    {
        get => _settings.BorderSegment;
        set { if (_settings.BorderSegment == value) return; _settings.BorderSegment = value; OverlaySettingChanged(); }
    }

    public bool DividerEnabled
    {
        get => _settings.DividerEnabled;
        set { if (_settings.DividerEnabled == value) return; _settings.DividerEnabled = value; OverlaySettingChanged(); }
    }

    private void OverlaySettingChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        OnPropertyChanged(name);
        Persist();
        RefreshOverlays();
    }

    public void RefreshOverlays()
    {
        try { _overlays.Refresh(_settings); }
        catch (Exception ex) { _main.AppendStatus("Overlay error: " + ex.Message); }
    }

    // ------------------------------------------------------------------ split ratio

    public bool SplitRatioAvailable => _main.IsTwoSegmentLayout;

    public double SplitRatioPercent
    {
        get => _splitRatio;
        set
        {
            value = Math.Clamp(Math.Round(value), 20, 80);
            if (!SetProperty(ref _splitRatio, value)) return;
            _main.SetSplitRatio(value / 100.0);
        }
    }

    // ------------------------------------------------------------------ auto-switch

    public bool AutoSwitchEnabled
    {
        get => _settings.AutoSwitchEnabled;
        set
        {
            if (_settings.AutoSwitchEnabled == value) return;
            _settings.AutoSwitchEnabled = value;
            OnPropertyChanged();
            Persist();
            if (value)
            {
                _watchTimer.Start();
            }
            else
            {
                _watchTimer.Stop();
                _autoActive = null;
                _autoRestoreTo = null;
                _autoSuppressed = null;
            }
        }
    }

    private void TakeOverFromAuto()
    {
        if (_autoActive is null) return;
        _autoSuppressed = _autoActive;
        _autoActive = null;
        _autoRestoreTo = null;
    }

    private async Task WatchTickAsync()
    {
        if (_watchTickRunning || _main.IsBusy) return;
        _watchTickRunning = true;
        try
        {
            HashSet<string> running = await Task.Run(RunningProcessNames).ConfigureAwait(true);

            LayoutProfile? target = Profiles.FirstOrDefault(
                p => p.TriggerApps.Any(t => running.Contains(NormalizeProcess(t))));

            if (_autoSuppressed is not null)
            {
                if (ReferenceEquals(target, _autoSuppressed)) return;
                _autoSuppressed = null;
            }

            if (target is not null && !ReferenceEquals(target, _autoActive))
            {
                if (_autoActive is null)
                {
                    // Remember what to go back to once the trigger app closes.
                    bool active = await Task.Run(HidingStatus.IsSplitActive).ConfigureAwait(true);
                    AppSettings? last = SettingsStore.Load();
                    _autoRestoreTo = active && last is not null && last.Segments.Count > 0
                        ? new LayoutProfile
                        {
                            Name = "previous split",
                            PhysicalDevice = last.PhysicalDevice,
                            PhysicalFriendlyName = last.PhysicalFriendlyName,
                            Layout = last.Layout,
                            Segments = last.Segments,
                        }
                        : null;
                }

                _autoActive = target;
                _main.AppendStatus($"Auto-switch: trigger app running → profile '{target.Name}'.");
                if (!await ApplyProfileWithWindowsAsync(target, prompt: false).ConfigureAwait(true))
                    _autoSuppressed = target; // don't retry every 2 s
                return;
            }

            if (target is null && _autoActive is not null)
            {
                LayoutProfile? restore = _autoRestoreTo;
                _autoActive = null;
                _autoRestoreTo = null;

                if (restore is not null)
                {
                    _main.AppendStatus("Auto-switch: trigger app closed → back to the previous split.");
                    await _main.ApplyProfileAsync(restore, promptToConfirm: false).ConfigureAwait(true);
                }
                else
                {
                    _main.AppendStatus("Auto-switch: trigger app closed → split off.");
                    await _main.RevertAsync().ConfigureAwait(true);
                }
            }
        }
        catch (Exception ex)
        {
            _main.AppendStatus("Auto-switch error: " + ex.Message);
        }
        finally
        {
            _watchTickRunning = false;
        }
    }

    private static HashSet<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process p in Process.GetProcesses())
        {
            try { names.Add(p.ProcessName); }
            catch { /* process exited meanwhile */ }
            finally { p.Dispose(); }
        }
        return names;
    }

    // ------------------------------------------------------------------ helpers

    private static string NormalizeProcess(string name)
    {
        string n = name.Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        return n;
    }

    private static List<string> ParseTriggers(string text)
        => text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(NormalizeProcess)
               .Where(s => s.Length > 0)
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .ToList();

    private static string FormatTriggers(LayoutProfile? p) => p is null ? string.Empty : string.Join(", ", p.TriggerApps);

    private void RaiseCommands()
    {
        SaveProfileCommand.RaiseCanExecuteChanged();
        LoadProfileCommand.RaiseCanExecuteChanged();
        ApplyProfileCommand.RaiseCanExecuteChanged();
        DeleteProfileCommand.RaiseCanExecuteChanged();
        SaveArrangementCommand.RaiseCanExecuteChanged();
        ResetArrangementCommand.RaiseCanExecuteChanged();
        SaveWindowsCommand.RaiseCanExecuteChanged();
        ClearWindowsCommand.RaiseCanExecuteChanged();
        ToggleRecordingCommand.RaiseCanExecuteChanged();
    }

    private void Persist()
    {
        try
        {
            _settings.Profiles = Profiles.ToList();
            // Homes are written by ApplyService directly; keep the on-disk copy.
            _settings.Homes = ExtrasStore.Load().Homes;
            ExtrasStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _main.AppendStatus("Could not save profiles: " + ex.Message);
        }
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(ProfileHotkeysText));
        RegisterHotkeys(); // profile hotkeys follow the list
    }

    public void Dispose()
    {
        _hotkey?.Dispose();
        _overlays.Dispose();
    }
}
