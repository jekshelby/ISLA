using System.Text.Json;
using System.IO;

namespace ISLA.Settings;

public sealed class AppSettings
{
    public string Theme { get; set; } = "dark";
    public string Position { get; set; } = "top-center";
    public bool CloseOnOutsideClick { get; set; } = true;
    public bool StartupWithWindows { get; set; }
    public bool MediaEnabled { get; set; } = true;
    public bool QuickSettingsEnabled { get; set; } = true;
    public bool PomodoroEnabled { get; set; } = true;
}

public sealed class AppSettingsService
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ISLA",
        "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public AppSettingsService()
    {
        Load();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                Save();
                return;
            }

            Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new AppSettings();
        }
        catch (JsonException)
        {
            Settings = new AppSettings();
        }
        catch (IOException)
        {
            Settings = new AppSettings();
        }
    }
}
