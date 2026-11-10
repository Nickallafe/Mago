using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagoDaPerformance;

/// <summary>Preferencias do utilizador, guardadas em %LOCALAPPDATA%\MagoDaPerformance\mago_settings.json.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("advanced_mode")] public bool AdvancedMode { get; set; }
    [JsonPropertyName("presentmon_path")] public string? PresentMonPath { get; set; }
    [JsonPropertyName("auto_update")] public bool AutoUpdate { get; set; } = true;

    // Ultima posicao do overlay de FPS, em DIPs. Nulo = ainda nunca foi posicionado.
    [JsonPropertyName("overlay_left")] public double? OverlayLeft { get; set; }
    [JsonPropertyName("overlay_top")] public double? OverlayTop { get; set; }

    // Aspeto do overlay de FPS: opacidade do fundo (0.15-1) e tamanho do texto em DIPs (12-48).
    // Os padroes ficam em constantes para o botao "repor overlay" usar os mesmos valores.
    public const double DefaultOverlayOpacity = 0.82;
    public const double DefaultOverlayFontSize = 22;

    [JsonPropertyName("overlay_opacity")] public double OverlayOpacity { get; set; } = DefaultOverlayOpacity;
    [JsonPropertyName("overlay_font_size")] public double OverlayFontSize { get; set; } = DefaultOverlayFontSize;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(Paths.SettingsFile))
            {
                // Cria o ficheiro com valores padrao para que a opcao de update fique disponivel imediatamente
                var defaults = new AppSettings();
                defaults.Save();
                return defaults;
            }
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write("Preferencias ilegiveis: " + ex.Message, "AVISO");
            // Em caso de erro, retorna defaults e tenta criar o ficheiro
            var defaults = new AppSettings();
            try { defaults.Save(); } catch { }
            return defaults;
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
