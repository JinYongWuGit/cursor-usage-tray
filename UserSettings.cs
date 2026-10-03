using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace UsageTray;

public sealed class UserSettings
{
    public int RefreshIntervalMinutes { get; set; } = 1;

    public double WindowOpacity { get; set; } = 0.85;

    public bool StartWithWindows { get; set; } = false;

    public static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CursorUsageTray",
        "settings.json");

    public static UserSettings Load(string? customPath = null)
    {
        try
        {
            var path = customPath ?? SettingsFilePath;
            if (!File.Exists(path))
            {
                return new UserSettings();
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<UserSettings>(json);
            if (settings is null)
            {
                return new UserSettings();
            }

            settings.RefreshIntervalMinutes = Math.Max(1, Math.Min(1440, settings.RefreshIntervalMinutes));
            settings.WindowOpacity = Math.Max(0.2, Math.Min(1.0, settings.WindowOpacity));
            return settings;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UserSettings] Failed to load settings: {ex}");
            return new UserSettings();
        }
    }

    public void Save(string? customPath = null)
    {
        try
        {
            var path = customPath ?? SettingsFilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UserSettings] Failed to save settings: {ex}");
        }
    }
}
