using System.Diagnostics;
using System.IO;
using System.Text;

namespace MagoDaPerformance;

/// <summary>Resumo de uma janela de amostragem: media da GPU e o pior nucleo de CPU.</summary>
public readonly record struct LoadSummary(int GpuAverage, int CpuMax, int Samples)
{
    public static readonly LoadSummary Empty = new(-1, -1, 0);
}

/// <summary>
/// Amostra GPU/CPU a ~1 Hz durante a medicao, num unico processo PowerShell. Uma leitura so' no
/// fim dava o retrato de um instante: o pico de CPU no menu do jogo ou a GPU a 100% apenas
/// durante o combate ficavam de fora. Aqui a conclusao usa a media da GPU e o pior nucleo de
/// toda a janela, que e' o que corresponde ao que o jogador sentiu.
/// </summary>
public sealed class LoadSampler : IDisposable
{
    private readonly object _lock = new();
    private readonly List<int> _gpu = new();
    private readonly List<int> _cpu = new();
    private Process? _process;

    public bool Running
    {
        get
        {
            lock (_lock)
            {
                try { return _process is { HasExited: false }; }
                catch { return false; }
            }
        }
    }

    /// <summary>Comeca uma janela nova: as amostras anteriores sao descartadas.</summary>
    public void Start(string processName)
    {
        Stop();
        lock (_lock)
        {
            _gpu.Clear();
            _cpu.Clear();
        }

        var bare = Path.GetFileNameWithoutExtension(processName ?? "");
        if (string.IsNullOrWhiteSpace(bare)) return;
        bare = bare.Replace("'", "''");   // o nome vem de uma janela: nao pode fechar a string do PS

        // -1 = contador indisponivel, que e' diferente de "0% de uso" na hora de concluir.
        var script =
            $"$p=Get-Process -Name '{bare}' -ErrorAction SilentlyContinue | Select-Object -First 1; " +
            "if(-not $p){'none'; exit}; " +
            "while($true){ " +
            "$gpu=-1; try { $g=(Get-Counter ('\\GPU Engine(pid_'+$p.Id+'*engtype_3D*)\\Utilization Percentage') " +
            "-ErrorAction Stop).CounterSamples | Measure-Object CookedValue -Maximum; $gpu=[int]$g.Maximum } catch {}; " +
            "$cpu=-1; try { $c=(Get-Counter '\\Processor(*)\\% Processor Time' -ErrorAction Stop).CounterSamples | " +
            "Where-Object {$_.InstanceName -ne '_total'} | Measure-Object CookedValue -Maximum; $cpu=[int]$c.Maximum } catch {}; " +
            "'GPU='+$gpu+' CPU='+$cpu; Start-Sleep -Milliseconds 1000 }";

        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -NonInteractive -Command \"" + script + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                if (!Bottleneck.TryParseSample(e.Data, out var gpu, out var cpu)) return;
                lock (_lock)
                {
                    if (gpu >= 0) _gpu.Add(gpu);
                    if (cpu >= 0) _cpu.Add(cpu);
                }
            };
            process.Start();
            process.BeginOutputReadLine();
            lock (_lock) _process = process;
        }
        catch (Exception ex)
        {
            Log.Write("Amostragem de carga: " + ex.Message, "AVISO");
        }
    }

    public void Stop()
    {
        Process? process;
        lock (_lock)
        {
            process = _process;
            _process = null;
        }
        if (process == null) return;

        try { if (!process.HasExited) process.Kill(true); } catch { }
        try { process.Dispose(); } catch { }
    }

    public LoadSummary Summary()
    {
        lock (_lock)
        {
            if (_gpu.Count == 0 && _cpu.Count == 0) return LoadSummary.Empty;

            var gpu = _gpu.Count > 0 ? (int)Math.Round(_gpu.Average()) : -1;
            var cpu = _cpu.Count > 0 ? _cpu.Max() : -1;
            return new LoadSummary(gpu, cpu, Math.Max(_gpu.Count, _cpu.Count));
        }
    }

    public void Dispose() => Stop();
}
