using MagoDaPerformance;

namespace MagoTests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+F", true)]
    [InlineData("ctrl+shift+f12", true)]
    [InlineData("Win+Space", true)]
    [InlineData("Alt+9", true)]
    [InlineData("Ctrl+PageDown", true)]
    [InlineData("F", false)]              // sem modificador, engolia a tecla em todos os programas
    [InlineData("Ctrl+Alt", false)]       // sem tecla principal
    [InlineData("Ctrl+Alt+F+G", false)]   // duas teclas principais
    [InlineData("Ctrl+OemComma", false)]  // tecla que o registo nao suporta
    [InlineData("Ctrl+F25", false)]
    [InlineData("", false)]
    public void Aceita_so_combinacoes_validas(string text, bool valid) =>
        Assert.Equal(valid, Hotkey.TryParse(text, out _));

    [Fact]
    public void Descreve_o_atalho_sem_repetir_modificadores()
    {
        Assert.True(Hotkey.TryParse("shift+ctrl+f1", out var hotkey));
        Assert.Equal("Ctrl+Shift+F1", hotkey.Describe());
        Assert.Equal("Win+Left", (hotkey with { Modifiers = Hotkey.Win, Key = 0x25 }).Describe());
    }

    [Fact]
    public void Atalho_de_fabrica_le_se()
    {
        Assert.True(Hotkey.TryParse(Hotkey.DefaultText, out var hotkey));
        Assert.Equal("Ctrl+Alt+F", hotkey.Describe());
    }
}
