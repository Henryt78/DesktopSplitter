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

        _main.PropertyChanged += (_, e) =>
        {
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
        await _main.ApplyProfileAsync(p, prompt).ConfigureAwait(true);
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

    // ------------------------------------------------------------------ hotkey

    public bool HotkeyEnabled
    {
        get => _settings.HotkeyEnabled;
        set
        {
            if (_settings.HotkeyEnabled == value) return;
            _settings.HotkeyEnabled = value;
            OnPropertyChanged();
            Persist();
            RegisterHotkey();
        }
    }

    public string HotkeyText => GlobalHotkeyService.Describe(_settings.HotkeyModifiers, _settings.HotkeyKey);

    public string HotkeyStatus
    {
        get => _hotkeyStatus;
        private set => SetProperty(ref _hotkeyStatus, value);
    }

    /// <summary>Called by the view once the UI thread is up.</summary>
    public void StartHotkey()
    {
        _hotkey ??= new GlobalHotkeyService();
        _hotkey.Pressed += async (_, _) => await ToggleAsync().ConfigureAwait(true);
        RegisterHotkey();
    }

    /// <summary>Hotkey / tray toggle. A manual toggle overrides auto-switching until the trigger app exits.</summary>
    public Task ToggleAsync()
    {
        TakeOverFromAuto();
        return _main.ToggleSplitAsync();
    }

    public void SetHotkey(uint modifiers, uint virtualKey)
    {
        if (modifiers == 0 || virtualKey == 0) return;
        _settings.HotkeyModifiers = modifiers;
        _settings.HotkeyKey = virtualKey;
        OnPropertyChanged(nameof(HotkeyText));
        Persist();
        RegisterHotkey();
    }

    private void RegisterHotkey()
    {
        if (_hotkey is null) return;
        if (!_settings.HotkeyEnabled)
        {
            _hotkey.Unregister();
            HotkeyStatus = "Hotkey off.";
            return;
        }
        HotkeyStatus = _hotkey.Register(_settings.HotkeyModifiers, _settings.HotkeyKey)
            ? $"{HotkeyText} toggles the split on/off."
            : $"{HotkeyText} is already used by another program — pick a different combination.";
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
                if (!await _main.ApplyProfileAsync(target, promptToConfirm: false).ConfigureAwait(true))
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
    }

    public void Dispose() => _hotkey?.Dispose();
}
