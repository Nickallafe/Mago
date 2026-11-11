using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

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
    private readonly Button _overlayButton;
    private readonly TextBlock _measureStatus;
    private readonly TextBlock _screenLabel;
    private readonly TextBlock _hotkeyLabel;
    private readonly TextBox _hotkeyBox;

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
            "Abra o jogo, volte a esta janela e clique em medir. O Mago mede a app que estava em foco e liga o overlay.",
            11, Ui.Dim));

        _fpsValue = Ui.Mono("FPS --   media --", 24, Ui.Green);
        _fpsStatus = Ui.Lbl("Abra o jogo e clique em \"Medir o jogo em foco\".", 11, Ui.Dim);

        _presentMon = Ui.Input("PresentMon-2.6.0-x64.exe", 430);
        _presentMon.Margin = new Thickness(0, 6, 8, 8);
        _presentMon.Text = FpsMonitor.AutoLocatePresentMon() ?? "";
        _targets = Ui.List(120);
        _fpsButton = Ui.Btn("Medir o jogo em foco", ToggleFps, primary: true);
        _overlayButton = Ui.Btn("Ligar overlay", ToggleOverlay);

        fpsBody.Children.Add(_fpsValue);
        fpsBody.Children.Add(_fpsStatus);
        fpsBody.Children.Add(Ui.Wrap(_fpsButton, _overlayButton, Ui.Btn("Reiniciar contagem", ResetFps)));

        // Opcoes avancadas: escolher PresentMon ou outro .exe a mao. Ficam escondidas porque o
        // caso normal e' um clique - o jogo em foco e' descoberto sozinho.
        var advanced = new StackPanel { Visibility = Visibility.Collapsed };
        advanced.Children.Add(Ui.Lbl("PRESENTMON E OUTRA APP", 9, Ui.Dim, bold: true));
        advanced.Children.Add(_presentMon);
        advanced.Children.Add(Ui.Wrap(
            Ui.Btn("Localizar PresentMon", BrowsePresentMon),
            Ui.Btn("Baixar PresentMon", DownloadPresentMon),
            Ui.Btn("Listar apps abertas", RefreshTargets)));
        advanced.Children.Add(_targets);
        var advancedButton = Ui.Btn("Mostrar opcoes avancadas", () => { });
        advancedButton.Click += (_, _) =>
        {
            var show = advanced.Visibility != Visibility.Visible;
            advanced.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            advancedButton.Content = show ? "Esconder opcoes avancadas" : "Mostrar opcoes avancadas";
        };
        // Ui.Wrap para o botao nao esticar os 480 px do cartao como uma barra vazia.
        fpsBody.Children.Add(Ui.Wrap(advancedButton));
        fpsBody.Children.Add(advanced);

        // ---------- overlay ----------
        var overlayCard = Card("OVERLAY NO ECRA", 480);
        var overlayBody = BodyOf(overlayCard);
        overlayBody.Children.Add(Ui.Lbl(
            "Contador de FPS por cima do jogo. Arraste-o para o canto que quiser: a posicao fica guardada.",
            11, Ui.Dim));
        // Atalho configuravel: clicar na caixa e carregar na combinacao, sem escrever nada.
        var hotkeyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var hotkeyCaption = Ui.Lbl("Atalho global:", 11, Ui.Dim);
        hotkeyCaption.VerticalAlignment = VerticalAlignment.Center;
        hotkeyRow.Children.Add(hotkeyCaption);
        _hotkeyBox = Ui.Input("", 150);
        _hotkeyBox.IsReadOnly = true;
        _hotkeyBox.Width = 150;
        _hotkeyBox.Margin = new Thickness(8, 0, 0, 0);
        _hotkeyBox.Cursor = Cursors.Hand;
        _hotkeyBox.PreviewKeyDown += OnHotkeyKey;
        hotkeyRow.Children.Add(_hotkeyBox);
        overlayBody.Children.Add(hotkeyRow);

        _hotkeyLabel = Ui.Lbl("", 9, Ui.Dim);
        _hotkeyLabel.Margin = new Thickness(0, 4, 0, 0);
        overlayBody.Children.Add(_hotkeyLabel);

        // Legenda do modo de ecra, atualizada sozinha pelo vigia do primeiro plano.
        _screenLabel = Ui.Lbl("", 11, Ui.Dim);
        _screenLabel.Margin = new Thickness(0, 6, 0, 10);
        overlayBody.Children.Add(_screenLabel);

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
        _measureStatus = Ui.Lbl("Abra o jogo; a base e o depois medem a app que estava em foco.", 11, Ui.Dim);
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
                _fpsButton.Content = "Medir o jogo em foco";
            }));
        };

        // O modo de ecra muda em segundo plano (o jogo entra em ecra inteiro): a legenda segue.
        Host.ScreenModeChanged += () =>
        {
            if (Host.Dispatcher.HasShutdownStarted) return;
            UpdateScreenLabel();
        };

        // O atalho global tambem liga o overlay: o botao e a legenda tem de refletir isso.
        Host.OverlayChanged += () =>
        {
            if (Host.Dispatcher.HasShutdownStarted) return;
            UpdateOverlayButton();
            UpdateScreenLabel();
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
        UpdateOverlayButton();
        UpdateScreenLabel();
        UpdateHotkeyLabel();
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
                if (_targets.SelectedIndex < 0 && list.Count > 0 && string.IsNullOrWhiteSpace(Host.LastApp.Process))
                    _targets.SelectedIndex = 0;
                _fpsStatus.Text = list.Count > 0
                    ? $"{list.Count} executaveis encontrados."
                    : "Nenhum executavel listado. Abra o jogo e tente de novo.";
            });
        });
    }

    /// <summary>
    /// Um clique: mede a app que estava em foco (o jogo) e liga o overlay. A escolha manual de
    /// um .exe nas opcoes avancadas continua a ter prioridade.
    /// </summary>
    private void ToggleFps()
    {
        if (Host.Fps.Running)
        {
            Host.Fps.Stop();
            _fpsButton.Content = "Medir o jogo em foco";
            _fpsStatus.Text = "Medicao parada.";
            return;
        }

        var target = _targets.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(target)) target = Host.LastApp.Process;
        if (string.IsNullOrWhiteSpace(target))
        {
            _fpsStatus.Foreground = Ui.Br(Ui.Gold);
            _fpsStatus.Text = "Abra o jogo, volte a esta janela e clique de novo: o Mago mede a app que estava em foco.";
            return;
        }

        var presentMon = _presentMon.Text.Trim().Trim('"');
        if (presentMon.Length == 0) presentMon = FpsMonitor.AutoLocatePresentMon() ?? "";
        if (presentMon.Length == 0)
        {
            _fpsStatus.Foreground = Ui.Br(Ui.Gold);
            _fpsStatus.Text = "PresentMon nao encontrado. Abra as opcoes avancadas e clique em Baixar PresentMon.";
            return;
        }
        _presentMon.Text = presentMon;

        if (!Host.Fps.Start(presentMon, target, out var error))
        {
            _fpsStatus.Text = error;
            return;
        }

        _fpsButton.Content = "Parar medicao";
        _fpsStatus.Foreground = Ui.Br(Ui.Txt);
        _fpsStatus.Text = $"A medir {System.IO.Path.GetFileNameWithoutExtension(target)}.";

        // Medir sem ver o numero nao serve de nada: o overlay liga-se com a medicao.
        if (Host.Overlay == null)
        {
            Host.ToggleOverlay(true);
            UpdateOverlayButton();
            UpdateScreenLabel();
        }
    }

    private void ToggleOverlay() => Host.ToggleOverlay(Host.Overlay == null);

    /// <summary>O atalho so' se anuncia quando ficou mesmo registado.</summary>
    private void UpdateHotkeyLabel()
    {
        _hotkeyBox.Text = Host.OverlayHotkeyText;
        _hotkeyLabel.Text = Host.OverlayHotkeyActive
            ? "Clique na caixa e carregue na combinacao (Ctrl, Alt, Shift ou Win + uma tecla)."
            : $"Atalho indisponivel: outra app ja' usa {Host.OverlayHotkeyText}.";
    }

    /// <summary>Le a combinacao do teclado: mais simples e mais seguro do que a escrever a mao.</summary>
    private void OnHotkeyKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
            or Key.RightShift or Key.LWin or Key.RWin or Key.None) return;
        if (key is Key.Tab or Key.Escape) return;

        var parts = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(KeyName(key));

        if (Host.SetOverlayHotkey(string.Join("+", parts), out var error))
        {
            UpdateHotkeyLabel();
            _hotkeyLabel.Text = $"Atalho {Host.OverlayHotkeyText} registado.";
        }
        else
        {
            _hotkeyLabel.Text = error;
        }
    }

    private static string KeyName(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return ((char)('0' + (key - Key.NumPad0))).ToString();
        if (key is >= Key.F1 and <= Key.F24) return "F" + (key - Key.F1 + 1);
        return key.ToString();
    }

    private void UpdateOverlayButton() =>
        _overlayButton.Content = Host.Overlay == null ? "Ligar overlay" : "Desligar overlay";

    /// <summary>Diz o que o vigia viu: janela, ecra inteiro com overlay por cima, ou exclusivo.</summary>
    private void UpdateScreenLabel()
    {
        var screen = Host.GameScreen;
        if (!screen.Fullscreen)
        {
            _screenLabel.Foreground = Ui.Br(Ui.Dim);
            _screenLabel.Text = "Modo do jogo: janela. O overlay fica por cima do jogo quando o ligar.";
            return;
        }

        var name = screen.ShortName.Length > 0 ? screen.ShortName : "O jogo";
        if (Host.OverlayBehindGame)
        {
            _screenLabel.Foreground = Ui.Br(Ui.Gold);
            _screenLabel.Text = $"{name} em tela cheia exclusiva: o Windows esconde qualquer sobreposicao. " +
                                "Use \"tela cheia sem bordas\" ou janela - o FPS continua a ser medido.";
        }
        else
        {
            _screenLabel.Foreground = Ui.Br(Ui.Green);
            _screenLabel.Text = $"{name} em tela cheia: o overlay fica por cima do jogo.";
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
        var target = _targets.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(target)) target = Host.LastApp.Process;
        if (string.IsNullOrWhiteSpace(target))
        {
            Host.Info("Abra o jogo, volte a esta janela e tente de novo.", "Medir");
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
            _fpsButton.Content = "Medir o jogo em foco";

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
