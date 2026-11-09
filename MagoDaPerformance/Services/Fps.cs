using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace MagoDaPerformance;

/// <summary>Le o stdout do PresentMon e calcula FPS por media harmonica dos frame times.</summary>
public sealed class FpsMonitor : IDisposable
{
    private const int PublishIntervalMs = 200;   // 5 atualizacoes/s chegam para a UI
    private const int WindowSize = 120;

    private readonly object _lock = new();
    private readonly List<double> _window = new();
    private readonly List<double> _all = new();
    private Process? _process;
    private Thread? _reader;
    private long _lastPublish;
    private volatile bool _running;
    private volatile string _lastError = "";
    private Dictionary<string, int>? _header;

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
            "system", "idle", "dwm.exe", "explorer.exe", "MagoDaPerformance.exe",
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
                if (p.ProcessName.StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase)) continue;
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
                    .OrderByDescending(SafeWriteTime)
                    .FirstOrDefault();
                if (found != null) return found;
            }
            catch { }
        }
        return null;
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
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
                .OrderByDescending(SafeWriteTime).FirstOrDefault();
            if (found != null) return found;
        }
        throw new InvalidOperationException("a ultima release nao traz um PresentMon x64");
    }

    private static void DownloadTo(HttpClient http, string url, string destination, Action<string>? progress)
    {
        using var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        // Escreve num .part: um download interrompido nao deixa um exe corrompido que o AutoLocate apanha.
        var partial = destination + ".part";
        try
        {
            using (var source = response.Content.ReadAsStream())
            using (var target = File.Create(partial))
            {
                var buffer = new byte[81920];
                long received = 0;
                var lastPercent = -1;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    target.Write(buffer, 0, read);
                    received += read;
                    if (total <= 0) continue;
                    var percent = (int)(received * 100 / total);
                    if (percent == lastPercent) continue;   // nao inunda o dispatcher da UI
                    lastPercent = percent;
                    progress?.Invoke($"A descarregar... {percent}%");
                }
            }
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(partial, destination);
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
    }

    public bool Start(string presentMonPath, string processName, out string error)
    {
        error = "";
        if (_running) { error = "ja existe uma medicao a decorrer"; return false; }
        if (string.IsNullOrWhiteSpace(presentMonPath)) presentMonPath = AutoLocatePresentMon() ?? "";
        if (presentMonPath.Length == 0 || !File.Exists(presentMonPath))
        {
            error = "PresentMon nao encontrado. Use o botao Baixar para o instalar automaticamente.";
            return false;
        }
        if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        { error = "o processo deve terminar em .exe"; return false; }

        try
        {
            lock (_lock)
            {
                _window.Clear();
                _all.Clear();
                _header = null;
            }
            CurrentFps = AverageFps = 0;
            Samples = 0;
            _lastPublish = 0;
            _lastError = "";

            // --stop_existing_session: sem isto uma captura anterior mal fechada deixa a sessao ETW
            // "PresentMon" viva e todas as tentativas seguintes falham ate reiniciar o PC.
            // --no_console_stats: o stdout fica so com o CSV que o parser entende.
            var psi = new ProcessStartInfo(presentMonPath,
                $"--process_name \"{processName}\" --output_stdout --v2_metrics --no_console_stats --stop_existing_session")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            _process = new Process { StartInfo = psi };
            _process.OutputDataReceived += (_, e) => { if (e.Data != null) HandleLine(e.Data); };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var text = e.Data.Trim();
                if (text.Length == 0) return;
                _lastError = text;
                Log.Write("PresentMon: " + text, "AVISO");
            };
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _running = true;
            _reader = new Thread(() => Watch(processName))
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
            _running = false;
            return false;
        }
    }

    /// <summary>Vigia a captura: termina quando o jogo fecha e explica porque o PresentMon saiu.</summary>
    private void Watch(string processName)
    {
        var seen = false;
        var missingChecks = 0;

        while (_running)
        {
            var process = _process;
            if (process == null) break;

            bool exited;
            try { exited = process.HasExited; } catch { exited = true; }
            if (exited) break;

            // Se o jogo foi fechado, nao vale a pena continuar a medir (o FPS ficava congelado).
            if (IsRunning(processName))
            {
                seen = true;
                missingChecks = 0;
            }
            else if (seen && ++missingChecks >= 3)
            {
                _running = false;
                try { if (!process.HasExited) process.Kill(true); } catch { }
                Log.Write($"Jogo {processName} fechado; captura FPS parada.");
                Status?.Invoke($"O jogo {processName} foi fechado; medicao parada.");
                return;
            }

            Thread.Sleep(1000);
        }

        if (!_running) return;   // paragem pedida pelo utilizador: sem mensagem extra
        _running = false;

        var code = -1;
        try { code = _process?.ExitCode ?? -1; } catch { }
        var detail = _lastError.Trim();

        if (Samples == 0)
        {
            Status?.Invoke(detail.Length > 0
                ? $"PresentMon terminou sem dados (codigo {code}): {Truncate(detail, 160)}"
                : $"PresentMon terminou sem quadros (codigo {code}). Confirme o nome do .exe e que o jogo esta aberto.");
        }
        else
        {
            Status?.Invoke($"Captura FPS terminada (codigo {code}).");
        }
        Log.Write($"PresentMon saiu (codigo {code}, {Samples} quadros)." + (detail.Length > 0 ? " " + detail : ""));
    }

    private static bool IsRunning(string processName)
    {
        var bare = Path.GetFileNameWithoutExtension(processName);
        try
        {
            var list = Process.GetProcessesByName(bare);
            try { return list.Length > 0; }
            finally { foreach (var p in list) p.Dispose(); }
        }
        catch { return true; }   // falha de permissao nao deve encerrar a captura
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "...";

    private void HandleLine(string line)
    {
        try
        {
            var row = SplitCsv(line);
            if (row.Count == 0) return;
            if (row[0].Trim().Equals("Application", StringComparison.OrdinalIgnoreCase))
            {
                var header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < row.Count; i++) header[row[i].Trim()] = i;
                _header = header;
                return;
            }
            if (_header == null) return;

            // PresentMon 2.3.1+ renomeou FrameTime; MsBetweenPresents existe em todas as versoes 2.x.
            var index = -1;
            foreach (var column in new[] { "FrameTime", "MsBetweenPresents", "MsBetweenAppStart" })
                if (_header.TryGetValue(column, out var col) && col < row.Count) { index = col; break; }
            if (index < 0) return;

            if (!double.TryParse(row[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var frameTime)) return;
            if (!double.IsFinite(frameTime) || frameTime < 0.1 || frameTime > 1000) return;

            double current, average;
            int samples;
            lock (_lock)
            {
                _all.Add(frameTime);
                _window.Add(frameTime);
                if (_window.Count > WindowSize) _window.RemoveAt(0);
                samples = _all.Count;
                Samples = samples;
                current = _window.Count * 1000.0 / _window.Sum();
                average = _all.Count * 1000.0 / _all.Sum();
                CurrentFps = current;
                AverageFps = average;
            }

            // Publicar a cada frame saturava a thread da UI (200+ Invoke/s). 5 Hz chega e sobra.
            var now = Environment.TickCount64;
            if (Samples > 1 && now - _lastPublish < PublishIntervalMs) return;
            _lastPublish = now;
            Updated?.Invoke(current, average, samples);
        }
        catch { }
    }

    internal static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
                continue;
            }
            if (ch == ',' && !quoted) { cells.Add(current.ToString()); current.Clear(); continue; }
            current.Append(ch);
        }
        cells.Add(current.ToString());
        return cells;
    }

    public void Stop()
    {
        var wasRunning = _running;
        _running = false;
        try
        {
            if (_process is { HasExited: false }) _process.Kill(true);
        }
        catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
        lock (_lock) _header = null;
        if (wasRunning) Log.Write("Captura FPS parada.");
    }

    public void Reset()
    {
        lock (_lock) { _window.Clear(); _all.Clear(); }
        CurrentFps = AverageFps = 0;
        Samples = 0;
        _lastPublish = 0;
        Updated?.Invoke(0, 0, 0);
    }

    public void Dispose()
    {
        Stop();
        _reader = null;
    }
}
