using System.Diagnostics;
using System.Globalization;

namespace MagoDaPerformance;

/// <summary>Le o stdout do PresentMon e calcula FPS por media harmonica dos frame times.</summary>
public sealed class FpsMonitor : IDisposable
{
    private readonly object _lock = new();
    private readonly List<double> _window = new();
    private readonly List<double> _all = new();
    private Process? _process;
    private Thread? _reader;
    private volatile bool _running;

    public double CurrentFps { get; private set; }
    public double AverageFps { get; private set; }
    public int Samples { get; private set; }
    public bool Running => _running;

    public event Action<double, double, int>? Updated;
    public event Action<string>? Status;

    public static List<string> Targets()
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "system", "idle", "dwm.exe" };
        return Process.GetProcesses()
            .Select(p => { try { return p.ProcessName + ".exe"; } catch { return ""; } })
            .Where(n => n.Length > 0 && !excluded.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? AutoLocatePresentMon()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            var found = Directory.EnumerateFiles(dir, "PresentMon*64.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f).FirstOrDefault();
            return found;
        }
        catch { return null; }
    }

    public bool Start(string presentMonPath, string processName, out string error)
    {
        error = "";
        if (_running) { error = "ja existe uma medicao a decorrer"; return false; }
        if (!File.Exists(presentMonPath)) { error = "PresentMon nao encontrado no caminho indicado"; return false; }
        if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        { error = "o processo deve terminar em .exe"; return false; }

        try
        {
            lock (_lock) { _window.Clear(); _all.Clear(); }
            CurrentFps = AverageFps = 0;
            Samples = 0;

            var psi = new ProcessStartInfo(presentMonPath,
                $"--process_name {processName} --output_stdout --v2_metrics")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            _process = new Process { StartInfo = psi };
            _process.OutputDataReceived += (_, e) => { if (e.Data != null) HandleLine(e.Data); };
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) Log.Write("PresentMon: " + e.Data, "AVISO"); };
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _running = true;
            _reader = new Thread(Watch)
            {
                IsBackground = true,
                Name = "mago-fps-watch",
            };
            _reader.Start();
            Log.Write($"Captura FPS iniciada para {processName}.");
            return true;
        }
        catch (Exception ex)
        {
            error = "nao foi possivel iniciar PresentMon: " + ex.Message;
            return false;
        }
    }

    private void Watch()
    {
        try { _process?.WaitForExit(); } catch { }
        if (!_running) return;
        _running = false;
        Status?.Invoke("A captura terminou (PresentMon saiu).");
    }

    private Dictionary<string, int>? _header;

    private void HandleLine(string line)
    {
        try
        {
            var row = SplitCsv(line);
            if (row.Count == 0) return;
            if (row[0].Trim().Equals("Application", StringComparison.OrdinalIgnoreCase))
            {
                _header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < row.Count; i++) _header[row[i].Trim()] = i;
                return;
            }
            if (_header == null) return;

            var index = _header.TryGetValue("FrameTime", out var ft) ? ft
                : _header.TryGetValue("msBetweenPresents", out var ms) ? ms : -1;
            if (index < 0 || index >= row.Count) return;
            if (!double.TryParse(row[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var frameTime)) return;
            if (!double.IsFinite(frameTime) || frameTime < 0.1 || frameTime > 1000) return;

            lock (_lock)
            {
                _all.Add(frameTime);
                _window.Add(frameTime);
                if (_window.Count > 120) _window.RemoveAt(0);
                Samples = _all.Count;

                CurrentFps = _window.Count * 1000.0 / _window.Sum();
                AverageFps = _all.Count * 1000.0 / _all.Sum();
            }
            Updated?.Invoke(CurrentFps, AverageFps, Samples);
        }
        catch { }
    }

    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (ch == ',' && !quoted) { cells.Add(current.ToString()); current.Clear(); continue; }
            current.Append(ch);
        }
        cells.Add(current.ToString());
        return cells;
    }

    public void Stop()
    {
        _running = false;
        try
        {
            if (_process is { HasExited: false }) _process.Kill(true);
        }
        catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
        Log.Write("Captura FPS parada.");
    }

    public void Reset()
    {
        lock (_lock) { _window.Clear(); _all.Clear(); }
        CurrentFps = AverageFps = 0;
        Samples = 0;
        Updated?.Invoke(0, 0, 0);
    }

    public void Dispose()
    {
        Stop();
        _reader = null;
    }
}
