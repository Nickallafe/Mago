using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MagoDaPerformance;

/// <summary>
/// Contador de FPS sempre por cima. Abre no canto superior direito da area util
/// (ja sem barra de tarefas) do ecra onde esta' o rato, cresce para a esquerda
/// quando o numero ganha digitos e nunca fica fora do ecra: corrige-se em
/// multimonitor, em mudancas de escala/DPI e depois de ser arrastado.
/// </summary>
public sealed class FpsOverlay : Window
{
    private const double Edge = 24;

    private readonly TextBlock _text;
    private readonly Border _badge;
    private readonly AppSettings _settings;
    private double _opacity;
    private double _fontSize;

    // Ancorado ao canto superior direito: o badge nao "anda" quando o FPS muda de largura.
    private bool _anchoredRight = true;
    private double _rightEdge = double.NaN;

    // O jogo tambem se declara "sempre por cima": quem chegou por ultimo ao topo da
    // pilha ganha, por isso o overlay tem de reafirmar o lugar depois de o jogo ganhar foco.
    public const int TopmostIntervalMs = 700;
    private DispatcherTimer? _keeper;

    public FpsOverlay(AppSettings settings)
    {
        _settings = settings;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        // Nao roubar o foco do jogo ao ligar o overlay (causava minimizar/perda de foco).
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _opacity = Math.Clamp(settings.OverlayOpacity, MinOpacity, 1);
        _fontSize = Math.Clamp(settings.OverlayFontSize, MinFontSize, MaxFontSize);

        _text = new TextBlock
        {
            Text = "FPS --",
            FontFamily = new FontFamily("Consolas"),
            FontWeight = FontWeights.Bold,
            Foreground = Ui.Br(Ui.Green),
            Margin = new Thickness(14, 8, 14, 8),
        };

        _badge = new Border
        {
            BorderBrush = Ui.Br(Ui.Green),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = _text,
            Cursor = Cursors.SizeAll,
        };
        _badge.MouseLeftButtonDown += OnBadgeMouseDown;
        Content = _badge;
        ApplyLook();

        // Ferramenta sem ativacao: nao aparece no Alt+Tab e nao rouba o foco ao jogo.
        SourceInitialized += (_, _) => MakeToolWindow();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) ReassertTopmost();
        };

        SizeChanged += OnOverlaySizeChanged;
        DpiChanged += (_, _) =>
        {
            // Ao cair noutro monitor a escala muda: recalcula a posicao nesse ecra.
            if (_anchoredRight) AnchorTopRight();
            else KeepInsideScreen();
        };
    }

    public const double MinOpacity = 0.15;
    public const double MinFontSize = 12;
    public const double MaxFontSize = 48;

    /// <summary>Opacidade do fundo do badge (0.15 a 1). Aplicada ao vivo.</summary>
    public double BadgeOpacity
    {
        get => _opacity;
        set { _opacity = Math.Clamp(value, MinOpacity, 1); ApplyLook(); }
    }

    /// <summary>Tamanho do texto em DIPs (12 a 48). Aplicado ao vivo.</summary>
    public double BadgeFontSize
    {
        get => _fontSize;
        set { _fontSize = Math.Clamp(value, MinFontSize, MaxFontSize); ApplyLook(); }
    }

    /// <summary>
    /// Volta ao aspeto e a posicao de fabrica: opacidade/tamanho padrao e canto superior
    /// direito do ecra onde esta' o rato. Nao grava nada; quem chama decide quando guardar.
    /// </summary>
    public void ResetToDefaults()
    {
        _opacity = AppSettings.DefaultOverlayOpacity;
        _fontSize = AppSettings.DefaultOverlayFontSize;
        ApplyLook();
        PlaceOnCursorScreen();
    }

    private void ApplyLook()
    {
        _text.FontSize = _fontSize;
        _badge.Background = Ui.Br(Color.FromRgb(0x05, 0x07, 0x0B), _opacity);
    }

    public void UpdateFps(double fps)
    {
        _text.Text = fps > 0 ? $"FPS {fps:0}" : "FPS --";
        _text.Foreground = Ui.Br(fps >= 60 ? Ui.Green : fps >= 30 ? Ui.Gold : Ui.Red);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!double.IsNaN(_rightEdge)) return;

        // Retoma a posicao da ultima sessao; se o monitor desapareceu, encaixa no ecra atual.
        if (RestoreSavedPosition()) KeepInsideScreen();
        else PlaceOnCursorScreen();
    }

    protected override void OnClosed(EventArgs e)
    {
        _keeper?.Stop();
        _keeper = null;
        base.OnClosed(e);
        SavePosition();
    }

    /// <summary>
    /// Marca a janela como ferramenta (fora do Alt+Tab) sem ativacao e comeca a reafirmar o
    /// topo. Uma janela topmost normal perde o lugar para o jogo, que tambem pede o topo ao
    /// entrar em ecra inteiro; reafirmar em segundo plano devolve-a acima sem tirar o foco.
    /// </summary>
    private void MakeToolWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var style = GetExStyle(handle);
        SetExStyle(handle, style | WsExToolWindow | WsExNoActivate);
        ReassertTopmost();

        _keeper ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(TopmostIntervalMs),
            DispatcherPriority.Background,
            (_, _) => ReassertTopmost(),
            Dispatcher);
        _keeper.Start();
    }

    /// <summary>
    /// Pede o topo sem mexer no tamanho, posicao ou foco. Nota: em "tela cheia exclusiva"
    /// nenhuma janela do Windows aparece por cima do jogo - so' em "sem bordas" ou janela.
    /// </summary>
    public void ReassertTopmost()
    {
        if (!IsVisible) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    /// <summary>
    /// Leva o badge para o canto do monitor onde o jogo esta' em ecra inteiro. Ignora o pedido
    /// quando o utilizador ja' o arrastou para outro sitio: a escolha dele manda.
    /// </summary>
    public void FollowMonitor(IntPtr monitor)
    {
        if (!_anchoredRight || monitor == IntPtr.Zero || !IsVisible) return;

        var area = WorkArea(monitor);
        if (area.IsEmpty) return;

        UpdateLayout();
        _rightEdge = area.Right - Edge;
        Left = Math.Max(area.Left + Edge, _rightEdge - ActualWidth);
        Top = area.Top + Edge;
    }

    /// <summary>
    /// O overlay esta' abaixo de <paramref name="other"/> na pilha de janelas? E' este teste
    /// que identifica a "tela cheia exclusiva": nesse modo o Windows ignora o pedido de topo,
    /// por isso o overlay continua atras do jogo mesmo depois de o reafirmar.
    /// </summary>
    public bool IsBehind(IntPtr other)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || other == IntPtr.Zero || other == handle || !IsVisible) return false;

        // GW_HWNDPREV anda para a janela imediatamente acima na ordem Z.
        for (var above = GetWindow(handle, GwHwndPrev); above != IntPtr.Zero; above = GetWindow(above, GwHwndPrev))
            if (above == other) return true;
        return false;
    }

    private bool RestoreSavedPosition()
    {
        if (_settings.OverlayLeft is not { } left || _settings.OverlayTop is not { } top) return false;
        if (double.IsNaN(left) || double.IsNaN(top) || double.IsInfinity(left) || double.IsInfinity(top)) return false;

        _anchoredRight = false;
        Left = left;
        Top = top;
        return true;
    }

    private void SavePosition()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;

        _settings.OverlayLeft = Left;
        _settings.OverlayTop = Top;
        _settings.Save();
    }

    /// <summary>Duplo clique (ou primeira abertura) reposiciona no ecra onde esta' o rato.</summary>
    private void PlaceOnCursorScreen()
    {
        UpdateLayout();
        _anchoredRight = true;
        var area = WorkArea(MonitorAtCursor());
        if (area.IsEmpty) area = SystemParameters.WorkArea;

        _rightEdge = area.Right - Edge;
        Left = Math.Max(area.Left + Edge, _rightEdge - ActualWidth);
        Top = area.Top + Edge;
    }

    /// <summary>Encaixa no canto superior direito da area util do monitor atual.</summary>
    private void AnchorTopRight()
    {
        UpdateLayout();
        var area = WorkArea(MonitorOfWindow());
        if (area.IsEmpty) area = SystemParameters.WorkArea;

        _anchoredRight = true;
        _rightEdge = area.Right - Edge;
        Left = Math.Max(area.Left + Edge, _rightEdge - ActualWidth);
        Top = area.Top + Edge;
    }

    private void OnBadgeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            PlaceOnCursorScreen();
            SavePosition();
            return;
        }
        if (e.ButtonState != MouseButtonState.Pressed) return;

        DragMove(); // bloqueia ate' soltar o botao
        _anchoredRight = false; // posicao escolhida pelo utilizador
        KeepInsideScreen();
        SavePosition(); // grava logo, mesmo que a app seja fechada a forca depois
    }

    private void OnOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Largura mudou (FPS com mais/menos digitos): mantem a borda direita no lugar.
        if (_anchoredRight && !double.IsNaN(_rightEdge)) Left = _rightEdge - ActualWidth;
        KeepInsideScreen();
    }

    /// <summary>Garante que o badge fica inteiro dentro da area util do monitor onde esta'.</summary>
    private void KeepInsideScreen()
    {
        // Ainda sem posicao definida: o posicionamento acontece em OnContentRendered.
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;

        var area = WorkArea(MonitorOfWindow());
        if (area.IsEmpty) area = SystemParameters.WorkArea;
        if (area.IsEmpty) return;

        var maxLeft = Math.Max(area.Left, area.Right - ActualWidth);
        var maxTop = Math.Max(area.Top, area.Bottom - ActualHeight);
        Left = Math.Clamp(Left, area.Left, maxLeft);
        Top = Math.Clamp(Top, area.Top, maxTop);

        if (_anchoredRight) _rightEdge = Left + ActualWidth;
    }

    // ---- Win32: monitor e area util (WorkArea do WPF so' cobre a tela primaria) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr handle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr handle, int index, IntPtr value);

    private const uint NearestMonitor = 2;
    private const uint GwHwndPrev = 3;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTopmost = new(-1);

    private static long GetExStyle(IntPtr handle) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(handle, GwlExStyle).ToInt64() : GetWindowLong32(handle, GwlExStyle);

    private static void SetExStyle(IntPtr handle, long style)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(handle, GwlExStyle, new IntPtr(style));
        else SetWindowLong32(handle, GwlExStyle, unchecked((int)style));
    }

    private static IntPtr MonitorAtCursor() =>
        GetCursorPos(out var point) ? MonitorFromPoint(point, NearestMonitor) : IntPtr.Zero;

    private IntPtr MonitorOfWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        return handle == IntPtr.Zero ? MonitorAtCursor() : MonitorFromWindow(handle, NearestMonitor);
    }

    /// <summary>
    /// Area util do monitor (exclui a barra de tarefas) em DIPs. A conversao usa a escala
    /// atual da janela, exatamente a mesma que o WPF usa para aplicar Left/Top.
    /// </summary>
    private Rect WorkArea(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return Rect.Empty;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return Rect.Empty;

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        var work = info.Work;
        return new Rect(
            work.Left / scale,
            work.Top / scale,
            (work.Right - work.Left) / scale,
            (work.Bottom - work.Top) / scale);
    }
}
