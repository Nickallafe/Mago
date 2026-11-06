using System.Windows;
using System.Windows.Controls;

namespace MagoDaPerformance;

public sealed class BiosPage : PageBase
{
    private readonly TextBlock _text;
    private Dictionary<string, string?>? _info;

    public BiosPage(MainWindow host) : base(host, "BIOS e firmware",
        "Identifica o firmware, a board, o Secure Boot e o TPM. Atualizacoes vem do fabricante.")
    {
        var card = Card("ANALISE", 720);
        var body = BodyOf(card);
        _text = Ui.Mono("A analisar BIOS/firmware...", 11, Ui.Txt);
        body.Children.Add(_text);
        body.Children.Add(Ui.Wrap(
            Ui.Btn("ANALISAR", () => Analyze(), primary: true),
            Ui.Btn("Suporte do fabricante", OpenVendor),
            Ui.Btn("Windows Update", () => Proc.OpenUri("ms-settings:windowsupdate"))));

        Add(Row(card));
        Analyze();
    }

    private static StackPanel BodyOf(Border card) => (StackPanel)((StackPanel)card.Child).Children[1];

    private void Analyze()
    {
        Host.RunTask(() =>
        {
            var info = SystemInfo.Bios();
            Host.Dispatcher.Invoke(() =>
            {
                _info = info;
                _text.Text = SystemInfo.BiosText(info);
            });
            return null;
        }, "A analisar BIOS...");
    }

    private void OpenVendor()
    {
        if (_info == null) { Host.Info("Corre primeiro a analise.", "BIOS"); return; }
        var url = SystemInfo.VendorSupportUrl(_info);
        Log.Write("A abrir suporte do fabricante: " + url);
        Proc.OpenUri(url);
    }
}
