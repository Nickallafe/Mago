using MagoDaPerformance;

namespace MagoTests;

public class BottleneckTests
{
    [Theory]
    [InlineData(95, 40, LimitKind.Gpu)]
    [InlineData(88, 100, LimitKind.Gpu)]     // GPU saturada ganha, mesmo com CPU carregada
    [InlineData(75, 60, LimitKind.Gpu)]
    [InlineData(40, 95, LimitKind.Cpu)]      // um nucleo no maximo segura o frame
    [InlineData(-1, 95, LimitKind.Cpu)]      // sem contador de GPU, o pico de CPU chega
    [InlineData(30, 50, LimitKind.Capped)]   // nada saturado: V-Sync ou limite de FPS
    [InlineData(60, 80, LimitKind.Unknown)]  // zona cinzenta: nao vale a pena afirmar
    [InlineData(-1, -1, LimitKind.Unknown)]
    public void Classifica_o_gargalo_nas_bandas(int gpu, int cpu, LimitKind expected) =>
        Assert.Equal(expected, Bottleneck.Classify(gpu, cpu).Kind);

    [Fact]
    public void Descricao_diz_o_que_fazer_em_cada_caso()
    {
        Assert.Contains("GPU", Bottleneck.Classify(92, 30).Describe(90));
        Assert.Contains("CPU", Bottleneck.Classify(40, 95).Describe(90));
        Assert.Contains("V-Sync", Bottleneck.Classify(35, 45).Describe(60));
        Assert.Contains("Diagnostico", Bottleneck.Classify(-1, -1).Describe(0));
    }
}
