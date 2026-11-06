using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagoDaPerformance;

/// <summary>Preferencias do utilizador, guardadas em %LOCALAPPDATA%\MagoDaPerformance\mago_settings.json.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("advanced_mode")] public bool AdvancedMode { get; set; }
    [JsonPropertyName("presentmon_path")] public string? PresentMonPath { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(Paths.SettingsFile)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write("Preferencias ilegiveis: " + ex.Message, "AVISO");
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            var temp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, Paths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            Log.Write("Preferencias nao guardadas: " + ex.Message, "AVISO");
        }
    }
}
