using System.Globalization;

namespace MagoDaPerformance;

/// <summary>
/// Contabilidade dos frame times lidos do PresentMon: escolhe a coluna da versao certa,
/// descarta valores impossiveis e calcula FPS pela media harmonica da janela recente e do
/// total. Sem processos, threads nem IO, para poder ser testado com linhas CSV reais.
/// </summary>
internal sealed class FrameStats
{
    public const int WindowSize = 120;

    // Abaixo de 0.1 ms ou acima de 1000 ms nao e' um frame: e' ruido ou linha trocada.
    private const double MinFrameTimeMs = 0.1;
    private const double MaxFrameTimeMs = 1000;

    // PresentMon 2.3.1+ renomeou MsBetweenPresents; FrameTime tem prioridade quando existe.
    private static readonly string[] FrameTimeColumns = { "FrameTime", "MsBetweenPresents", "MsBetweenAppStart" };

    private readonly List<double> _window = new();
    private readonly List<double> _all = new();
    private Dictionary<string, int>? _header;

    public double CurrentFps { get; private set; }
    public double AverageFps { get; private set; }
    public int Samples { get; private set; }

    /// <summary>
    /// Processa uma linha CSV. Devolve true apenas quando entrou um frame novo; linhas de
    /// cabecalho, texto invalido e valores fora dos limites devolvem false.
    /// </summary>
    public bool Feed(IReadOnlyList<string> row)
    {
        if (row.Count == 0) return false;

        if (row[0].Trim().Equals("Application", StringComparison.OrdinalIgnoreCase))
        {
            _header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < row.Count; i++) _header[row[i].Trim()] = i;
            return false;
        }

        if (_header == null) return false;

        var index = -1;
        foreach (var column in FrameTimeColumns)
            if (_header.TryGetValue(column, out var col) && col < row.Count) { index = col; break; }
        if (index < 0) return false;

        if (!double.TryParse(row[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var frameTime)) return false;
        if (!double.IsFinite(frameTime) || frameTime < MinFrameTimeMs || frameTime > MaxFrameTimeMs) return false;

        _all.Add(frameTime);
        _window.Add(frameTime);
        if (_window.Count > WindowSize) _window.RemoveAt(0);

        Samples = _all.Count;
        CurrentFps = HarmonicMean(_window);
        AverageFps = HarmonicMean(_all);
        return true;
    }

    public void Reset()
    {
        _window.Clear();
        _all.Clear();
        CurrentFps = AverageFps = 0;
        Samples = 0;
    }

    /// <summary>Uma nova sessao do PresentMon volta a enviar o cabecalho.</summary>
    public void ClearHeader() => _header = null;

    /// <summary>n * 1000 / soma dos frame times: media harmonica, a forma correta de somar FPS.</summary>
    private static double HarmonicMean(List<double> frameTimes) =>
        frameTimes.Count == 0 ? 0 : frameTimes.Count * 1000.0 / frameTimes.Sum();
}
