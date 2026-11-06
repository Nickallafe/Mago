using System.Text;

namespace MagoDaPerformance;

public static class Log
{
    private static readonly object Sync = new();
    public static event Action<string>? Line;

    public static void Write(string message, string level = "INFO")
    {
        var text = $"[{DateTime.Now:HH:mm:ss}] {level,-5} {message}";
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                File.AppendAllText(Paths.LogFile, text + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
        try { Line?.Invoke(text); } catch { }
    }
}
