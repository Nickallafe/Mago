using System.Windows;
using System.Windows.Controls;

namespace MagoDaPerformance;

public sealed class ToolsPage : PageBase
{
    private readonly ListBox _startup;
    private readonly TextBlock _startupStatus;
    private bool _loaded;

    public ToolsPage(MainWindow host) : base(host, "Ferramentas",
        "Utilitarios de manutencao e controlo dos programas de arranque.")
    {
        var utils = Card("UTILITARIOS", 360);
        var utilsBody = BodyOf(utils);
        utilsBody.Children.Add(Ui.Wrap(
            Ui.Btn("Efeitos visuais: desempenho", () => Host.RunTask(() => { Host.Engine.VisualEffectsPerf(); return null; }, "Efeitos visuais...")),
            Ui.Btn("Limpar cache de shaders", () => Host.RunTask(() => { Host.Engine.CleanShaderCache(); return null; }, "A limpar shaders...")),
            Ui.Btn("Desativar arranque rapido", () => Host.RunTask(() => { Host.Engine.DisableFastStartup(); return "Entra em vigor no proximo desligar."; }, "Arranque rapido...")),
            Ui.Btn("Limpar DNS", () => Host.Engine.FlushDns()),
            Ui.Btn("Reiniciar Explorer", () =>
            {
                if (Host.Confirm("Reiniciar o Explorador de Ficheiros?", "Explorer")) Host.Engine.RestartExplorer();
            }),
            Ui.Btn("Abrir log", () => Proc.OpenUri(Paths.LogFile)),
            Ui.Btn("Abrir pasta de dados", () => Proc.OpenUri(Paths.DataDir))));

        var startupCard = Card("PROGRAMAS DE ARRANQUE", 600);
        var startupBody = BodyOf(startupCard);
        _startup = Ui.TextList(240);
        _startupStatus = Ui.Lbl("", 11, Ui.Dim);
        startupBody.Children.Add(Ui.Wrap(
            Ui.Btn("Listar", LoadStartup),
            Ui.Btn("Desativar selecionado", DisableStartup)));
        startupBody.Children.Add(_startup);
        startupBody.Children.Add(_startupStatus);

        Add(Row(utils, startupCard));
    }

    public override void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        LoadStartup();
    }

    private static StackPanel BodyOf(Border card) => (StackPanel)((StackPanel)card.Child).Children[1];

    private void LoadStartup()
    {
        _startupStatus.Text = "A ler programas de arranque...";
        System.Threading.Tasks.Task.Run(() =>
        {
            var items = Software.StartupItems();
            Host.Dispatcher.Invoke(() =>
            {
                _startup.Items.Clear();
                foreach (var item in items) _startup.Items.Add(item);
                _startupStatus.Text = items.Count == 0
                    ? "Nenhum programa nas chaves Run do utilizador e do computador."
                    : $"{items.Count} programas de arranque encontrados.";
                // Altura acompanha o numero de itens quando cabem todos.
                _startup.Height = items.Count <= 8 ? Math.Max(items.Count * 32, 40) : 260;
            });
        });
    }

    private void DisableStartup()
    {
        if (_startup.SelectedItem is not Software.StartupItem item)
        {
            Host.Info("Seleciona um item na lista.", "Arranque");
            return;
        }
        if (!Host.Confirm($"Desativar {item.Name}?", "Arranque")) return;

        Host.RunTask(() =>
        {
            Software.DisableStartup(item, Host.Store);
            LoadStartup();
            return null;
        }, "A desativar arranque...");
    }
}
