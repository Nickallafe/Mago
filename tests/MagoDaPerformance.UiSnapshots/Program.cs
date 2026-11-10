using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MagoDaPerformance;

namespace MagoSnapshots;

/// <summary>
/// Renderiza as paginas da app para PNG, fora do ecra, sem rede nem alteracoes no sistema.
/// Serve para revisao visual do layout (o dotnet test so' valida invariantes).
///
/// Uso: dotnet run --project tests/MagoDaPerformance.UiSnapshots -c Release -- [pasta]
/// </summary>
internal static class Program
{
    private const double PageWidth = 1280;
    private const double MaxHeight = 5000;

    [STAThread]
    private static int Main(string[] args)
    {
        var output = args.Length > 0 ? args[0] : Path.Combine(FindRoot(), "ui-snapshots");
        Directory.CreateDirectory(output);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow
        {
            SkipPageSideEffects = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = PageWidth,
            Height = 820,
        };
        window.Settings.AutoUpdate = false;

        try
        {
            window.Show();
            Pump();

            var dump = args.Contains("--dump");
            foreach (var page in new[] { "painel", "otimizar", "rede" })
            {
                window.ShowPage(page);
                Pump();
                if (dump) Dump(window, page);

                var captured = Capture(window.CurrentPageContent, Path.Combine(output, page + ".png"));
                Console.WriteLine(captured
                    ? $"{page}.png ok"
                    : $"{page}.png falhou (pagina sem conteudo)");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("falhou: " + ex);
            return 4;
        }
        finally
        {
            try { window.Close(); } catch { }
            app.Shutdown();
        }

        Console.WriteLine("pasta: " + output);
        return 0;
    }

    /// <summary>
    /// Captura a pagina inteira (nao so' a parte visivel): mede o conteudo do ScrollViewer
    /// com altura livre e desenha o resultado num bitmap.
    /// </summary>
    private static bool Capture(UIElement? page, string path)
    {
        if (page == null) return false;

        // Desenha o conteudo interno, nao o ScrollViewer: ele recorta a area visivel e
        // deixaria a pagina inteira de fora (so' a primeira dobra aparecia).
        var content = page is ScrollViewer { Content: FrameworkElement inner } ? inner : page as FrameworkElement;
        if (content == null) return false;

        content.Measure(new Size(PageWidth, double.PositiveInfinity));
        var height = Math.Min(MaxHeight, Math.Max(120, content.DesiredSize.Height));
        content.Arrange(new Rect(0, 0, PageWidth, height));
        content.UpdateLayout();

        // O conteudo e' transparente: sem esta composicao o PNG sai branco.
        var element = new RenderTargetBitmap(
            (int)Math.Ceiling(PageWidth), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        element.Render(content);

        var frame = new DrawingVisual();
        using (var dc = frame.RenderOpen())
        {
            var area = new Rect(0, 0, PageWidth, height);
            dc.DrawRectangle(Ui.Br(Ui.Bg), null, area);
            dc.DrawRectangle(new ImageBrush(BitmapFrame.Create(element)), null, area);
        }
        var composed = new RenderTargetBitmap(
            (int)Math.Ceiling(PageWidth), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        composed.Render(frame);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(composed));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return true;
    }

    /// <summary>Diagnostico: lista os blocos grandes da pagina com posicao e cor de fundo.</summary>
    private static void Dump(Window window, string page)
    {
        Console.WriteLine($"--- {page} ---");
        if (window.Content is not FrameworkElement root) return;
        foreach (var element in Walk(root))
        {
            if (element is not FrameworkElement fe || fe.ActualWidth < 60 || fe.ActualHeight < 24) continue;
            var point = fe.TransformToAncestor(root).Transform(new Point(0, 0));
            var brush = (fe as Control)?.Background as SolidColorBrush
                        ?? (fe as Panel)?.Background as SolidColorBrush
                        ?? (fe as Border)?.Background as SolidColorBrush;
            var kind = fe.GetType().Name;
            if (kind is not ("ListBox" or "TextBox" or "Border" or "ResponsiveCard")) continue;
            var kids = fe is Border { Child: FrameworkElement child }
                ? $" childDesired={child.DesiredSize.Height:0} childActual={child.ActualHeight:0}"
                : "";
            Console.WriteLine($"  {kind,-14} x={point.X,6:0} y={point.Y,5:0} w={fe.ActualWidth,5:0} h={fe.ActualHeight,4:0} bg={(brush?.Color.ToString() ?? "-")}{kids}");
        }
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            foreach (var child in Walk(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    /// <summary>Deixa o dispatcher processar layout/timers antes de capturar.</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MagoDaPerformance.sln"))
               && !Directory.Exists(Path.Combine(dir.FullName, "MagoDaPerformance")))
            dir = dir.Parent;
        return dir?.FullName ?? Environment.CurrentDirectory;
    }
}
