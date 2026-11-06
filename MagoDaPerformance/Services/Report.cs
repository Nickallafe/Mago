using System.Text;
using System.Text.RegularExpressions;

namespace MagoDaPerformance;

/// <summary>Relatorio em Markdown para enviar a suporte ou guardar com o PC.</summary>
public static class Report
{
    public const string Version = "6.0";

    public static string Build(
        HardwareInfo? hardware,
        IReadOnlyList<DriverRow>? drivers,
        HealthReport? health,
        BackupStore store,
        IReadOnlyDictionary<string, string?>? bios = null,
        int logLines = 25)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Relatorio - Mago da Performance");
        sb.AppendLine();
        sb.AppendLine($"- **Data:** {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- **Versao:** {Version} (C# / WPF)");
        sb.AppendLine($"- **Windows:** {Environment.OSVersion.VersionString} (build {Environment.OSVersion.Version.Build})");
        sb.AppendLine($"- **Administrador:** {(Proc.IsAdmin() ? "sim" : "nao")}");
        sb.AppendLine();

        if (health != null)
        {
            sb.AppendLine($"## Saude: {health.Score}/100");
            sb.AppendLine();
            sb.AppendLine(health.Summary);
            sb.AppendLine();
            foreach (var item in health.Items)
                sb.AppendLine($"- {(item.Ok ? "OK" : "ATENCAO")} **{item.Label}**: {item.Detail}");
            sb.AppendLine();
        }

        sb.AppendLine("## Hardware");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(string.IsNullOrWhiteSpace(hardware?.Text) ? "hardware nao lido" : hardware!.Text);
        sb.AppendLine("```");
        sb.AppendLine();

        if (bios is { Count: > 0 })
        {
            sb.AppendLine("## BIOS e firmware");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(SystemInfo.BiosText(new Dictionary<string, string?>(bios, StringComparer.OrdinalIgnoreCase)));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("## Drivers");
        sb.AppendLine();
        if (drivers is { Count: > 0 })
        {
            sb.AppendLine("| Dispositivo | Versao | Data | Estado |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var d in drivers)
                sb.AppendLine($"| {Escape(d.Device)} | {d.Version} | {d.Date} | {(d.Old ? "antigo" : "recente")} |");
        }
        else
        {
            sb.AppendLine("_Sem dados de drivers._");
        }
        sb.AppendLine();

        sb.AppendLine("## Backup Mago");
        sb.AppendLine();
        sb.AppendLine($"- Ultimo: {store.Data.Created ?? "nunca"}");
        sb.AppendLine($"- Registo: {store.Data.Registry.Count} valores | Servicos: {store.Data.Services.Count} | BCD: {store.Data.Bcd.Count} | Arranque: {store.Data.Startup.Count}");
        sb.AppendLine($"- Plano de energia guardado: {store.Data.PowerScheme ?? "-"}");
        sb.AppendLine();

        var tail = ReadLogTail(logLines);
        if (tail.Count > 0)
        {
            sb.AppendLine("## Ultimas linhas do log");
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (var line in tail) sb.AppendLine(Redact(line));
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    private static List<string> ReadLogTail(int count)
    {
        try
        {
            if (!File.Exists(Paths.LogFile)) return new();
            var all = File.ReadAllLines(Paths.LogFile);
            return all.Length <= count ? all.ToList() : all[^count..].ToList();
        }
        catch { return new(); }
    }

    /// <summary>Remove nomes de utilizador dos caminhos antes de sair do PC.</summary>
    internal static string Redact(string line)
    {
        var user = Environment.UserName;
        if (!string.IsNullOrEmpty(user)) line = line.Replace(user, "<utilizador>", StringComparison.OrdinalIgnoreCase);
        return Regex.Replace(line, @"[A-Za-z]:\\Users\\[^\\\s]+", "<perfil>", RegexOptions.IgnoreCase);
    }

    private static string Escape(string text) => (text ?? "").Replace("|", "\\|");
}
