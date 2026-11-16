using MagoDaPerformance;

namespace MagoTests;

public class OverlayCompatTests
{
    [Theory]
    [InlineData("~ DISABLEDXMAXIMIZEDWINDOWEDMODE", true)]
    [InlineData("~HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE", true)]
    [InlineData("~", false)]
    [InlineData("~ HIGHDPIAWARE", false)]
    [InlineData(null, false)]
    public void Reconhece_o_flag_que_desliga_as_otimizacoes(string? layer, bool expected) =>
        Assert.Equal(expected, OverlayCompat.DisablesFullscreenOptimizations(layer));

    [Theory]
    [InlineData("~ DISABLEDXMAXIMIZEDWINDOWEDMODE", "~")]
    [InlineData("~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE", "~ HIGHDPIAWARE")]
    [InlineData("~", "~")]
    [InlineData(null, "~")]
    [InlineData("DISABLEDXMAXIMIZEDWINDOWEDMODE", "~")]
    public void Liga_as_otimizacoes_sem_perder_os_outros_flags(string? layer, string expected) =>
        Assert.Equal(expected, OverlayCompat.EnableFlags(layer));
}
