using System.IO;
using System.Text.Json;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

/// <summary>Reads/writes %APPDATA%\DesktopSplitter\extras.json.</summary>
public static class ExtrasStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(SettingsStore.Directory, "extras.json");

    public static ExtrasSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ExtrasSettings>(File.ReadAllText(FilePath), Options) ?? new ExtrasSettings();
        }
        catch
        {
            // A corrupt file must never stop the app from starting.
        }
        return new ExtrasSettings();
    }

    public static void Save(ExtrasSettings settings)
    {
        Directory.CreateDirectory(SettingsStore.Directory);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
