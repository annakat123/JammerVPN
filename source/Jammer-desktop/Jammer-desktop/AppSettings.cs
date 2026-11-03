using System;
using System.IO;
using System.Text.Json;
using Serilog;

namespace Jammer_desktop;

public sealed class AppSettings
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JammerVPN");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    public static AppSettings Current { get; } = Load();

    public string SelectedServer { get; set; } = "";
    public string Language { get; set; } = "ru";
    public bool LoggingEnabled { get; set; } = true;

    private static AppSettings Load()
    {
        try
        {
            var settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) : null;
            if (settings is null)
            {
                var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jammer", "server-ip.txt");
                return new AppSettings { SelectedServer = File.Exists(legacy) ? File.ReadAllText(legacy) : "" };
            }
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning(ex, "Could not load settings");
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, FilePath, true);
    }
}
