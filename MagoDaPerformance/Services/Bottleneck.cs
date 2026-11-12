using System.Globalization;
using System.IO;

namespace MagoDaPerformance;

public enum LimitKind
{
    /// <summary>Nao foi possivel medir (sem jogo aberto ou contadores indisponiveis).</summary>
    Unknown,
    Gpu,
    Cpu,
    /// <summary>Nem CPU nem GPU saturadas: ha um limite de FPS, V-Sync ou um nucleo a segurar.</summary>
    Capped,
}

/// <summary>
/// Diz o que esta' a limitar o jogo durante a medicao. Usa os contadores do Windows (GPU Engine
/// 3D e o nucleo mais carregado) porque o frametime sozinho nao distingue "GPU no maximo" de
/// "CPU a segurar". Sem saturacao nenhuma o limite e' artificial: V-Sync, limite de FPS ou um
/// unico nucleo no maximo, que e' o caso tipico dos jogos competitivos.
/// </summary>
public readonly record struct Bottleneck(LimitKind Kind, int GpuPercent, int CpuMaxPercent)
{
    public static readonly Bottleneck None = new(LimitKind.Unknown, -1, -1);

    /// <summary>Banda de decisao, separada da leitura para poder ser testada sem contadores.</summary>
    public static Bottleneck Classify(int gpuPercent, int cpuMaxPercent)
    {
        if (gpuPercent < 0 && cpuMaxPercent < 0) return new(LimitKind.Unknown, gpuPercent, cpuMaxPercent);

        // GPU a 85%+ satura primeiro e nenhuma mudanca de CPU a ajuda.
        if (gpuPercent >= 85) return new(LimitKind.Gpu, gpuPercent, cpuMaxPercent);

        // Um nucleo no maximo segura o frame mesmo com a GPU folgada (jogos com 1 thread critica).
        if (cpuMaxPercent >= 90) return new(LimitKind.Cpu, gpuPercent, cpuMaxPercent);

        if (gpuPercent < 0) return new(LimitKind.Unknown, gpuPercent, cpuMaxPercent);
        if (gpuPercent >= 70) return new(LimitKind.Gpu, gpuPercent, cpuMaxPercent);

        if (gpuPercent <= 50 && cpuMaxPercent <= 70)
            return new(LimitKind.Capped, gpuPercent, cpuMaxPercent);

        return new(LimitKind.Unknown, gpuPercent, cpuMaxPercent);
    }

    public string Describe(double fps)
    {
        var rate = fps > 1 ? $"{fps:0} FPS" : "sem quadros";
        return Kind switch
        {
            LimitKind.Gpu =>
                $"Limitado pela GPU ({GpuPercent}% a {rate}): baixar a qualidade grafica sobe o FPS; mexer na CPU nao muda nada.",
            LimitKind.Cpu =>
                $"Limitado pela CPU (nucleo a {CpuMaxPercent}%, GPU {GpuPercent}%): feche apps de fundo; a placa grafica tem folga.",
            LimitKind.Capped =>
                $"Nem CPU nem GPU saturadas (GPU {GpuPercent}%, pico de CPU {CpuMaxPercent}%): ha um limite de FPS ou V-Sync. " +
                "Desligue o limite para ver o maximo.",
            _ => "Nao consegui ler a carga da GPU e da CPU; o Diagnostico mostra o que esta' a consumir.",
        };
    }

    /// <summary>Le os contadores do Windows uma vez. Corre num processo a parte (~1 s).</summary>
    public static Bottleneck Detect(string processName, int timeoutMs = 15000)
    {
        var bare = Path.GetFileNameWithoutExtension(processName ?? "");
        if (string.IsNullOrWhiteSpace(bare)) return None;
        bare = bare.Replace("'", "''");   // o nome vem de uma janela: nao pode fechar a string do PS

        // Contadores que nao existirem ficam a -1: e' diferente de "0% de uso".
        var script =
            $"$p=Get-Process -Name '{bare}' -ErrorAction SilentlyContinue | Select-Object -First 1; " +
            "if(-not $p){'none'; exit}; " +
            "$gpu=-1; try { $g=(Get-Counter ('\\GPU Engine(pid_'+$p.Id+'*engtype_3D*)\\Utilization Percentage') " +
            "-ErrorAction Stop).CounterSamples | Measure-Object CookedValue -Maximum; $gpu=[int]$g.Maximum } catch {}; " +
            "$cpu=-1; try { $c=(Get-Counter '\\Processor(*)\\% Processor Time' -ErrorAction Stop).CounterSamples | " +
            "Where-Object {$_.InstanceName -ne '_total'} | Measure-Object CookedValue -Maximum; $cpu=[int]$c.Maximum } catch {}; " +
            "'GPU='+$gpu+' CPU='+$cpu";

        var (ok, output) = Proc.Run("powershell", "-NoProfile -NonInteractive -Command \"" + script + "\"", timeoutMs);
        if (!ok || output.Contains("none", StringComparison.OrdinalIgnoreCase)) return None;

        return Classify(ReadValue(output, "GPU="), ReadValue(output, "CPU="));
    }

    private static int ReadValue(string text, string key)
    {
        var start = text.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return -1;

        start += key.Length;
        var end = start;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '-')) end++;
        return int.TryParse(text[start..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : -1;
    }
}
