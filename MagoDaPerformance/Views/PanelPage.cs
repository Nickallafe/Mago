using System.Windows;
using System.Windows.Controls;

namespace MagoDaPerformance;

public sealed class PanelPage : PageBase
{
    private readonly TextBlock _hw;
    private readonly TextBlock _fpsValue;
    private readonly TextBlock _fpsStatus;
    private readonly TextBox _presentMon;
    private readonly ListBox _targets;
    private readonly Button _fpsButton;
    private readonly CheckBox _overlay;
    private readonly TextBlock _stress;

    public PanelPage(MainWindow host) : base(host, "Painel do sistema",
        "Visao rapida do hardware, BIOS e atalhos de manutencao.")
    {
        // --- hardware
        var hwCard = Card("HARDWARE E SISTEMA", 480);
        var hwBody = BodyOf(hwCard);
        _hw = Ui.Mono("A ler hardware...", 11, Ui.Txt);
        hwBody.Children.Add(_hw);
        hwBody.Children.Add(Ui.Wrap(
            Ui.Btn("Atualizar hardware", () => Host.RunTask(() =>
            {
                var hw = SystemInfo.Detect();
                Host.Dispatcher.Invoke(() =>
                {
                    Host.GetType();
                    _hw.Text = hw.Text;
                    Host.Engine.GpuName = hw.Gpu;
                    Host.Engine.DiskTypes = hw.DiskTypes;
                });
                return null;
            }, "A identificar hardware...")),
            Ui.Btn("Abrir log", () => Proc.OpenUri(Paths.LogFile))));

        // --- FPS
        var fpsCard = Card("FPS (PRESENTMON)", 480);
        var fpsBody = BodyOf(fpsCard);
        _fpsValue = Ui.Mono("FPS --   media --", 26, Ui.Green);
        _fpsStatus = Ui.Lbl("Selecione o executavel do jogo e o PresentMon.", 11, Ui.Dim);

        _presentMon = Ui.Input("PresentMon-2.6.0-x64.exe", 430);
        _presentMon.Margin = new Thickness(0, 6, 8, 8);
        _presentMon.Text = FpsMonitor.AutoLocatePresentMon() ?? "";
        _targets = Ui.List(150);
        _fpsButton = Ui.Btn("INICIAR MEDICAO", ToggleFps);
        _overlay = new CheckBox
        {
            Content = "Overlay sempre no topo",
            Foreground = Ui.Br(Ui.Txt),
            Margin = new Thickness(8, 4, 0, 8),
        };
        _overlay.Checked += (_, _) => Host.ToggleOverlay(true);
        _overlay.Unchecked += (_, _) => Host.ToggleOverlay(false);

        fpsBody.Children.Add(_fpsValue);
        fpsBody.Children.Add(_fpsStatus);
        fpsBody.Children.Add(_presentMon);
        fpsBody.Children.Add(Ui.Wrap(
            Ui.Btn("Procurar PresentMon", BrowsePresentMon),
            Ui.Btn("Baixar PresentMon", () => Proc.OpenUri("https://github.com/GameTechDev/PresentMon/releases/latest")),
            Ui.Btn("Atualizar jogos", RefreshTargets)));
        fpsBody.Children.Add(_targets);
        fpsBody.Children.Add(Ui.Wrap(_fpsButton, _overlay, Ui.Btn("Reiniciar contagem", ResetFps)));

        // --- manutencao
        var quickCard = Card("MANUTENCAO RAPIDA", 480);
        var quickBody = BodyOf(quickCard);
        _stress = Ui.Lbl("", 11, Ui.Dim);
        quickBody.Children.Add(Ui.Wrap(
            Ui.Btn("Teste de stresse 15s", () => Host.RunTask(() => Host.Engine.StressTest(15), "Teste de stresse...")),
            Ui.Btn("Limpar DNS", () => Host.Engine.FlushDns()),
            Ui.Btn("Reiniciar Explorer", () => { if (Host.Confirm("Reiniciar o Explorador de Ficheiros?", "Explorer")) Host.Engine.RestartExplorer(); })));
        quickBody.Children.Add(_stress);

        Add(Row(hwCard, fpsCard));
        Add(Row(quickCard));

        Host.Fps.Updated += (current, average, samples) => Host.Dispatcher.Invoke(() =>
        {
            _fpsValue.Text = $"FPS {current:0}   media {average:0}   ({samples} quadros)";
            _fpsValue.Foreground = Ui.Br(current >= 60 ? Ui.Green : current >= 30 ? Ui.Gold : Ui.Red);
            Host.UpdateOverlay(current);
        });
        Host.Fps.Status += text => Host.Dispatcher.Invoke(() =>
        {
            _fpsStatus.Text = text;
            _fpsButton.Content = "INICIAR MEDICAO";
        });
    }

    private static StackPanel BodyOf(Border card) => (StackPanel)((StackPanel)card.Child).Children[1];

    public void SetHardware(HardwareInfo hw) => _hw.Text = hw.Text;

    private void BrowsePresentMon()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecionar PresentMon",
            Filter = "PresentMon (PresentMon*-x64.exe)|PresentMon*-x64.exe|Executavel (*.exe)|*.exe",
        };
        if (dialog.ShowDialog() == true)
        {
            _presentMon.Text = dialog.FileName;
            _fpsStatus.Text = "PresentMon selecionado.";
        }
    }

    private void RefreshTargets()
    {
        _fpsStatus.Text = "A procurar aplicacoes abertas...";
        System.Threading.Tasks.Task.Run(() =>
        {
            var list = FpsMonitor.Targets();
            Host.Dispatcher.Invoke(() =>
            {
                _targets.Items.Clear();
                foreach (var item in list) _targets.Items.Add(item);
                if (_targets.SelectedIndex < 0 && list.Count > 0) _targets.SelectedIndex = 0;
                _fpsStatus.Text = list.Count > 0
                    ? $"{list.Count} executaveis encontrados."
                    : "Nenhum executavel listado. Digite o nome .exe do jogo.";
            });
        });
    }

    private void ToggleFps()
    {
        if (Host.Fps.Running)
        {
            Host.Fps.Stop();
            _fpsButton.Content = "INICIAR MEDICAO";
            _fpsStatus.Text = "Medicao parada.";
            return;
        }
        var target = _targets.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(target))
        {
            _fpsStatus.Text = "Selecione um processo na lista (ou atualize os jogos abertos).";
            return;
        }
        if (Host.Fps.Start(_presentMon.Text.Trim().Trim('"'), target, out var error))
        {
            _fpsButton.Content = "PARAR MEDICAO";
            _fpsStatus.Text = $"A medir {target}. Se o jogo nao estiver aberto, aguarda.";
            if (_overlay.IsChecked == true) Host.ToggleOverlay(true);
        }
        else
        {
            _fpsStatus.Text = error;
        }
    }

    private void ResetFps()
    {
        Host.Fps.Reset();
        _fpsValue.Text = "FPS --   media --";
    }
}
