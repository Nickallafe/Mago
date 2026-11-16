using MagoDaPerformance;

namespace MagoTests;

public class VerificationTests
{
    [Theory]
    [InlineData("2", "2", true)]
    [InlineData("0", "0", true)]
    [InlineData("2", "0", false)]
    [InlineData("4294967295", "-1", true)]   // DWORD maximo escrito e lido como -1
    [InlineData("4294967295", "4294967295", true)]
    [InlineData("2", "(ausente)", false)]
    [InlineData("x", "2", false)]
    public void Compara_dword_sem_sinal(string expected, string actual, bool ok) =>
        Assert.Equal(ok, TweakEngine.DwordMatches(expected, actual));

    [Fact]
    public void Relatorio_confirma_quando_tudo_pegou()
    {
        var report = new VerificationReport("GPU", new List<VerifiedItem>
        {
            new("HwSchMode", "2", "2", true),
            new("Servico DiagTrack", "desativado", "desativado", true),
        });

        Assert.Equal(0, report.Failed);
        Assert.Contains("\u2713", report.Describe());
        Assert.Contains("2 alteracao(oes) confirmadas", report.Describe());
    }

    [Fact]
    public void Relatorio_mostra_o_que_nao_pegou_e_o_que_esta_la()
    {
        var report = new VerificationReport("Windows Gaming", new List<VerifiedItem>
        {
            new("Win32PrioritySeparation", "26", "26", true),
            new("EnableVirtualizationBasedSecurity", "0", "1", false),
        });

        Assert.Equal(1, report.Confirmed);
        Assert.Equal(1, report.Failed);

        var text = report.Describe();
        Assert.Contains("\u26A0", text);
        Assert.Contains("1 confirmadas, 1 nao pegaram", text);
        Assert.Contains("EnableVirtualizationBasedSecurity", text);
        Assert.Contains("esperado 0, esta' 1", text);
    }

    [Fact]
    public void Modulo_sem_verificacoes_nao_inventa_resultado() =>
        Assert.Contains("nada para confirmar", new VerificationReport("Rede", Array.Empty<VerifiedItem>()).Describe());
}
