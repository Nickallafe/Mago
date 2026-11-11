using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MagoDaPerformance;

/// <summary>
/// FPS, overlay e comparacao antes/depois. Estava tudo dentro do painel inicial, ao lado
/// do hardware, e quem abria o app nao percebia que aquilo era uma ferramenta de medicao.
/// </summary>
public sealed class GamesPage : PageBase
{
    private readonly TextBlock _fpsValue;
    private readonly TextBlock _fpsStatus;
    private readonly TextBox _presentMon;
    private readonly ListBox _targets;
    private readonly Button _fpsButton;
    private readonly TextBlock _measureStatus;

    private bool _downloading;
    private bool _measuring;

    // Comparacao antes/depois: mesma duracao nas duas capturas.
    private const int MeasureSeconds = 20;
    private Measurement? _baseline;

    private readonly record struct Measurement(double Average, double Low1, double P95, int Samples);

    public GamesPage(MainWindow host) : base(host, "Jogos e FPS",
        "Mede o FPS do jogo aberto, liga o overlay no ecra e compara o antes/depois das otimizacoes.")
    {
        // ---------- medicao de fps ----------
        var fpsCard = Card("FPS DO JOGO", 480);
        var fpsBody = BodyOf(fpsCard);
        fpsBody.Children.Add(Ui.Lbl(
            "Abra o jogo, escolha-o na lista e inicie a medicao. O PresentMon le os quadros do jogo.",
            11, Ui.Dim));

        _fpsValue = Ui.Mono("FPS --   media --", 24, Ui.Green);
        _fpsStatus = Ui.Lbl("Escolha o jogo na lista e confirme o PresentMon.", 11, Ui.Dim);

        _presentMon = Ui.Input("PresentMon-2.6.0-x64.exe", 430);
        _presentMon.Margin = new Thickness(0, 6, 8, 8);
        _presentMon.Text = FpsMonitor.AutoLocatePresentMon() ?? "";
        _targets = Ui.List(120);
        _fpsButton = Ui.Btn("Iniciar medicao", ToggleFps, primary: true);

        fpsBody.Children.Add(_fpsValue);
        fpsBody.Children.Add(_fpsStatus);
        fpsBody.Children.Add(_presentMon);
        fpsBody.Children.Add(Ui.Wrap(
            Ui.Btn("Localizar PresentMon", BrowsePresentMon),
            Ui.Btn("Baixar PresentMon", DownloadPresentMon),
            Ui.Btn("Listar jogos abertos", RefreshTargets)));
        fpsBody.Children.Add(_targets);
        fpsBody.Children.Add(Ui.Wrap(_fpsButton, Ui.Btn("Reiniciar contagem", ResetFps)));

        // ---------- overlay ----------
        var overlayCard = Card("OVERLAY NO ECRA", 480);
        var overlayBody = BodyOf(overlayCard);
        overlayBody.Children.Add(Ui.Lbl(
            "Janela pequena sempre por cima com o FPS atual. Arraste-a para o canto que quiser.",
            11, Ui.Dim));
        // O erro mais comum de quem mede FPS: o jogo em ecra inteiro exclusivo, onde o
        // Windows esconde qualquer sobreposicao. Dizer isto evita achar que o overlay falhou.
        overlayBody.Children.Add(Ui.Lbl(
            "Em \"tela cheia exclusiva\" nada do Windows fica por cima do jogo. Se o overlay nao aparecer, " +
            "nas opcoes de video do jogo escolha \"tela cheia sem bordas\" (ou janela): o FPS continua a ser medido.",
            11, Ui.Gold));

        var overlayOn = new CheckBox
        {
            Content = "Mostrar o overlay",
            IsChecked = Host.Overlay != null,
            Foreground = Ui.Br(Ui.Txt),
            Margin = new Thickness(0, 6, 0, 10),
            VerticalAlignment = VerticalAlignment.Center,
        };
        overlayOn.Checked += (_, _) => Host.ToggleOverlay(true);
        overlayOn.Unchecked += (_, _) => Host.ToggleOverlay(false);
        overlayBody.Children.Add(overlayOn);

        var opacityLabel = Ui.Lbl("", 11, Ui.Dim);
        var opacity = OverlaySlider(
            FpsOverlay.MinOpacity * 100, 100, 5,
            Math.Clamp(Host.Settings.OverlayOpacity, FpsOverlay.MinOpacity, 1) * 100);
        void ShowOpacity() => opacityLabel.Text = $"Opacidade do fundo: {opacity.Value:0}%";
        ShowOpacity();
        opacity.ValueChanged += (_, _) =>
        {
            ShowOpacity();
            // Durante o arrasto so' aplica; grava no fim (ou logo, se veio do teclado).
            Host.SetOverlayOpacity(opacity.Value / 100, save: !opacity.IsMouseCaptureWithin);
        };
        opacity.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => Host.Settings.Save()));

        var sizeLabel = Ui.Lbl("", 11, Ui.Dim);
        var size = OverlaySlider(
            FpsOverlay.MinFontSize, FpsOverlay.MaxFontSize, 1,
            Math.Clamp(Host.Settings.OverlayFontSize, FpsOverlay.MinFontSize, FpsOverlay.MaxFontSize));
        void ShowSize() => sizeLabel.Text = $"Tamanho do texto: {size.Value:0} px";
        ShowSize();
        size.ValueChanged += (_, _) =>
        {
            ShowSize();
            Host.SetOverlayFontSize(size.Value, save: !size.IsMouseCaptureWithin);
        };
        size.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => Host.Settings.Save()));

        // Os sliders refletem o reset (ValueChanged aplica e grava por cima do padrao).
        void ResetOverlay()
        {
            Host.ResetOverlay();
            opacity.Value = AppSettings.DefaultOverlayOpacity * 100;
            size.Value = AppSettings.DefaultOverlayFontSize;
        }

        overlayBody.Children.Add(opacityLabel);
        overlayBody.Children.Add(opacity);
        overlayBody.Children.Add(sizeLabel);
        overlayBody.Children.Add(size);
        overlayBody.Children.Add(Ui.Wrap(Ui.Btn("Repor overlay ao padrao", ResetOverlay)));

        // ---------- medir antes / depois ----------
        var measureCard = Card("MEDIR ANTES / DEPOIS", 970);
        var measureBody = BodyOf(measureCard);
        measureBody.Children.Add(Ui.Lbl(
            $"Mede {MeasureSeconds} s do jogo aberto (FPS medio, 1% low e p95 do frametime). " +
            "Mede a base, aplica as otimizacoes em Otimizar e mede de novo: e' a unica forma de " +
            "saber se o ganho foi real.", 11, Ui.Dim));
        _measureStatus = Ui.Lbl("Escolha o jogo na lista e indique o PresentMon acima.", 11, Ui.Dim);
        measureBody.Children.Add(Ui.Wrap(
            Ui.Btn("1. Medir base", () => Measure(isBaseline: true), primary: true),
            Ui.Btn("2. Medir depois e comparar", () => Measure(isBaseline: false)),
            Ui.Btn("Limpar", ClearMeasurement)));
        measureBody.Children.Add(_measureStatus);

        Add(Row(fpsCard, overlayCard));
        Add(Row(measureCard));

        // BeginInvoke: o leitor do PresentMon nunca pode ficar bloqueado a espera da UI.
        Host.Fps.Updated += (current, average, samples) =>
        {
            if (Host.Dispatcher.HasShutdownStarted) return;
            Host.Dispatcher.BeginInvoke(new Action(() =>
            {
                _fpsValue.Text = $"FPS {current:0}   media {average:0}   ({samples} quadros)";
                _fpsValue.Foreground = Ui.Br(current >= 60 ? Ui.Green : current >= 30 ? Ui.Gold : Ui.Red);
                Host.UpdateOverlay(current);
            }));
        };
        Host.Fps.Status += text =>
        {
            if (Host.Dispatcher.HasShutdownStarted) return;
            Host.Dispatcher.BeginInvoke(new Action(() =>
            {
                _fpsStatus.Text = text;
                _fpsButton.Content = "Iniciar medicao";
            }));
        };
    }

    private static StackPanel BodyOf(Border card) => (StackPanel)((StackPanel)card.Child).Children[1];

    private static Slider OverlaySlider(double min, double max, double step, double value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = value,
        TickFrequency = step,
        IsSnapToTickEnabled = true,
        MinWidth = 220,
        Style = Ui.DarkSlider,
        Margin = new Thickness(0, 2, 0, 10),
    };

    /// <summary>Reaproveita um PresentMon ja descarregado quando a pagina volta a aparecer.</summary>
    public override void OnShown()
    {
        if (string.IsNullOrWhiteSpace(_presentMon.Text))
            _presentMon.Text = FpsMonitor.AutoLocatePresentMon() ?? "";
    }

    /// <summary>Descarrega e instala o PresentMon sem sair da app.</summary>
    private void DownloadPresentMon()
    {
        if (_downloading) return;
        _downloading = true;
        _fpsStatus.Foreground = Ui.Br(Ui.Gold);
        _fpsStatus.Text = "A descarregar o PresentMon do GitHub...";
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var path = FpsMonitor.DownloadPresentMon(progress =>
                {
                    if (Host.Dispatcher.HasShutdownStarted) return;
                    Host.Dispatcher.BeginInvoke(new Action(() => _fpsStatus.Text = progress));
                });
                if (Host.Dispatcher.HasShutdownStarted) return;
                Host.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _presentMon.Text = path;
                    _fpsStatus.Foreground = Ui.Br(Ui.Green);
                    _fpsStatus.Text = "PresentMon pronto: " + System.IO.Path.GetFileName(path);
                }));
            }
            catch (Exception ex)
            {
                Log.Write("Download PresentMon: " + ex.Message, "ERRO");
                if (Host.Dispatcher.HasShutdownStarted) return;
                Host.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _fpsStatus.Foreground = Ui.Br(Ui.Gold);
                    _fpsStatus.Text = "Falha ao baixar o PresentMon: " + ex.Message;
                }));
            }
            finally
            {
                _downloading = false;
            }
        });
    }

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
                    : "Nenhum executavel listado. Abra o jogo e tente de novo.";
            });
        });
    }

    private void ToggleFps()
    {
        if (Host.Fps.Running)
        {
            Host.Fps.Stop();
            _fpsButton.Content = "Iniciar medicao";
            _fpsStatus.Text = "Medicao parada.";
            return;
        }
        if (_targets.SelectedItem is not string target || string.IsNullOrWhiteSpace(target))
        {
            _fpsStatus.Text = "Escolha um jogo na lista (use \"Listar jogos abertos\").";
            return;
        }
        var presentMon = _presentMon.Text.Trim().Trim('"');
        if (presentMon.Length == 0) presentMon = FpsMonitor.AutoLocatePresentMon() ?? "";
        if (presentMon.Length == 0)
        {
            _fpsStatus.Foreground = Ui.Br(Ui.Gold);
            _fpsStatus.Text = "PresentMon nao encontrado. Clique em Baixar.";
            return;
        }
        _presentMon.Text = presentMon;
        if (Host.Fps.Start(presentMon, target, out var error))
        {
            _fpsButton.Content = "Parar medicao";
            _fpsStatus.Text = $"A medir {target}. Se o jogo nao estiver aberto, aguarde.";
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

    // ---------- medir antes / depois ----------

    private void ClearMeasurement()
    {
        _baseline = null;
        _measureStatus.Text = "Medicao limpa. Comece pela base.";
        _measureStatus.Foreground = Ui.Br(Ui.Dim);
    }

    private async void Measure(bool isBaseline)
    {
        if (_measuring) return;
        if (Host.Fps.Running)
        {
            Host.Info("Ja existe uma medicao a decorrer. Pare-a primeiro.", "Medir");
            return;
        }
        if (!isBaseline && _baseline == null)
        {
            Host.Info("Meca primeiro a base: sem ela nao há nada para comparar.", "Medir");
            return;
        }
        if (_targets.SelectedItem is not string target || string.IsNullOrWhiteSpace(target))
        {
            Host.Info("Escolha o jogo na lista (use \"Listar jogos abertos\").", "Medir");
            return;
        }

        var presentMon = _presentMon.Text.Trim().Trim('"');
        if (presentMon.Length == 0) presentMon = FpsMonitor.AutoLocatePresentMon() ?? "";
        if (presentMon.Length == 0)
        {
            Host.Info("Indique o PresentMon (Localizar ou Baixar).", "Medir");
            return;
        }
        _presentMon.Text = presentMon;

        _measuring = true;
        _measureStatus.Foreground = Ui.Br(Ui.Txt);
        try
        {
            Host.Fps.Reset();
            if (!Host.Fps.Start(presentMon, target, out var error))
            {
                _measureStatus.Foreground = Ui.Br(Ui.Red);
                _measureStatus.Text = error;
                return;
            }

            _fpsButton.Content = "A medir...";
            _measureStatus.Text = $"A medir {target} durante {MeasureSeconds} s. Deixe o jogo a correr.";
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(MeasureSeconds));
            Host.Fps.Stop();
            _fpsButton.Content = "Iniciar medicao";

            var samples = Host.Fps.Samples;
            if (samples < 60)
            {
                _measureStatus.Foreground = Ui.Br(Ui.Gold);
                _measureStatus.Text = $"So {samples} quadros em {MeasureSeconds} s: o jogo estava a correr? " +
                                      "Sem dados suficientes para comparar.";
                return;
            }

            var current = new Measurement(Host.Fps.AverageFps, Host.Fps.OnePercentLowFps, Host.Fps.P95FrameTime, samples);
            if (isBaseline)
            {
                _baseline = current;
                _measureStatus.Foreground = Ui.Br(Ui.Txt);
                _measureStatus.Text = "Base: " + Describe(current) +
                                      "\nAplique as otimizacoes (Otimizar) e clique em \"2. Medir depois\".";
            }
            else
            {
                _measureStatus.Foreground = Ui.Br(Ui.Txt);
                _measureStatus.Text = Compare(_baseline!.Value, current);
            }
        }
        finally
        {
            _measuring = false;
        }
    }

    private static string Describe(Measurement m) =>
        $"{m.Average:0.0} fps media | {m.Low1:0.0} fps no 1% low | p95 {m.P95:0.00} ms | {m.Samples} quadros";

    private static string Compare(Measurement before, Measurement after)
    {
        var avg = PercentChange(before.Average, after.Average);
        var low = PercentChange(before.Low1, after.Low1);
        var p95 = PercentChange(before.P95, after.P95);

        // So conta como ganho se a media subir e o 1% low nao piorar: media alta com
        // stutter pior nao é progresso.
        var verdict = avg >= 3 && low >= -1
            ? "GANHO"
            : avg <= -3 || low <= -5
                ? "PIOROU"
                : "DENTRO DO RUIDO";

        return $"VEREDITO: {verdict}\n" +
               $"Antes: {Describe(before)}\n" +
               $"Depois: {Describe(after)}\n" +
               $"Media {avg:+0.0;-0.0;0.0}% | 1% low {low:+0.0;-0.0;0.0}% | p95 {p95:+0.0;-0.0;0.0}% " +
               "(no p95, negativo e' melhor)" +
               (verdict == "PIOROU" ? "\nUse \"Desfazer um modulo\" em Otimizar." : "");
    }

    private static double PercentChange(double before, double after) =>
        before <= 0.0001 ? 0 : (after - before) / before * 100.0;
}
