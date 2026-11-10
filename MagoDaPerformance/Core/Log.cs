using System.Text;

namespace MagoDaPerformance;

public static class Log
{
    private static readonly object Sync = new();
    public static event Action<string>? Line;

    /// <summary>Caminho alternativo do log; os testes apontam-no para uma pasta temporaria. Nulo = <see cref="Paths.LogFile"/>.</summary>
    public static string? FileOverride { get; set; }

    public static void Write(string message, string level = "INFO")
    {
        var text = $"[{DateTime.Now:HH:mm:ss}] {level,-5} {message}";
        lock (Sync)
        {
            try
            {
                var file = FileOverride ?? Paths.LogFile;
                Directory.CreateDirectory(Path.GetDirectoryName(file) ?? Paths.DataDir);
                File.AppendAllText(file, text + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
        try { Line?.Invoke(text); } catch { }
    }
}
