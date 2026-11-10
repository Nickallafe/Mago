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
        // Sem "Limpar DNS" nem "Abrir log": ja' existem em Rede e na barra lateral, e repetir
        // a mesma acao em dois sitios so' faz duvidar de qual e' o certo.
        var utils = Card("UTILITARIOS DO WINDOWS", 360);
        var utilsBody = BodyOf(utils);
        utilsBody.Children.Add(Ui.Wrap(
            Ui.Btn("Efeitos visuais: desempenho", () => Host.RunTask(() => { Host.Engine.VisualEffectsPerf(); return null; }, "Efeitos visuais...")),
            Ui.Btn("Limpar cache de shaders", ClearShaderCache),
            Ui.Btn("Desativar arranque rapido", () => Host.RunTask(() => { Host.Engine.DisableFastStartup(); return "Entra em vigor no proximo desligar."; }, "Arranque rapido...")),
            Ui.Btn("Reiniciar Explorer", () =>
            {
                if (Host.Confirm("Reiniciar o Explorador de Ficheiros?", "Explorer")) Host.Engine.RestartExplorer();
            }),
            Ui.Btn("Abrir pasta de dados", () => Proc.OpenUri(Paths.DataDir))));

        var startupCard = Card("PROGRAMAS DE ARRANQUE", 970);
        var startupBody = BodyOf(startupCard);
        _startup = Ui.TextList(240);
        _startupStatus = Ui.Lbl("", 11, Ui.Dim);
        startupBody.Children.Add(Ui.Wrap(
            Ui.Btn("Listar programas", LoadStartup),
            Ui.Btn("Desativar selecionado", DisableStartup)));
        startupBody.Children.Add(_startup);
        startupBody.Children.Add(_startupStatus);

        var updatesCard = Card("ATUALIZACOES DO MAGO", 360);
        var updatesBody = BodyOf(updatesCard);
        updatesBody.Children.Add(Ui.Lbl($"Versao atual: {Updater.DisplayVersion}", 11, Ui.Dim));
        var autoUpdate = new CheckBox
        {
            Content = "Atualizar automaticamente ao abrir",
            IsChecked = Host.Settings.AutoUpdate,
            Foreground = Ui.Br(Ui.Txt),
            Margin = new Thickness(0, 2, 0, 8),
        };
        autoUpdate.Checked += (_, _) => SetAutoUpdate(true);
        autoUpdate.Unchecked += (_, _) => SetAutoUpdate(false);
        updatesBody.Children.Add(autoUpdate);
        updatesBody.Children.Add(Ui.Wrap(
            Ui.Btn("Verificar agora", () => Host.CheckForUpdates(silent: false)),
            Ui.Btn("Abrir releases", () => Proc.OpenUri($"https://github.com/{Updater.Repository}/releases"))));

        // Utilitarios e atualizacoes sao curtos e ficam lado a lado; a lista de arranque,
        // que e' alta, ocupa a linha inteira por baixo (antes deixava um buraco ao lado).
        Add(Row(utils, updatesCard));
        Add(Row(startupCard));
    }

    /// <summary>A cache volta a ser compilada pelos jogos: o primeiro arranque fica mais lento.</summary>
    private void ClearShaderCache()
    {
        if (!Host.Confirm(
                "Apagar as caches de shaders de GPU?\n\n" +
                "O primeiro arranque de cada jogo depois disto fica mais lento enquanto compila os " +
                "shaders outra vez. Nao aumenta FPS.",
                "Cache de shaders"))
            return;

        Host.RunTask(() => { Host.Engine.CleanShaderCache(); return null; }, "A limpar shaders...");
    }

    private void SetAutoUpdate(bool enabled)
    {
        Host.Settings.AutoUpdate = enabled;
        Host.Settings.Save();
        Host.Status(enabled ? "Atualizacao automatica ligada." : "Atualizacao automatica desligada.");
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
