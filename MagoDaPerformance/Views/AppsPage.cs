using System.Windows;
using System.Windows.Controls;

namespace MagoDaPerformance;

public sealed class AppsPage : PageBase
{
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private List<AppEntry> _all = new();
    private bool _loaded;

    public AppsPage(MainWindow host) : base(host, "Aplicativos instalados",
        "Pesquise o que esta instalado e remova o que nao usa. Use sempre a desinstalacao oficial.")
    {
        var card = Card("PROGRAMAS NO PC", 970);
        var body = BodyOf(card);

        _search = Ui.Input("Pesquisar por nome, fabricante ou tipo...", 420);
        _search.TextChanged += (_, _) => ApplyFilter();
        _status = Ui.Lbl("Ainda nao carregado.", 11, Ui.Dim);
        _list = Ui.TextList(420);

        body.Children.Add(Ui.Wrap(
            _search,
            Ui.Btn("Atualizar lista", () => LoadApps(true)),
            Ui.Btn("Desinstalar", Uninstall),
            Ui.Btn("Remocao forcada", ForceUninstall)));
        body.Children.Add(_list);
        body.Children.Add(_status);

        Add(Row(card));
    }

    /// <summary>A lista so e lida quando o utilizador abre a pagina.</summary>
    public override void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        LoadApps(false);
    }

    private static StackPanel BodyOf(Border card) => (StackPanel)((StackPanel)card.Child).Children[1];

    private void LoadApps(bool showBusy)
    {
        if (showBusy) Host.Busy(true, "A listar aplicativos...");
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var apps = Software.InstalledApps();
                Host.Dispatcher.Invoke(() =>
                {
                    _all = apps;
                    ApplyFilter();
                });
            }
            catch (Exception ex) { Log.Write(ex.Message, "ERRO"); }
            finally { Host.Dispatcher.Invoke(() => Host.Busy(false)); }
        });
    }

    private void ApplyFilter()
    {
        var query = (_search.Text ?? "").Trim().ToLowerInvariant();
        var visible = string.IsNullOrEmpty(query)
            ? _all
            : _all.Where(a => ($"{a.Name} {a.Publisher} {a.Type}").ToLowerInvariant().Contains(query)).ToList();

        _list.Items.Clear();
        foreach (var app in visible) _list.Items.Add(app);

        _status.Text = _all.Count == 0
            ? "Nenhum aplicativo encontrado. Tenta atualizar a lista."
            : visible.Count == 0
                ? $"Nenhum resultado para '{query}'."
                : $"A mostrar {visible.Count} de {_all.Count} aplicativos instalados.";
    }

    private AppEntry? Selected => _list.SelectedItem as AppEntry;

    private void Uninstall()
    {
        var app = Selected;
        if (app == null) { Host.Info("Seleciona um aplicativo na lista.", "Apps"); return; }
        if (!Host.Confirm($"Desinstalar {app.Name} pelo metodo oficial?", "Desinstalar")) return;

        Host.RunTask(() =>
        {
            Software.Uninstall(app, false, _ => false);
            RefreshInPlace();
            return null;
        }, "A desinstalar " + app.Name);
    }

    private void ForceUninstall()
    {
        var app = Selected;
        if (app == null) { Host.Info("Seleciona um aplicativo na lista.", "Apps"); return; }
        var message =
            $"{app.Name}\n\n1) tenta o desinstalador oficial\n2) remove a entrada do Programs and Features\n" +
            "3) se a pasta do programa for segura, pergunta se a apaga\n\nNao uses isto em componentes do Windows. Continuar?";
        if (!Host.Confirm(message, "Remocao forcada")) return;

        Host.RunTask(() =>
        {
            Software.Uninstall(app, true, folder => Host.Confirm("Apagar leftover?\n" + folder, "Apagar pasta"));
            RefreshInPlace();
            return null;
        }, "A remover " + app.Name);
    }

    private void RefreshInPlace()
    {
        try
        {
            var apps = Software.InstalledApps();
            Host.Dispatcher.Invoke(() => { _all = apps; ApplyFilter(); });
        }
        catch (Exception ex) { Log.Write(ex.Message, "ERRO"); }
    }
}
