using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MagoDaPerformance;

public sealed class PanelPage : PageBase
{
    private readonly TextBlock _headline;
    private readonly TextBlock _vOs, _vCpu, _vGpu, _vRam, _vDisk;
    private readonly TextBlock _drvStatus;
    private readonly ListBox _drvList;
    private readonly TextBlock _fpsValue;
    private readonly TextBlock _fpsStatus;
    private readonly TextBox _presentMon;
    private readonly ListBox _targets;
    private readonly Button _fpsButton;
    private readonly CheckBox _overlay;
    private readonly TextBlock _stress;

    private bool _scanning;
    private bool _downloading;

    public PanelPage(MainWindow host) : base(host, "Bem-vindo",
        "Resumo do seu PC, drivers e atalhos do dia a dia.")
    {
        // ---------- o meu computador ----------
        var pc = Card("O MEU COMPUTADOR", 970);
        var pcBody = BodyOf(pc);
        _headline = Ui.Lbl("A ler o hardware...", 16, Ui.Txt, bold: true);
        _headline.Margin = new Thickness(0, 0, 0, 14);
        pcBody.Children.Add(_headline);

        var specs = new Grid();
        specs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        specs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) specs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _vOs = Spec(specs, 0, 0, "SISTEMA OPERATIVO");
        _vCpu = Spec(specs, 0, 1, "PROCESSADOR");
        _vGpu = Spec(specs, 1, 0, "GRAFICA");
        _vRam = Spec(specs, 1, 1, "MEMORIA");
        _vDisk = Spec(specs, 2, 0, "ARMAZENAMENTO");
        pcBody.Children.Add(specs);

        pcBody.Children.Add(Ui.Wrap(
            Ui.Btn("Atualizar informacoes", () => Host.RefreshHardware(), primary: true)));

        // ---------- drivers ----------
        var drv = Card("DRIVERS E ATUALIZACOES", 970);
        var drvBody = BodyOf(drv);
        drvBody.Children.Add(Ui.Lbl(
            "O Mago le a versao e a data dos drivers principais e avisa quando algum esta antigo.",
            11, Ui.Dim));
        _drvStatus = Ui.Lbl("A verificar os drivers instalados...", 12, Ui.Txt, bold: true);
        _drvStatus.Margin = new Thickness(0, 6, 0, 4);
        drvBody.Children.Add(_drvStatus);
        _drvList = Ui.List(168);
        _drvList.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        drvBody.Children.Add(_drvList);
        drvBody.Children.Add(Ui.Wrap(
            Ui.Btn("Verificar no Windows Update", CheckUpdates, primary: true),
            Ui.Btn("Instalar atualizacoes", InstallUpdates),
            Ui.Btn("Abrir Windows Update", () => Proc.OpenUri("ms-settings:windowsupdate"))));

        // ---------- fps ----------
        var fpsCard = Card("FPS DO JOGO", 480);
        var fpsBody = BodyOf(fpsCard);
        fpsBody.Children.Add(Ui.Lbl("Mede os quadros por segundo do jogo que estiver aberto.", 11, Ui.Dim));
        _fpsValue = Ui.Mono("FPS --   media --", 24, Ui.Green);
        _fpsStatus = Ui.Lbl("Selecione o executavel do jogo e o PresentMon.", 11, Ui.Dim);

