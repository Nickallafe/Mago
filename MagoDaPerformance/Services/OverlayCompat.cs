using System.Diagnostics;
using System.IO;

namespace MagoDaPerformance;

/// <summary>
/// "Otimizacoes de ecra inteiro" do Windows (Fullscreen Optimizations) para um .exe. Com elas
/// desligadas o jogo apresenta-se em ecra inteiro exclusivo e nenhuma janela normal fica por
/// cima - nem o overlay. Com elas ligadas o Windows apresenta o jogo por flip model (como "sem
/// bordas") e o overlay consegue aparecer. E' a unica alavanca sem injetar codigo no jogo.
/// </summary>
public static class OverlayCompat
{
    private const string LayersKey = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string DisabledFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    public enum State
    {
        /// <summary>Sem entrada no registo: o Windows usa o padrao, que e' ligado.</summary>
        Default,
        Enabled,
        Disabled,
    }

    /// <summary>Caminho do .exe de um processo aberto; null se nao estiver a correr ou nao der para ler.</summary>
    public static string? ExePath(string processName)
    {
        var bare = Path.GetFileNameWithoutExtension(processName ?? "");
        if (string.IsNullOrWhiteSpace(bare)) return null;

        try
        {
            using var process = Process.GetProcessesByName(bare).FirstOrDefault();
            if (process == null) return null;

            var path = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;   // processo protegido (anticheat) ou sem permissao
        }
    }

    public static State Read(string exePath)
    {
        var value = Reg.ReadString(LayersKey, exePath);
        if (value == null) return State.Default;
        return DisablesFullscreenOptimizations(value) ? State.Disabled : State.Enabled;
    }

    internal static bool DisablesFullscreenOptimizations(string? layerValue) =>
        layerValue != null && layerValue.Contains(DisabledFlag, StringComparison.OrdinalIgnoreCase);

    /// <summary>Tira o flag que desliga as otimizacoes, preservando os outros.</summary>
    internal static string EnableFlags(string? layerValue)
    {
        var flags = (layerValue ?? "~")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(flag => !flag.Equals(DisabledFlag, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (flags.Count == 0) flags.Add("~");
        return string.Join(" ", flags);
    }

    /// <summary>
    /// Liga as otimizacoes para o .exe. <paramref name="previous"/> devolve o valor anterior
    /// (null quando nao havia entrada) para o botao de reverter poder repor tudo.
    /// </summary>
    public static bool Enable(string exePath, out string? previous, out string error)
    {
        previous = Reg.ReadString(LayersKey, exePath);
        error = "";

        if (!Reg.Write(LayersKey, exePath, "REG_SZ", EnableFlags(previous)))
        {
            error = "Nao consegui escrever as otimizacoes de ecra inteiro no registo.";
            return false;
        }
        return true;
    }

    /// <summary>Repoe o valor anterior; null apaga a entrada (volta ao padrao do Windows).</summary>
    public static bool Restore(string exePath, string? previous)
    {
        try
        {
            return previous == null
                ? Reg.DeleteValue(LayersKey, exePath)
                : Reg.Write(LayersKey, exePath, "REG_SZ", previous);
        }
        catch
        {
            return false;
        }
    }
}
