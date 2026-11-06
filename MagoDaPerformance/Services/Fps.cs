using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

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

    /// <summary>Processos com janela visivel: os que fazem sentido medir.</summary>
    public static List<string> Targets()
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "dwm.exe", "explorer.exe", "MagoDaPerformance.exe", "presentmon.exe",
        };
        var own = Environment.ProcessId;
        var list = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == own || p.MainWindowHandle == IntPtr.Zero) continue;
                var name = p.ProcessName + ".exe";
                if (excluded.Contains(name)) continue;
                list.Add(name);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    public static string? AutoLocatePresentMon()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.Combine(Paths.DataDir, "presentmon") })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var found = Directory.EnumerateFiles(dir, "PresentMon*.exe", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f).FirstOrDefault();
                if (found != null) return found;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Descarrega o PresentMon mais recente do GitHub (exe direto ou zip).</summary>
    public static string DownloadPresentMon(Action<string>? progress = null)
    {
        progress?.Invoke("A consultar a ultima versao do PresentMon...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MagoDaPerformance/6.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var json = http.GetStringAsync("https://api.github.com/repos/GameTechDev/PresentMon/releases/latest")
            .GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var assets))
            throw new InvalidOperationException("resposta do GitHub sem lista de ficheiros");

        var files = assets.EnumerateArray()
            .Select(a => (
                Name: a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Url: a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : ""))
            .Where(f => f.Url.Length > 0)
            .ToList();

        var exe = files.FirstOrDefault(f =>
            f.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            f.Name.Contains("x64", StringComparison.OrdinalIgnoreCase));
        var zip = files.FirstOrDefault(f =>
            f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            f.Name.Contains("x64", StringComparison.OrdinalIgnoreCase));

        var dir = Path.Combine(Paths.DataDir, "presentmon");
        Directory.CreateDirectory(dir);

        if (!string.IsNullOrEmpty(exe.Name))
        {
            var dest = Path.Combine(dir, exe.Name);
            DownloadTo(http, exe.Url, dest, progress);
            return dest;
        }
        if (!string.IsNullOrEmpty(zip.Name))
        {
            var archive = Path.Combine(dir, zip.Name);
            DownloadTo(http, zip.Url, archive, progress);
            progress?.Invoke("A extrair...");
            ZipFile.ExtractToDirectory(archive, dir, true);
            try { File.Delete(archive); } catch { }
            var found = Directory.EnumerateFiles(dir, "PresentMon*.exe", SearchOption.AllDirectories)
                .OrderByDescending(f => f).FirstOrDefault();
            if (found != null) return found;
        }
        throw new InvalidOperationException("a ultima release nao traz um PresentMon x64");
    }

    private static void DownloadTo(HttpClient http, string url, string destination, Action<string>? progress)
    {
        using var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        using var source = response.Content.ReadAsStream();
        using var target = File.Create(destination);
        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            target.Write(buffer, 0, read);
            received += read;
            if (total > 0) progress?.Invoke($"A descarregar... {received * 100 / total}%");
        }
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

    internal static List<string> SplitCsv(string line)
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