        _presentMon = Ui.Input("PresentMon-2.6.0-x64.exe", 430);
        _presentMon.Margin = new Thickness(0, 6, 8, 8);
        _presentMon.Text = FpsMonitor.AutoLocatePresentMon() ?? "";
        _targets = Ui.List(120);
        _fpsButton = Ui.Btn("INICIAR MEDICAO", ToggleFps, primary: true);
        _overlay = new CheckBox
        {
            Content = "Overlay sempre no topo",
            Foreground = Ui.Br(Ui.Txt),
            Margin = new Thickness(8, 6, 12, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _overlay.Checked += (_, _) => Host.ToggleOverlay(true);
        _overlay.Unchecked += (_, _) => Host.ToggleOverlay(false);

        fpsBody.Children.Add(_fpsValue);
        fpsBody.Children.Add(_fpsStatus);
        fpsBody.Children.Add(_presentMon);
        fpsBody.Children.Add(Ui.Wrap(
            Ui.Btn("Localizar PresentMon", BrowsePresentMon),
            Ui.Btn("Baixar", DownloadPresentMon),
            Ui.Btn("Listar jogos abertos", RefreshTargets)));
        fpsBody.Children.Add(_targets);
        fpsBody.Children.Add(Ui.Wrap(_fpsButton, _overlay, Ui.Btn("Reiniciar contagem", ResetFps)));

        // Aspeto do overlay: aplicado ao vivo e guardado em mago_settings.json.
        var opacityLabel = Ui.Lbl("", 11, Ui.Dim);
        var opacity = OverlaySlider(
            FpsOverlay.MinOpacity * 100, 100, 5,
            Math.Clamp(Host.Settings.OverlayOpacity, FpsOverlay.MinOpacity, 1) * 100);
        void ShowOpacity() => opacityLabel.Text = $"Opacidade do overlay: {opacity.Value:0}%";
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

        fpsBody.Children.Add(opacityLabel);
        fpsBody.Children.Add(opacity);
        fpsBody.Children.Add(sizeLabel);
        fpsBody.Children.Add(size);

        // ---------- manutencao ----------
        var quickCard = Card("MANUTENCAO RAPIDA", 480);
        var quickBody = BodyOf(quickCard);
        quickBody.Children.Add(Ui.Lbl("Tarefas simples que costumam resolver lentidao.", 11, Ui.Dim));
        _stress = Ui.Lbl("", 11, Ui.Dim);
        quickBody.Children.Add(Ui.Wrap(
            Ui.Btn("Teste de carga 15s", () => Host.RunTask(() => Host.Engine.StressTest(15), "Teste de carga...")),
            Ui.Btn("Limpar DNS", () => Host.Engine.FlushDns()),
            Ui.Btn("Reiniciar Explorer", () =>
            {
                if (Host.Confirm("Reiniciar o Explorador de Ficheiros?", "Explorer")) Host.Engine.RestartExplorer();
            })));
        quickBody.Children.Add(_stress);

        Add(pc);
        Add(drv);
        Add(Row(fpsCard, quickCard));

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
                _fpsButton.Content = "INICIAR MEDICAO";
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

    private static TextBlock Spec(Grid grid, int row, int column, string label)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 20, 14) };
        stack.Children.Add(Ui.Lbl(label, 9, Ui.Dim, bold: true, wrap: false));
        var value = Ui.Lbl("-", 13, Ui.Txt);
        stack.Children.Add(value);
        Grid.SetRow(stack, row);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
        return value;
    }

    public void SetHardware(HardwareInfo hw)
    {
        var os = FriendlyOs();
        var ram = hw.RamGb > 0 ? $"{hw.RamGb:0.#} GB" : "?";
        _headline.Text = $"{os}  ·  {Environment.ProcessorCount} nucleos  ·  {ram} de RAM";
        _vOs.Text = os;
        _vCpu.Text = string.IsNullOrWhiteSpace(hw.Cpu) ? "-" : hw.Cpu;
        _vGpu.Text = string.IsNullOrWhiteSpace(hw.Gpu) ? "-" : hw.Gpu;
        _vRam.Text = ram;
        _vDisk.Text = hw.Disks.Count > 0 ? string.Join("  •  ", hw.Disks) : "Nao identificado";
    }

    /// <summary>"Microsoft Windows 10.0.26100" nao diz nada ao utilizador.</summary>
    private static string FriendlyOs()
    {
        var v = Environment.OSVersion.Version;
        var name = v.Build >= 22000 ? "Windows 11" : v.Build >= 10240 ? "Windows 10" : "Windows";
        return $"{name} (build {v.Build})";
    }

    /// <summary>Le os drivers em segundo plano (nao bloqueia a janela).</summary>
    public void BeginScan()
    {
        if (_scanning) return;
        _scanning = true;
        _drvStatus.Foreground = Ui.Br(Ui.Txt);
        _drvStatus.Text = "A verificar os drivers instalados...";
        _drvList.Items.Clear();
        System.Threading.Tasks.Task.Run(() =>
        {
            List<DriverRow> rows;
            try { rows = Drivers.Scan(); }
            catch (Exception ex) { Log.Write("Drivers: " + ex.Message, "ERRO"); rows = new(); }
            Host.Dispatcher.Invoke(() =>
            {
                FillDrivers(rows);
                _scanning = false;
            });
        });
    }

    private void FillDrivers(List<DriverRow> rows)
    {
        _drvList.Items.Clear();
        if (rows.Count == 0)
        {
            _drvStatus.Text = "Nao foi possivel ler a lista de drivers.";
            _drvStatus.Foreground = Ui.Br(Ui.Gold);
            _drvList.Items.Add(Ui.Lbl("Sem dados de drivers neste sistema.", 11, Ui.Dim));
            return;
        }

        var old = rows.Count(r => r.Old);
        if (old == 0)
        {
            _drvStatus.Text = $"{rows.Count} drivers verificados - todos recentes.";
            _drvStatus.Foreground = Ui.Br(Ui.Green);
        }
        else
        {
            _drvStatus.Text = $"{rows.Count} drivers verificados - {old} com mais de 2 anos. Veja o Windows Update.";
            _drvStatus.Foreground = Ui.Br(Ui.Gold);
        }

        foreach (var row in rows.Take(12)) _drvList.Items.Add(DriverItem(row));
        // Altura justa: com poucos drivers a caixa nao fica com espaco vazio nem barra.
        _drvList.Height = Math.Min(168, 26 * Math.Min(rows.Count, 12) + 10);
    }

    private static UIElement DriverItem(DriverRow row)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        var name = Ui.Lbl(row.Device, 12, Ui.Txt, wrap: false);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = $"{row.Device}\nClasse: {row.Class}\nFabricante: {row.Provider}";
        var meta = Ui.Lbl($"v{row.Version} · {row.Date}", 11, Ui.Dim, wrap: false);
        var badge = Ui.Lbl(row.Old ? "DESATUALIZADO" : "recente", 10,
            row.Old ? Ui.Gold : Ui.Green, bold: true, wrap: false);

        Grid.SetColumn(name, 0);
        Grid.SetColumn(meta, 1);
        Grid.SetColumn(badge, 2);
        grid.Children.Add(name);
        grid.Children.Add(meta);
        grid.Children.Add(badge);
        return grid;
    }

    private void CheckUpdates()
    {
        Host.RunTask(() =>
        {
            var (ok, message, titles) = Drivers.CheckUpdates();
            Host.Dispatcher.Invoke(() =>
            {
                if (!ok)
                {
                    _drvStatus.Text = "Nao foi possivel consultar o Windows Update.";
                    _drvStatus.Foreground = Ui.Br(Ui.Gold);
                }
                else if (titles.Count == 0)
                {
                    _drvStatus.Text = "Windows Update: nenhuma atualizacao de driver pendente.";
                    _drvStatus.Foreground = Ui.Br(Ui.Green);
                }
                else
                {
                    _drvStatus.Text = $"Windows Update: {titles.Count} atualizacao(oes) de driver disponivel(is).";
                    _drvStatus.Foreground = Ui.Br(Ui.Gold);
                }
            });
            return message;
        }, "A consultar o Windows Update...");
    }

    private void InstallUpdates()
    {
        if (!Host.Confirm(
                "Isto descarrega e instala as atualizacoes de driver do Windows Update.\n" +
                "Pode demorar e pode ser necessario reiniciar o PC. Continuar?",
                "Atualizar drivers")) return;

        Host.RunTask(() =>
        {
            var (ok, message) = Drivers.InstallUpdates();
            Host.Dispatcher.Invoke(() =>
            {
                _drvStatus.Text = ok ? "Atualizacoes de driver concluidas." : "Falha ao instalar atualizacoes de driver.";
                _drvStatus.Foreground = Ui.Br(ok ? Ui.Green : Ui.Gold);
            });
            BeginScan();
            return message;
        }, "A instalar atualizacoes de driver...");
    }

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
            _fpsButton.Content = "INICIAR MEDICAO";
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
            _fpsButton.Content = "PARAR MEDICAO";
            _fpsStatus.Text = $"A medir {target}. Se o jogo nao estiver aberto, aguarde.";
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
