using System.Windows;
using System.Windows.Controls;

namespace MagoDaPerformance;

public sealed class NetworkPage : PageBase
{
    private readonly TextBlock _info;
    private readonly TextBlock _ping;
    private readonly TextBlock _speed;
    private readonly TextBlock _diag;
    private readonly TextBlock _dnsRanking;
    private readonly TextBlock _dnsStatus;
    private readonly ListBox _dnsChoices;
    private readonly Button _applyDns;
    private NetRoute _route = new("", null, new List<string>(), null);
    private List<DnsResult> _results = new();

    public NetworkPage(MainWindow host) : base(host, "Rede",
        "Mede ping, download, upload e a latencia dos resolvedores DNS.")
    {
        var infoCard = Card("ADAPTADOR", 400);
        var infoBody = BodyOf(infoCard);
        _info = Ui.Mono("A ler a rede...", 11, Ui.Txt);
        infoBody.Children.Add(_info);
        infoBody.Children.Add(Ui.Wrap(
            Ui.Btn("Atualizar", RefreshRoute),
            Ui.Btn("Limpar DNS", () => Host.Engine.FlushDns())));

        var testCard = Card("TESTES", 620);
        var testBody = BodyOf(testCard);
        _ping = Ui.Lbl("Ping: ainda nao medido.", 12, Ui.Txt);
        _speed = Ui.Lbl("Velocidade: ainda nao medida.", 12, Ui.Txt);
        _diag = Ui.Lbl("", 12, Ui.Gold);
        testBody.Children.Add(Ui.Wrap(
            Ui.Btn("Ping", RunPing),
            Ui.Btn("Velocidade", RunSpeed),
            Ui.Btn("TESTE COMPLETO", RunFull, primary: true)));
        testBody.Children.Add(_ping);
        testBody.Children.Add(_speed);
        testBody.Children.Add(_diag);

        var dnsCard = Card("DNS - MELHOR RESOLVEDOR", 620);
        var dnsBody = BodyOf(dnsCard);
        _dnsRanking = Ui.Mono("Corre o benchmark para comparar Cloudflare, Google, Quad9, OpenDNS, AdGuard e o DNS atual.", 11, Ui.Txt);
        _dnsStatus = Ui.Lbl("", 11, Ui.Dim);
        _dnsChoices = Ui.List(120);
        _applyDns = Ui.Btn("APLICAR DNS", ApplyDns, primary: true);
        dnsBody.Children.Add(Ui.Wrap(
            Ui.Btn("BENCHMARK DNS", RunDnsBenchmark),
            Ui.Btn("Automatico (DHCP)", ApplyAutoDns)));
        dnsBody.Children.Add(_dnsRanking);
        dnsBody.Children.Add(_dnsStatus);
        dnsBody.Children.Add(_dnsChoices);
        dnsBody.Children.Add(Ui.Wrap(_applyDns));

        Add(Row(infoCard, testCard));
        Add(Row(dnsCard));
    }

    private static StackPanel BodyOf(Border card) => (StackPanel)((StackPanel)card.Child).Children[1];

