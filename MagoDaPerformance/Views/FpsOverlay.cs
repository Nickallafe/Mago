using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

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
        base.OnClosed(e);
        SavePosition();
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

    private const uint NearestMonitor = 2;

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
