using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
    private ColumnDefinition? _sideColumn;
    private FrameworkElement? _headerMeters;
    private FrameworkElement? _headerVersion;
    private DispatcherTimer? _screenWatch;

    /// <summary>O que esta' em primeiro plano e, se for o caso, em ecra inteiro.</summary>
    public GameScreen GameScreen { get; private set; } = GameScreen.None;

    /// <summary>
    /// Ultima app que esteve em frente. Fica guardada porque, ao clicar no Mago para medir,
    /// o foco passa para o Mago e o jogo deixaria de ser "a app em primeiro plano".
    /// </summary>
    public GameScreen LastApp { get; private set; } = GameScreen.None;

    /// <summary>O overlay ficou abaixo do jogo - sinal de tela cheia exclusiva.</summary>
    public bool OverlayBehindGame { get; private set; }

    /// <summary>Muda quando o jogo entra/sai de ecra inteiro ou troca de modo.</summary>
    public event Action? ScreenModeChanged;

    /// <summary>O overlay foi ligado/desligado (pelo botao ou pelo atalho global).</summary>
    public event Action? OverlayChanged;

    /// <summary>Atalho global atual (texto guardado nas preferencias).</summary>
    public string OverlayHotkeyText => Settings.OverlayHotkey;

    /// <summary>Falso quando outra app ja' tem o atalho: a UI nao deve prometer o que nao funciona.</summary>
    public bool OverlayHotkeyActive { get; private set; }

    /// <summary>O atalho guardado estava ocupado e o Mago voltou ao de fabrica sozinho.</summary>
    public bool HotkeyWasReset { get; private set; }

    public BackupStore Store { get; }
    public TweakEngine Engine { get; }
    public HardwareInfo? Hardware { get; private set; }
    public FpsMonitor Fps { get; } = new();

    /// <summary>Amostragem de GPU/CPU durante a medicao de FPS (ver LoadSampler).</summary>
    public LoadSampler Load { get; } = new();
    public FpsOverlay? Overlay { get; private set; }
    public AppSettings Settings { get; }

    public MainWindow()
    {
        Title = $"MAGO DA PERFORMANCE {Updater.DisplayVersion}";
        FitWindowToScreen();
        Background = Ui.Br(Ui.Bg);
        FontFamily = Ui.UiFont;

        // CheckBox/RadioButton seguem a paleta (o template padrao do Windows e' claro).
        Resources[typeof(CheckBox)] = Ui.DarkCheck;
        Resources[typeof(RadioButton)] = Ui.DarkRadio;

        // Breakpoints: Compact <768, Medium <1200, Wide >=1200.
        // Aplicado diretamente no layout porque a janela nao define VisualStates.
        SizeChanged += OnSizeChanged;
        // Per-monitor v2: ao mudar de ecra com outra escala, garante que a janela continua a caber.
        DpiChanged += (_, _) => ClampToWorkArea();
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
        // Estados longos nao podem empurrar o cabecalho para fora em janelas estreitas.
        _lblStatus.MaxWidth = 300;
        _lblStatus.TextTrimming = TextTrimming.CharacterEllipsis;

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

        BuildPages();
        ShowPage(Program.StartPage ?? "painel");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _timer.Tick += (_, _) => TickMeters();
        _timer.Start();

        Loaded += (_, _) =>
        {
            Log.Write($"Mago da Performance {Updater.DisplayVersion} (C#) iniciado.");
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

        // Vigia o primeiro plano: diz qual e' o jogo a medir e se o overlay ficou por cima.
        StartScreenWatch();
        // So' ha handle depois de a janela ser criada; o atalho precisa dele.
        SourceInitialized += (_, _) => RegisterOverlayHotkey();

        Closing += (_, _) =>
        {
            // Regista o fecho normal: se o log nao tiver esta linha, o processo foi morto.
            Log.Write("A fechar a janela principal.");
            _screenWatch?.Stop();
            UnregisterOverlayHotkey();
            Load.Dispose();
            Fps.Dispose();
            try { Overlay?.Close(); } catch { }
        };
    }

    /// <summary>Uma leitura por segundo e' suficiente e nao se nota no jogo.</summary>
    private void StartScreenWatch()
    {
        _screenWatch = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => WatchScreen(),
            Dispatcher);
        _screenWatch.Start();
    }

    private void WatchScreen()
    {
        var screen = FullScreen.Detect();
        if (screen.Process.Length > 0) LastApp = screen;

        // Perguntar ao overlay se ficou atras do jogo e' a unica forma honesta de saber se o
        // Windows o esta' a esconder (ecra inteiro exclusivo): nao ha API que o diga.
        var behind = screen.Fullscreen && Overlay is { } overlay && overlay.IsBehind(screen.Handle);
        if (screen.Fullscreen) Overlay?.FollowMonitor(screen.Monitor);

        if (screen == GameScreen && behind == OverlayBehindGame) return;

        GameScreen = screen;
        OverlayBehindGame = behind;
        ScreenModeChanged?.Invoke();
    }

    // ---- atalho global do overlay ----

    private const int OverlayHotkeyId = 0x4A47;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private HwndSource? _hotkeySource;
    private Button? _hotkeyWarning;

    /// <summary>
    /// Liga o atalho guardado. O Windows entrega WM_HOTKEY ao handle que o registou e nao ativa
    /// a janela, por isso o jogo nao perde o foco ao ligar/desligar o overlay.
    /// </summary>
    private void RegisterOverlayHotkey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        _hotkeySource = HwndSource.FromHwnd(handle);
        _hotkeySource?.AddHook(HotkeyHook);

        if (TryRegisterHotkey(Settings.OverlayHotkey, out var error))
        {
            UpdateHotkeyWarning();
            return;
        }

        // Ficar sem atalho por causa de outra app e' pior do que voltar ao de fabrica: o
        // utilizador quer usar o overlay, nao discutir combinacoes de teclas.
        var saved = Settings.OverlayHotkey;
        var fellBack = !saved.Equals(Hotkey.DefaultText, StringComparison.OrdinalIgnoreCase) &&
                       TryRegisterHotkey(Hotkey.DefaultText, out _);
        if (fellBack)
        {
            HotkeyWasReset = true;
            Settings.OverlayHotkey = Hotkey.DefaultText;
            Settings.Save();
            Log.Write($"{error} Atalho reposto em {Hotkey.DefaultText}.", "AVISO");
            Status($"Atalho {saved} ocupado: reposto em {Hotkey.DefaultText}.");
            UpdateHotkeyWarning();
            return;
        }

        Log.Write(error, "AVISO");
        Status(error);
        UpdateHotkeyWarning();
    }

    /// <summary>
    /// Aviso no cabecalho enquanto o atalho nao estiver registado. O rodape passa despercebido
    /// e quem carrega na tecla e nao ve nada conclui que o overlay e' que esta' avariado.
    /// </summary>
    private void UpdateHotkeyWarning()
    {
        if (_hotkeyWarning == null) return;

        _hotkeyWarning.Visibility = OverlayHotkeyActive ? Visibility.Collapsed : Visibility.Visible;
        if (OverlayHotkeyActive) return;

        _hotkeyWarning.Content = $"{Settings.OverlayHotkey} INDISPONIVEL - TROCAR";
        _hotkeyWarning.ToolTip = $"{Settings.OverlayHotkey} ja' esta' a ser usado por outra app. " +
                                 "Clique para escolher outro atalho na pagina Jogos.";
    }

    private void OpenHotkeySettings()
    {
        ShowPage("jogos");
        Status("Escolha outro atalho no cartao OVERLAY NO ECRA.");
    }

    /// <summary>
    /// Troca o atalho global. Se o novo estiver ocupado, o anterior volta a funcionar: quem
    /// experimenta uma combinacao nao pode ficar sem atalho nenhum.
    /// </summary>
    public bool SetOverlayHotkey(string text, out string error)
    {
        var previous = Settings.OverlayHotkey;
        ReleaseHotkey();

        if (TryRegisterHotkey(text, out error))
        {
            Settings.OverlayHotkey = Hotkey.TryParse(text, out var parsed) ? parsed.Describe() : text;
            Settings.Save();
            HotkeyWasReset = false;   // escolha do utilizador: o aviso de reposicao deixa de fazer sentido
            Status($"Atalho do overlay: {Settings.OverlayHotkey}");
            UpdateHotkeyWarning();
            return true;
        }

        if (!TryRegisterHotkey(previous, out var previousError)) Log.Write(previousError, "AVISO");
        UpdateHotkeyWarning();
        return false;
    }

    private bool TryRegisterHotkey(string text, out string error)
    {
        error = "";
        if (!Hotkey.TryParse(text, out var hotkey))
        {
            error = $"\"{text}\" nao e' um atalho valido: use Ctrl, Alt, Shift ou Win com uma tecla.";
            return false;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            error = "A janela ainda nao esta' pronta.";
            return false;
        }

        if (RegisterHotKey(handle, OverlayHotkeyId, hotkey.Modifiers | ModNoRepeat, hotkey.Key))
        {
            OverlayHotkeyActive = true;
            return true;
        }

        error = $"{hotkey.Describe()} ja' esta' a ser usado por outra app.";
        return false;
    }

    private IntPtr HotkeyHook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey || wParam.ToInt32() != OverlayHotkeyId) return IntPtr.Zero;
        handled = true;
        ToggleOverlay(Overlay == null);
        return IntPtr.Zero;
    }

    private void ReleaseHotkey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && OverlayHotkeyActive) UnregisterHotKey(handle, OverlayHotkeyId);
        OverlayHotkeyActive = false;
    }

    private void UnregisterOverlayHotkey()
    {
        ReleaseHotkey();
        _hotkeySource?.RemoveHook(HotkeyHook);
        _hotkeySource = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr handle, int id);

    private UIElement BuildHeader()
    {
        var panel = new Grid { Background = Ui.Br(Ui.Card) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(22, 0, 0, 0) };
        left.Children.Add(Ui.Lbl("MAGO", 20, Ui.Purple, bold: true, wrap: false));
        left.Children.Add(Ui.Lbl(" DA PERFORMANCE", 20, Ui.Txt, bold: true, wrap: false));
        var version = Ui.Lbl($"   v{Updater.DisplayVersion}  CONTROL CENTER", 10, Ui.Dim, wrap: false);
        version.VerticalAlignment = VerticalAlignment.Center;
        _headerVersion = version;
        left.Children.Add(version);

        // CPU/RAM/disco escondem-se em janelas estreitas; o estado continua sempre visivel.
        var stats = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stats.Children.Add(_lblCpu);
        stats.Children.Add(_lblRam);
        stats.Children.Add(_lblDisk);

        // Aviso persistente do atalho ocupado, clicavel: leva direto ao sitio onde se troca.
        _hotkeyWarning = new Button
        {
            Background = Brushes.Transparent,
            Foreground = Ui.Br(Ui.Gold),
            BorderThickness = new Thickness(0),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = FlatTemplate(),
            Visibility = Visibility.Collapsed,
        };
        _hotkeyWarning.Click += (_, _) => OpenHotkeySettings();

        var meters = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        meters.Children.Add(stats);
        meters.Children.Add(_hotkeyWarning);
        meters.Children.Add(_lblStatus);

        Grid.SetColumn(left, 0);
        Grid.SetColumn(meters, 2);
        _headerMeters = stats;
        panel.Children.Add(left);
        panel.Children.Add(meters);
        return panel;
    }

    private UIElement BuildBody()
    {
        var body = new Grid();
        Grid.SetRow(body, 1);
        _sideColumn = new ColumnDefinition { Width = new GridLength(214) };
        body.ColumnDefinitions.Add(_sideColumn);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var side = new Border
        {
            Background = Ui.Br(Ui.Side),
            BorderBrush = Ui.Br(Ui.Line),
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        var sideStack = new StackPanel();
        // Secoes com titulo: a barra plana de sete itens nao dizia o que era "painel" nem
        // separava o que se usa todos os dias do que se abre uma vez por mês.
        var nav = new (string? Section, string Key, string Label)[]
        {
            ("SEU PC", "painel", "INICIO"),
            (null, "otimizar", "OTIMIZAR"),
            (null, "jogos", "JOGOS"),
            (null, "rede", "REDE"),
            ("MANUTENCAO", "apps", "APLICATIVOS"),
            (null, "backup", "BACKUP"),
            (null, "ferramentas", "FERRAMENTAS"),
        };
        foreach (var (section, key, label) in nav)
        {
            if (section != null) sideStack.Children.Add(SectionLabel(section));
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

    /// <summary>Titulo de secao da barra lateral (nao e' clicavel).</summary>
    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        Foreground = Ui.Br(Ui.Dim),
        FontSize = 9,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(24, 16, 0, 4),
        Opacity = 0.75,
    };

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
        _pages["jogos"] = new GamesPage(this);
        _pages["rede"] = new NetworkPage(this);
        _pages["apps"] = new AppsPage(this);
        _pages["backup"] = new BackupPage(this);
        _pages["ferramentas"] = new ToolsPage(this);
    }

    /// <summary>Aceita as chaves internas e os nomes que aparecem na barra lateral.</summary>
    private static string ResolvePage(string key) => key switch
    {
        "inicio" or "home" or "resumo" or "visao" => "painel",
        "jogos" or "fps" or "games" => "jogos",
        "aplicativos" or "app" or "programas" => "apps",
        "backup" or "restauro" or "restore" => "backup",
        "bios" or "firmware" => "painel",
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
        if (_host.Content != null && !SkipPageSideEffects) page.OnShown();
        foreach (var (name, btn) in _nav)
        {
            var active = name == key;
            btn.Foreground = Ui.Br(active ? Ui.Green : Ui.Dim);
            btn.Background = Ui.Br(active ? Color.FromRgb(0x15, 0x1C, 0x27) : Ui.Side);
        }
    }

    /// <summary>Conteudo da pagina visivel (usado pelos testes de layout para medir/capturar).</summary>
    internal UIElement? CurrentPageContent => _host.Content as UIElement;

    /// <summary>Nos testes de layout nao se dispara OnShown (evita rede e leituras do sistema).</summary>
    internal bool SkipPageSideEffects { get; set; }

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

    /// <summary>
    /// Corre uma tarefa longa sem bloquear a UI. Uma falha tem de chegar ao utilizador:
    /// antes so' ia para o log e a barra de estado anunciava sucesso.
    /// </summary>
    public void RunTask(Func<string?> work, string busyLabel)
    {
        Busy(true, busyLabel);
        Status(busyLabel);
        System.Threading.Tasks.Task.Run(() =>
        {
            string? result = null;
            Exception? failure = null;
            try { result = work(); }
            catch (Exception ex)
            {
                failure = ex;
                Log.Write($"{busyLabel} falhou: {ex.Message}", "ERRO");
            }

            Dispatcher.Invoke(() =>
            {
                Busy(false);
                if (failure != null)
                {
                    Status("Falhou: " + busyLabel.TrimEnd('.', '!'));
                    Info($"Nao foi possivel concluir: {busyLabel}\n\n{failure.Message}\n\nDetalhes no log.", "Falhou");
                    return;
                }

                Status("Pronto.");
                if (!string.IsNullOrEmpty(result)) Info(result!, "Concluido");
            });
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
            Overlay ??= new FpsOverlay(Settings);
            Overlay.Show();
        }
        else
        {
            try { Overlay?.Close(); } catch { }
            Overlay = null;
        }
        onToggle?.Invoke(show ? "Overlay FPS ligado" : "Overlay FPS desligado");
        OverlayChanged?.Invoke();
    }

    /// <summary>Opacidade do fundo do overlay de FPS (0.15-1), aplicada ao vivo.</summary>
    public void SetOverlayOpacity(double value, bool save)
    {
        Settings.OverlayOpacity = Math.Clamp(value, FpsOverlay.MinOpacity, 1);
        if (Overlay is { } overlay) overlay.BadgeOpacity = Settings.OverlayOpacity;
        if (save) Settings.Save();
    }

    /// <summary>Tamanho do texto do overlay de FPS em DIPs, aplicado ao vivo.</summary>
    public void SetOverlayFontSize(double value, bool save)
    {
        Settings.OverlayFontSize = Math.Clamp(value, FpsOverlay.MinFontSize, FpsOverlay.MaxFontSize);
        if (Overlay is { } overlay) overlay.BadgeFontSize = Settings.OverlayFontSize;
        if (save) Settings.Save();
    }

    /// <summary>
    /// Repoe o overlay de FPS no padrao: sem posicao guardada (volta ao canto superior direito
    /// do ecra do rato) e com opacidade/tamanho de fabrica.
    /// </summary>
    public void ResetOverlay()
    {
        Settings.ResetOverlay();
        Overlay?.ResetToDefaults();
        Settings.Save();
        Status("Overlay reposto ao padrao");
    }

    public void UpdateOverlay(double fps)
    {
        if (Overlay is { IsVisible: true }) Overlay.UpdateFps(fps);
    }

    /// <summary>
    /// Dimensiona a janela dentro da area util do ecra. <see cref="SystemParameters.WorkArea"/>
    /// ja vem em DIPs, portanto a escala/DPI do Windows e' considerada automaticamente.
    /// Sem isto, telas pequenas com escala alta (ex.: 1366x768 a 150%) abriam a janela fora do ecra.
    /// </summary>
    private void FitWindowToScreen()
    {
        var area = SystemParameters.WorkArea;
        var maxW = Math.Max(640, area.Width - 24);
        var maxH = Math.Max(420, area.Height - 24);

        // Minimos nunca excedem o espaco real disponivel.
        MinWidth = Math.Min(1200, maxW);
        MinHeight = Math.Min(740, maxH);

        // Tamanho inicial cabe sempre na area util (o conteudo tem scroll).
        Width = Math.Min(1280, maxW);
        Height = Math.Min(860, maxH);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);

        ApplyResponsiveLayout(Width);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout(ActualWidth);

    /// <summary>Impede que a janela fique maior que a area util apos mudanca de escala/DPI.</summary>
    private void ClampToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        var maxW = Math.Max(640, area.Width - 24);
        var maxH = Math.Max(420, area.Height - 24);
        MinWidth = Math.Min(1200, maxW);
        MinHeight = Math.Min(740, maxH);
        if (Width > maxW) Width = maxW;
        if (Height > maxH) Height = maxH;
        ApplyResponsiveLayout(ActualWidth > 0 ? ActualWidth : Width);
    }

    /// <summary>Ajusta a barra lateral e o cabecalho conforme a largura disponivel.</summary>
    private void ApplyResponsiveLayout(double width)
    {
        // Compact <768, Medium <1200, Wide >=1200
        var compact = width < 768;
        var medium = width < 1200;

        if (_sideColumn != null)
            _sideColumn.Width = new GridLength(compact ? 150 : medium ? 186 : 214);

        if (_headerVersion != null)
            _headerVersion.Visibility = width >= 1024 ? Visibility.Visible : Visibility.Collapsed;

        if (_headerMeters != null)
            _headerMeters.Visibility = width >= 1000 ? Visibility.Visible : Visibility.Collapsed;
    }
}
