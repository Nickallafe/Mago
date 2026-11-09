using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MagoDaPerformance;

public partial class MainWindow : Window
{
    private readonly ContentControl _host = new();
    private readonly Dictionary<string, Button> _nav = new();
    private readonly Dictionary<string, PageBase> _pages = new();
    private readonly TextBlock _lblCpu, _lblRam, _lblDisk, _lblStatus;
    private readonly ProgressBar _bar = new();
    private readonly DispatcherTimer _timer;
    private PanelPage? _panel;

    public BackupStore Store { get; }
    public TweakEngine Engine { get; }
    public HardwareInfo? Hardware { get; private set; }
    public FpsMonitor Fps { get; } = new();
    public FpsOverlay? Overlay { get; private set; }
    public AppSettings Settings { get; }

    public MainWindow()
    {
        Title = "MAGO DA PERFORMANCE v6.0";
        Width = 1280;
        Height = 860;
        MinWidth = 1100;
        MinHeight = 740;
        Background = Ui.Br(Ui.Bg);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Ui.UiFont;

        Directory.CreateDirectory(Paths.DataDir);
        Store = new BackupStore(Paths.BackupFile);
        Engine = new TweakEngine(Store);
        Settings = AppSettings.Load();

        _lblCpu = Ui.Mono("CPU --%", 12, Ui.Green);
        _lblRam = Ui.Mono("RAM --%", 12, Ui.Cyan);
        _lblDisk = Ui.Mono("C: --%", 12, Ui.Gold);
        _lblStatus = Ui.Lbl("Pronto.", 11, Ui.Dim, wrap: false);
        _lblCpu.Margin = new Thickness(10, 0, 10, 0);
        _lblRam.Margin = new Thickness(10, 0, 10, 0);
        _lblDisk.Margin = new Thickness(10, 0, 10, 0);
        _lblStatus.Margin = new Thickness(16, 0, 0, 0);

        _bar.IsIndeterminate = true;
        _bar.Height = 2;
        _bar.Visibility = Visibility.Collapsed;
        _bar.Foreground = Ui.Br(Ui.Green);
        _bar.Background = Ui.Br(Ui.Card);

        var root = new Grid { Background = Ui.Br(Ui.Bg) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(BuildHeader());
        root.Children.Add(BuildBody());

        Content = root;
        root.Children.Add(_bar);
        Grid.SetRow(_bar, 1);
        Grid.SetColumn(_bar, 0);
        VerticalAlignment = VerticalAlignment.Top;

        BuildPages();
        ShowPage(Program.StartPage ?? "painel");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _timer.Tick += (_, _) => TickMeters();
        _timer.Start();

        Loaded += (_, _) =>
        {
            Log.Write("Mago da Performance v6.0 (C#) iniciado.");
            if (!Proc.IsAdmin()) Log.Write("Sem privilegios de administrador - varias funcoes vao falhar.", "AVISO");
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var hw = SystemInfo.Detect();
                    Dispatcher.Invoke(() =>
                    {
                        Hardware = hw;
                        Engine.GpuName = hw.Gpu;
                        Engine.DiskTypes = hw.DiskTypes;
                        _panel?.SetHardware(hw);
                        _panel?.BeginScan();
                    });
                }
                catch (Exception ex) { Log.Write(ex.Message, "ERRO"); }
            });
            StartAutoUpdate();
        };

        Closing += (_, _) =>
        {
            // Regista o fecho normal: se o log nao tiver esta linha, o processo foi morto.
            Log.Write("A fechar a janela principal.");
            Fps.Dispose();
            try { Overlay?.Close(); } catch { }
        };
    }

    private UIElement BuildHeader()
    {
        var panel = new Grid { Background = Ui.Br(Ui.Card) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(22, 0, 0, 0) };
        left.Children.Add(Ui.Lbl("MAGO", 20, Ui.Purple, bold: true, wrap: false));
        left.Children.Add(Ui.Lbl(" DA PERFORMANCE", 20, Ui.Txt, bold: true, wrap: false));
        var version = Ui.Lbl("   v6.0  CONTROL CENTER", 10, Ui.Dim, wrap: false);
        version.VerticalAlignment = VerticalAlignment.Center;
        left.Children.Add(version);

        var meters = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        meters.Children.Add(_lblCpu);
        meters.Children.Add(_lblRam);
        meters.Children.Add(_lblDisk);
        meters.Children.Add(_lblStatus);

        Grid.SetColumn(left, 0);
        Grid.SetColumn(meters, 2);
        panel.Children.Add(left);
        panel.Children.Add(meters);
        return panel;
    }

    private UIElement BuildBody()
    {
        var body = new Grid();
        Grid.SetRow(body, 1);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(214) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var side = new Border
        {
            Background = Ui.Br(Ui.Side),
            BorderBrush = Ui.Br(Ui.Line),
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        var sideStack = new StackPanel();
        var nav = new (string Key, string Label)[]
        {
            ("painel", "PAINEL"), ("otimizar", "OTIMIZAR"), ("rede", "REDE"),
            ("apps", "APLICATIVOS"), ("backup", "BACKUP"), ("bios", "BIOS"), ("ferramentas", "FERRAMENTAS"),
        };
        foreach (var (key, label) in nav)
        {
            var b = new Button
            {
                Content = label,
                Foreground = Ui.Br(Ui.Dim),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(24, 14, 24, 14),
                Margin = new Thickness(0, 1, 0, 1),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = FlatTemplate(),
            };
            b.Click += (_, _) => ShowPage(key);
            sideStack.Children.Add(b);
            _nav[key] = b;
        }
        sideStack.Children.Add(new Border
        {
            Background = Ui.Br(Ui.Line),
            Height = 1,
            Margin = new Thickness(20, 18, 20, 14),
        });
        sideStack.Children.Add(new TextBlock
        {
            Text = Proc.IsAdmin() ? "ADMIN ATIVO" : "SEM PRIVILEGIOS",
            Foreground = Ui.Br(Proc.IsAdmin() ? Ui.Green : Ui.Gold),
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(24, 0, 0, 6),
        });
        var logButton = new Button
        {
            Content = "Abrir log",
            Foreground = Ui.Br(Ui.Dim),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(24, 8, 24, 8),
            FontSize = 11,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = FlatTemplate(),
        };
        logButton.Click += (_, _) => Proc.OpenUri(Paths.LogFile);
        sideStack.Children.Add(logButton);
        side.Child = sideStack;

        _host.Margin = new Thickness(16, 14, 16, 8);
        Grid.SetColumn(side, 0);
        Grid.SetColumn(_host, 1);
        body.Children.Add(side);
        body.Children.Add(_host);
        return body;
    }

    private static ControlTemplate FlatTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private void BuildPages()
    {
        _panel = new PanelPage(this);
        _pages["painel"] = _panel;
        _pages["otimizar"] = new OptimizePage(this);
        _pages["rede"] = new NetworkPage(this);
        _pages["apps"] = new AppsPage(this);
        _pages["backup"] = new BackupPage(this);
        _pages["bios"] = new BiosPage(this);
        _pages["ferramentas"] = new ToolsPage(this);
    }

    /// <summary>Aceita as chaves internas e os nomes que aparecem na barra lateral.</summary>
    private static string ResolvePage(string key) => key switch
    {
        "aplicativos" or "app" or "programas" => "apps",
        "backup" or "restauro" or "restore" => "backup",
        "bios" or "firmware" => "bios",
        "ferramentas" or "tools" => "ferramentas",
        "otimizar" or "otimizacoes" or "optimize" => "otimizar",
        "rede" or "network" => "rede",
        _ => key,
    };

    public void ShowPage(string key)
    {
        key = ResolvePage(key);
        if (!_pages.ContainsKey(key)) key = "painel";
        if (!_pages.TryGetValue(key, out var page)) return;
        _host.Content = page.Root;
        // Cada pagina pode carregar o que precisa ao ficar visivel (ex.: pre-analise de rede).
        if (_host.Content != null) page.OnShown();
        foreach (var (name, btn) in _nav)
        {
            var active = name == key;
            btn.Foreground = Ui.Br(active ? Ui.Green : Ui.Dim);
            btn.Background = Ui.Br(active ? Color.FromRgb(0x15, 0x1C, 0x27) : Ui.Side);
        }
    }

    public void Status(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Status(text)); return; }
        _lblStatus.Text = text;
    }

    public void Busy(bool busy, string? label = null)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Busy(busy, label)); return; }
        _bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrEmpty(label)) _lblStatus.Text = label;
    }

    public bool Confirm(string message, string title = "Confirmar") =>
        MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Info(string message, string title = "Mago") =>
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private void TickMeters()
    {
        try
        {
            _lblCpu.Text = $"CPU {SystemInfo.CpuUsage():0}%";
            _lblRam.Text = $"RAM {SystemInfo.Memory().UsedPercent:0}%";
            _lblDisk.Text = $"C: {SystemInfo.DiskUsedPercent():0}%";
        }
        catch { }
    }

    /// <summary>Corre uma tarefa longa sem bloquear a UI.</summary>
    public void RunTask(Func<string?> work, string busyLabel)
    {
        Busy(true, busyLabel);
        Status(busyLabel);
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var result = work();
                if (!string.IsNullOrEmpty(result)) Dispatcher.Invoke(() => Info(result!));
            }
            catch (Exception ex) { Log.Write(ex.Message, "ERRO"); }
            finally
            {
                Dispatcher.Invoke(() => { Busy(false, "Pronto."); });
            }
        });
    }

    /// <summary>Relê o hardware e volta a verificar os drivers.</summary>
    private bool _checkingUpdates;

    /// <summary>Verifica e instala a atualizacao logo apos abrir (se estiver ligada).</summary>
    private void StartAutoUpdate()
    {
        if (!Settings.AutoUpdate) return;
        System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(4000);   // deixa a janela assentar
            CheckForUpdates(silent: true);
        });
    }

    /// <summary>Consulta a ultima release; se houver versao nova, descarrega e instala.</summary>
    public void CheckForUpdates(bool silent)
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        Status("A procurar atualizacoes...");
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var info = Updater.CheckForUpdate();
                if (info == null)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Status("Pronto.");
                        if (!silent) Info($"Ja tem a versao mais recente ({Updater.DisplayVersion}).", "Atualizacoes");
                    }));
                    return;
                }

                if (string.IsNullOrEmpty(info.InstallerUrl))
                {
                    Log.Write($"Versao {info.Version} sem instalador anexado.", "AVISO");
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Status("Pronto.");
                        if (!silent) Info($"Nova versao {info.Version}: {info.PageUrl}", "Atualizacoes");
                    }));
                    return;
                }

                if (!Updater.IsInstalledCopy())
                {
                    Log.Write($"Versao {info.Version} disponivel; copia portatil nao se atualiza sozinha.", "AVISO");
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Status("Pronto.");
                        if (!silent) Info($"Nova versao {info.Version} disponivel em\n{info.PageUrl}", "Atualizacoes");
                    }));
                    return;
                }

                if (!silent && !ConfirmOnUi($"Nova versao {info.Version} disponivel. Atualizar agora?", "Atualizacoes"))
                {
                    Dispatcher.BeginInvoke(new Action(() => Status("Pronto.")));
                    return;
                }

                Dispatcher.BeginInvoke(new Action(() => Status($"A descarregar a versao {info.Version}...")));
                var installer = Updater.Download(info, message =>
                {
                    if (Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke(new Action(() => Status(message)));
                });

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Status($"A instalar a versao {info.Version}...");
                    if (!Updater.InstallAndRestart(installer))
                    {
                        Status("Falha ao iniciar a atualizacao.");
                        if (!silent) Info("Nao foi possivel iniciar o instalador:\n" + installer, "Atualizacoes");
                        return;
                    }
                    Application.Current.Shutdown();
                }));
            }
            catch (Exception ex)
            {
                Log.Write("Atualizacao: " + ex.Message, "AVISO");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Status(silent ? "Pronto." : "Falha ao verificar atualizacoes.");
                    if (!silent) Info("Nao foi possivel verificar atualizacoes.\n" + ex.Message, "Atualizacoes");
                }));
            }
            finally
            {
                _checkingUpdates = false;
            }
        });
    }

    private bool ConfirmOnUi(string message, string title)
    {
        if (Dispatcher.CheckAccess()) return Confirm(message, title);
        return Dispatcher.Invoke(() => Confirm(message, title));
    }

    public void RefreshHardware()
    {
        RunTask(() =>
        {
            var hw = SystemInfo.Detect();
            Dispatcher.Invoke(() =>
            {
                Hardware = hw;
                Engine.GpuName = hw.Gpu;
                Engine.DiskTypes = hw.DiskTypes;
                _panel?.SetHardware(hw);
            });
            return null;
        }, "A identificar hardware...");
        _panel?.BeginScan();
    }

    public void ToggleOverlay(bool show, Action<string>? onToggle = null)
    {
        if (show)
        {
            Overlay ??= new FpsOverlay();
            Overlay.Show();
        }
        else
        {
            try { Overlay?.Close(); } catch { }
            Overlay = null;
        }
        onToggle?.Invoke(show ? "Overlay FPS ligado" : "Overlay FPS desligado");
    }

    public void UpdateOverlay(double fps)
    {
        if (Overlay is { IsVisible: true }) Overlay.UpdateFps(fps);
    }
}