    private void RefreshRoute()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            var route = Net.Route();
            Host.Dispatcher.Invoke(() =>
            {
                _route = route;
                _info.Text =
                    $"Adaptador: {(string.IsNullOrEmpty(route.Adapter) ? "nao detetado" : route.Adapter)}\n" +
                    $"Gateway: {route.Gateway ?? "—"}\n" +
                    $"DNS atuais: {(route.Dns.Count > 0 ? string.Join(", ", route.Dns) : "automatico")}\n" +
                    $"Link: {route.Link ?? "—"}";
            });
        });
    }

    private void RunPing()
    {
        Host.RunTask(() =>
        {
            var gateway = _route.Gateway;
            var local = gateway != null ? Net.PingHost(gateway) : null;
            var internet = Net.PingHost("1.1.1.1");
            Host.Dispatcher.Invoke(() =>
            {
                _ping.Text = $"Ping internet (1.1.1.1): {internet}\n" +
                             $"Ping router: {(local?.ToString() ?? "sem gateway")}";
                _diag.Text = Net.Diagnose(internet, local, null, null);
            });
            return null;
        }, "A medir ping...");
    }

    private void RunSpeed()
    {
        Host.RunTask(() =>
        {
            var (downBytes, downSeconds) = Net.MeasureDownload();
            var downBps = Net.BitsPerSecond(downBytes, downSeconds);
            var (upBytes, upSeconds) = Net.MeasureUpload();
            var upBps = Net.BitsPerSecond(upBytes, upSeconds);
            Host.Dispatcher.Invoke(() =>
            {
                _speed.Text = $"Download: {Net.FormatMbps(downBps)}\nUpload: {Net.FormatMbps(upBps)}";
                _diag.Text = Net.Diagnose(null, null, downBps, upBps);
            });
            return null;
        }, "A medir velocidade (aprox. 30 MB)...");
    }

    private void RunFull()
    {
        Host.RunTask(() =>
        {
            var route = Net.Route();
            var local = route.Gateway != null ? Net.PingHost(route.Gateway) : null;
            var internet = Net.PingHost("1.1.1.1");
            var (downBytes, downSeconds) = Net.MeasureDownload();
            var downBps = Net.BitsPerSecond(downBytes, downSeconds);
            var (upBytes, upSeconds) = Net.MeasureUpload();
            var upBps = Net.BitsPerSecond(upBytes, upSeconds);
            Host.Dispatcher.Invoke(() =>
            {
                _route = route;
                _info.Text =
                    $"Adaptador: {(string.IsNullOrEmpty(route.Adapter) ? "nao detetado" : route.Adapter)}\n" +
                    $"Gateway: {route.Gateway ?? "—"}\n" +
                    $"DNS atuais: {(route.Dns.Count > 0 ? string.Join(", ", route.Dns) : "automatico")}\n" +
                    $"Link: {route.Link ?? "—"}";
                _ping.Text = $"Ping internet (1.1.1.1): {internet}\n" +
                             $"Ping router: {(local?.ToString() ?? "sem gateway")}";
                _speed.Text = $"Download: {Net.FormatMbps(downBps)}    Upload: {Net.FormatMbps(upBps)}";
                _diag.Text = Net.Diagnose(internet, local, downBps, upBps);
            });
            return null;
        }, "Teste completo de rede...");
    }

    private void RunDnsBenchmark()
    {
        Host.RunTask(() =>
        {
            var route = Net.Route();
            var current = route.Dns.FirstOrDefault();
            var results = Dns.Benchmark(Dns.Candidates(current));
            Host.Dispatcher.Invoke(() =>
            {
                _results = results;
                _dnsRanking.Text = Dns.Ranking(results);
                _dnsStatus.Text = Dns.Recommendation(results);
                _dnsChoices.Items.Clear();
                foreach (var choice in Dns.Choices(results)) _dnsChoices.Items.Add(choice);
                if (_dnsChoices.Items.Count > 0) _dnsChoices.SelectedIndex = 1;
            });
            return null;
        }, "Benchmark de DNS (aprox. 6 s)...");
    }

    private void ApplyDns()
    {
        var choice = _dnsChoices.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(choice))
        {
            Host.Info("Escolhe um resolvedor na lista.", "DNS");
            return;
        }
        var provider = Dns.ChoiceToProvider(choice, _results);
        if (provider == null)
        {
            ApplyAutoDns();
            return;
        }
        var alias = _route.Adapter;
        if (string.IsNullOrEmpty(alias))
        {
            Host.Info("Nao foi possivel identificar o adaptador ativo.", "DNS");
            return;
        }
        if (!Host.Confirm($"Aplicar {provider.Name} ({provider.Primary}) em '{alias}'?", "Aplicar DNS")) return;

        Host.RunTask(() =>
        {
            foreach (var command in Dns.ChangeCommands(alias, provider.Primary, provider.Secondary))
            {
                var args = "interface " + string.Join(' ', command);
                var (ok, output) = Proc.Run("netsh.exe", args);
                Log.Write(ok ? $"netsh {args}" : $"netsh falhou: {output}", ok ? "INFO" : "ERRO");
            }
            Proc.Run("ipconfig.exe", "/flushdns");
            Host.Dispatcher.Invoke(RefreshRoute);
            return "DNS aplicado. Reinicia o browser para usar o novo resolvedor.";
        }, "A aplicar DNS...");
    }

    private void ApplyAutoDns()
    {
        var alias = _route.Adapter;
        if (string.IsNullOrEmpty(alias))
        {
            Host.Info("Nao foi possivel identificar o adaptador ativo.", "DNS");
            return;
        }
        if (!Host.Confirm($"Voltar o DNS de '{alias}' para automatico (DHCP)?", "DNS automatico")) return;

        Host.RunTask(() =>
        {
            foreach (var command in Dns.ChangeCommands(alias, null))
                Proc.Run("netsh.exe", "interface " + string.Join(' ', command));
            Proc.Run("ipconfig.exe", "/flushdns");
            Host.Dispatcher.Invoke(RefreshRoute);
            return "DNS voltou a automatico (DHCP).";
        }, "A restaurar DNS automatico...");
    }
}
