using MagoDaPerformance;

namespace MagoTests;

/// <summary>
/// Matematica do contador de FPS, alimentada com linhas como as que o PresentMon escreve.
/// Sem processo, sem elevacao: e' so' o FrameStats (usado pelo FpsMonitor).
/// </summary>
public class FrameStatsTests
{
    // PresentMon 2.x: sem coluna FrameTime.
    private const string HeaderV2 =
        "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,AllowsTearing,PresentMode," +
        "TimeInSeconds,msInPresentAPI,msBetweenPresents,msBetweenDisplayChange,msUntilRenderComplete";

    // PresentMon 2.3.1+: FrameTime passou a existir.
    private const string HeaderV231 = HeaderV2 + ",FrameTime";

    private static string Row(string ms, string app = "game.exe") =>
        $"{app},1234,0x0,DXGI,0,0,0,Hardware Composed: Independent Flip,1.0,0.5,{ms},16.667,1.0";

    private static string RowV231(string ms, string frameTime) =>
        Row(ms) + "," + frameTime;

    private static FrameStats FeedAll(params string[] lines)
    {
        var stats = new FrameStats();
        foreach (var line in lines) stats.Feed(FpsMonitor.SplitCsv(line));
        return stats;
    }

    [Fact]
    public void Um_frame_de_16_667ms_da_60_fps()
    {
        var stats = FeedAll(HeaderV2, Row("16.667"));

        Assert.Equal(1, stats.Samples);
        Assert.Equal(60.0, stats.CurrentFps, 2);
        Assert.Equal(60.0, stats.AverageFps, 2);
    }

    [Fact]
    public void Usa_a_media_harmonica_dos_frame_times()
    {
        // 60 fps (16.667 ms) + 30 fps (33.333 ms): a media harmonica da' 40, nao 45.
        var stats = FeedAll(HeaderV2, Row("16.667"), Row("33.333"));

        Assert.Equal(40.0, stats.CurrentFps, 2);
        Assert.Equal(40.0, stats.AverageFps, 2);
    }

    [Fact]
    public void FrameTime_tem_prioridade_sobre_MsBetweenPresents()
    {
        // PresentMon 2.3.1+: as duas colunas existem e podem divergir (8 ms = 125 fps).
        var stats = FeedAll(HeaderV231, RowV231("50.0", "8.0"));

        Assert.Equal(125.0, stats.CurrentFps, 2);
    }

    [Fact]
    public void Sem_coluna_FrameTime_cai_para_MsBetweenPresents()
    {
        var stats = FeedAll(HeaderV2, Row("20.0"));

        Assert.Equal(50.0, stats.CurrentFps, 2);
    }

    [Fact]
    public void Linhas_antes_do_cabecalho_sao_ignoradas()
    {
        var stats = FeedAll(Row("16.667"), HeaderV2, Row("16.667"));

        Assert.Equal(1, stats.Samples);
    }

    [Fact]
    public void Cabecalho_repetido_nao_conta_como_frame()
    {
        var stats = FeedAll(HeaderV2, Row("16.667"), HeaderV2);

        Assert.Equal(1, stats.Samples);
    }

    [Theory]
    [InlineData("0.05")]     // abaixo do minimo: ruido
    [InlineData("2000")]     // acima do maximo: linha errada
    [InlineData("-16")]      // negativo
    [InlineData("abc")]      // sem numero
    [InlineData("")]         // celula vazia
    public void Valores_impossiveis_nao_entram_nas_contas(string ms)
    {
        var stats = FeedAll(HeaderV2, Row(ms));

        Assert.Equal(0, stats.Samples);
        Assert.Equal(0, stats.CurrentFps, 2);
    }

    [Fact]
    public void Cabecalho_sem_coluna_de_frame_time_nao_conta_nada()
    {
        var stats = FeedAll("Application,ProcessID", "game.exe,1234");

        Assert.Equal(0, stats.Samples);
    }

