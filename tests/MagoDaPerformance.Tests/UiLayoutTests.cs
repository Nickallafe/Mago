using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MagoDaPerformance;

namespace MagoTests;

/// <summary>
/// WPF exige STA e um dispatcher proprio: o xUnit corre em MTA, por isso o trabalho de UI
/// passa todo por este thread unico. Nao ha janelas visiveis nem chamadas de rede.
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Shared = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Run(Action action)
    {
        Exception? error = null;
        Shared.Value.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            // Uma Application por processo: fica viva e o dispatcher roda em background.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
            GC.KeepAlive(app);
        })
        {
            IsBackground = true,
            Name = "mago-ui-tests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}

/// <summary>
/// Layout real das paginas: mede a arvore visual a 1280x800 e garante que os cartoes
/// novos existem e que nada fica mais largo do que a pagina.
/// </summary>
public class UiLayoutTests
{
    private const double PageWidth = 1280;
    private const double PageHeight = 800;

    [Fact]
    public void Painel_tem_diagnostico()
    {
        UiThread.Run(() =>
        {
            using var window = new TestWindow();
            AssertPage(window.Window, "painel", "DIAGNOSTICO RAPIDO");
        });
    }

    [Fact]
    public void Jogos_tem_fps_e_medicao()
    {
        UiThread.Run(() =>
        {
            using var window = new TestWindow();
            // FPS, overlay e antes/depois sairam do painel: vivem na pagina Jogos.
            AssertPage(window.Window, "jogos", "FPS DO JOGO");
            AssertPage(window.Window, "jogos", "MEDIR ANTES / DEPOIS");
        });
    }

    [Fact]
    public void Otimizar_tem_desfazer_por_modulo()
    {
        UiThread.Run(() =>
        {
            using var window = new TestWindow();
            AssertPage(window.Window, "otimizar", "DESFAZER UM MODULO");
        });
    }

    [Fact]
    public void Rede_tem_latencia_sob_carga()
    {
        UiThread.Run(() =>
        {
            using var window = new TestWindow();
            AssertPage(window.Window, "rede", "Latencia sob carga");
        });
    }

    [Fact]
    public void Paginas_cabem_numa_janela_estreita()
    {
        UiThread.Run(() =>
        {
            using var window = new TestWindow();
            // 1366x768 e' o pior caso comum; a area util real fica abaixo de 900 DIPs.
            AssertNoOverflow(window.Window, "painel", 900);
            AssertNoOverflow(window.Window, "otimizar", 900);
            AssertNoOverflow(window.Window, "jogos", 900);
            AssertNoOverflow(window.Window, "rede", 900);
            AssertNoOverflow(window.Window, "ferramentas", 900);
        });
    }

    /// <summary>
    /// A mesma acao em duas paginas diferentes faz duvidar de qual e' a certa. Cada uma
    /// tem de ter um unico dono.
    /// </summary>
    [Fact]
    public void Acoes_repetidas_tem_uma_so_pagina()
    {
        UiThread.Run(() =>
        {
            using var window = new TestWindow();
            var keys = new[] { "painel", "otimizar", "jogos", "rede", "apps", "backup", "ferramentas" };
            foreach (var action in new[] { "Limpar cache DNS", "Reiniciar Explorer", "Restaurar backup Mago" })
            {
                var pages = keys
                    .Where(key => VisibleTexts(Show(window.Window, key)).Any(t => t == action))
                    .ToList();
                Assert.True(pages.Count == 1,
                    $"'{action}' aparece em {pages.Count} paginas: {string.Join(", ", pages)}");
            }
        });
    }

    /// <summary>
    /// Medir tem de ser um clique: o jogo vem do primeiro plano, nao de uma lista, e o overlay
    /// liga-se com a medicao. A escolha manual fica escondida nas opcoes avancadas.
    /// </summary>
    [Fact]
    public void Jogos_mede_o_jogo_em_foco_num_so_clique()
    {
        UiThread.Run(() =>
        {
            using var window = new TestWindow();
            AssertPage(window.Window, "jogos", "Medir o jogo em foco");
            AssertPage(window.Window, "jogos", "Ligar overlay");
            AssertPage(window.Window, "jogos", "Mostrar opcoes avancadas");
        });
    }

    /// <summary>
    /// O overlay tem de ser uma janela "invisivel" para o resto do sistema: por cima, fora
    /// da barra de tarefas e sem roubar o foco (roubar o foco minimiza jogos em ecra inteiro).
    /// </summary>
    [Fact]
    public void Overlay_e_sempre_por_cima_e_nao_rouba_o_foco()
    {
        UiThread.Run(() =>
        {
            var overlay = new FpsOverlay(new AppSettings());
            Assert.True(overlay.Topmost);
            Assert.False(overlay.ShowInTaskbar);
            Assert.False(overlay.ShowActivated);
            Assert.False(overlay.Focusable);
            Assert.Equal(ResizeMode.NoResize, overlay.ResizeMode);
        });
    }

    /// <summary>
    /// O BIOS deixou de ser uma pagina propria: a chave antiga tem de continuar a abrir
    /// a visao geral (onde ficou o cartao do firmware) e nao uma pagina vazia.
    /// </summary>
    [Fact]
    public void Bios_abre_a_visao_geral_com_o_cartao_do_firmware()
    {
        UiThread.Run(() =>
        {
            using var window = new TestWindow();
            AssertPage(window.Window, "bios", "BIOS E FIRMWARE");
            AssertPage(window.Window, "firmware", "Secure Boot");
        });
    }

    private static void AssertPage(MainWindow window, string key, string expectedText)
    {
        var page = Show(window, key);
        Assert.True(page.ActualWidth > 0, $"{key}: pagina sem largura");

        var texts = VisibleTexts(page).ToList();
        Assert.True(texts.Any(t => t.Contains(expectedText, StringComparison.Ordinal)),
            $"{key}: nao encontrei '{expectedText}'. Textos: {string.Join(" | ", texts.Take(25))}");
    }

    private static void AssertNoOverflow(MainWindow window, string key, double width)
    {
        var page = Show(window, key, width);
        var tooWide = new List<string>();

        foreach (var element in Descendants(page))
        {
            if (element is not FrameworkElement fe) continue;
            if (fe.Visibility != Visibility.Visible) continue;
            if (double.IsNaN(fe.ActualWidth) || fe.ActualWidth <= 0) continue;
            if (fe.ActualWidth <= width + 1) continue;
            tooWide.Add($"{fe.GetType().Name} '{FirstText(fe)}' = {fe.ActualWidth:0} px");
        }

        Assert.True(tooWide.Count == 0,
            $"{key} a {width:0} px: elementos mais largos que a pagina: {string.Join("; ", tooWide.Take(5))}");
    }

    private static FrameworkElement Show(MainWindow window, string key, double width = PageWidth)
    {
        window.ShowPage(key);
        var page = window.CurrentPageContent as FrameworkElement;
        Assert.NotNull(page);

        page!.Measure(new Size(width, PageHeight));
        page.Arrange(new Rect(0, 0, width, PageHeight));
        page.UpdateLayout();
        return page;
    }

    private static IEnumerable<string> VisibleTexts(DependencyObject root) =>
        Descendants(root)
            .Select(FirstText)
            .Where(t => t.Length > 0);

    private static string FirstText(DependencyObject element) => element switch
    {
        TextBlock tb => (tb.Text ?? "").Trim(),
        ContentControl cc when cc.Content is string s => s.Trim(),
        _ => "",
    };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    /// <summary>Janela criada fora da tela, sem efeitos colaterais de pagina, fechada no fim.</summary>
    private sealed class TestWindow : IDisposable
    {
        public MainWindow Window { get; }

        public TestWindow()
        {
            Window = new MainWindow
            {
                SkipPageSideEffects = true,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            // Sem atualizacao automatica: os testes nao devem tocar na rede depois de 4 s.
            Window.Settings.AutoUpdate = false;
        }

        public void Dispose()
        {
            try { Window.Close(); } catch { }
        }
    }
}