    [Fact]
    public void Janela_recente_segue_o_ritmo_e_a_media_guarda_o_historico()
    {
        var lines = new List<string> { HeaderV2 };
        for (var i = 0; i < FrameStats.WindowSize; i++) lines.Add(Row("10.0"));   // 100 fps
        for (var i = 0; i < FrameStats.WindowSize; i++) lines.Add(Row("20.0"));   //  50 fps

        var stats = FeedAll(lines.ToArray());

        Assert.Equal(2 * FrameStats.WindowSize, stats.Samples);
        Assert.Equal(50.0, stats.CurrentFps, 2);        // so' a janela recente
        Assert.Equal(66.67, stats.AverageFps, 2);       // 240 frames em 3.6 s
    }

    [Fact]
    public void Reset_zera_contas_e_mantem_o_cabecalho_da_sessao()
    {
        var stats = FeedAll(HeaderV2, Row("16.667"));
        stats.Reset();

        Assert.Equal(0, stats.Samples);
        Assert.Equal(0, stats.CurrentFps, 2);
        Assert.Equal(0, stats.AverageFps, 2);

        // A sessao do PresentMon continua a mesma: novas linhas nao repetem o cabecalho.
        Assert.True(stats.Feed(FpsMonitor.SplitCsv(Row("10.0"))));
        Assert.Equal(100.0, stats.CurrentFps, 2);
    }

    [Fact]
    public void ClearHeader_obriga_a_ler_um_cabecalho_novo()
    {
        var stats = FeedAll(HeaderV2, Row("16.667"));
        stats.ClearHeader();

        Assert.False(stats.Feed(FpsMonitor.SplitCsv(Row("16.667"))));
        Assert.Equal(1, stats.Samples);
    }

    [Fact]
    public void Nome_da_app_com_virgula_nao_desalinha_as_colunas()
    {
        var stats = FeedAll(HeaderV2, Row("16.667", "\"Game, The\""));

        Assert.Equal(60.0, stats.CurrentFps, 2);
    }
}

/// <summary>Metricas de cauda usadas na comparacao antes/depois.</summary>
public class FrameStatsPercentileTests
{
    private const string Header =
        "Application,ProcessID,MsBetweenPresents";

    private static FrameStats Feed(params string[] frameTimes)
    {
        var stats = new FrameStats();
        stats.Feed(FpsMonitor.SplitCsv(Header));
        foreach (var ms in frameTimes) stats.Feed(FpsMonitor.SplitCsv($"game.exe,1,{ms}"));
        return stats;
    }

    [Fact]
    public void Um_frame_lento_em_cem_aparece_no_1_por_cento_low()
    {
        var times = Enumerable.Repeat("10.0", 99).Append("50.0").ToArray();
        var stats = Feed(times);

        // O pior 1% de 100 frames e' o unico frame de 50 ms -> 20 fps.
        Assert.Equal(20.0, stats.OnePercentLowFps, 2);
        // Media harmonica de 100 frames em 1.04 s (99x10 ms + 1x50 ms) = 96.15 fps.
        Assert.Equal(96.15, stats.AverageFps, 2);
    }

    [Fact]
    public void Sem_amostras_as_metricas_sao_zero()
    {
        var stats = Feed();
        Assert.Equal(0, stats.OnePercentLowFps, 3);
        Assert.Equal(0, stats.P95FrameTime, 3);
    }

    [Fact]
    public void P95_fica_no_frametime_do_percentil_95()
    {
        // 95 frames rapidos + 5 lentos: 95% dos frames ficam em 10 ms (nearest-rank).
        var times = Enumerable.Repeat("10.0", 95).Concat(Enumerable.Repeat("20.0", 5)).ToArray();
        var stats = Feed(times);

        Assert.Equal(10.0, stats.P95FrameTime, 2);
        Assert.Equal(50.0, stats.OnePercentLowFps, 2);   // 1% de 100 = 1 frame: 20 ms -> 50 fps
    }

    [Fact]
    public void Comparacao_de_duas_capturas_mostra_a_diferenca()
    {
        var before = Feed(Enumerable.Repeat("16.667", 100).ToArray());
        var after = Feed(Enumerable.Repeat("10.0", 100).ToArray());

        Assert.True(after.AverageFps > before.AverageFps);
        Assert.Equal(100.0, after.AverageFps, 1);
        Assert.Equal(60.0, before.AverageFps, 1);
    }
}
