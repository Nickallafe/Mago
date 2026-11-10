using System.Windows;
using System.Windows.Controls;

namespace MagoDaPerformance;

public sealed class NetworkPage : PageBase
{
    private readonly TextBlock _info;
    private readonly TextBlock _ping;
    private readonly TextBlock _speed;
    private readonly TextBlock _diag;
    private readonly TextBlock _bloat;
    private readonly TextBlock _dnsRanking;
    private readonly TextBlock _dnsStatus;
    private readonly ListBox _dnsChoices;
    private readonly Button _applyDns;
    private NetRoute _route = new("", null, new List<string>(), null);
    private List<DnsResult> _results = new();

    private bool _autoRunning;
    private DateTime _lastAuto = DateTime.MinValue;

    public NetworkPage(MainWindow host) : base(host, "Rede",
        "Ping, velocidade e DNS. A analise basica comeca sozinha ao abrir esta pagina.")
    {
        var infoCard = Card("ADAPTADOR", 380);
        var infoBody = BodyOf(infoCard);
        _info = Ui.Mono("A ler a rede...", 11, Ui.Txt);
        infoBody.Children.Add(_info);
        infoBody.Children.Add(Ui.Wrap(
            Ui.Btn("Analisar de novo", () => AutoAnalyse(force: true), primary: true),
            Ui.Btn("Limpar DNS", () => Host.Engine.FlushDns())));

        var testCard = Card("TESTES", 590);
        var testBody = BodyOf(testCard);
        _ping = Ui.Lbl("Ping: a medir...", 12, Ui.Txt);
        _speed = Ui.Lbl("Velocidade: ainda nao medida.", 12, Ui.Txt);
        _diag = Ui.Lbl("", 12, Ui.Gold);
        _bloat = Ui.Lbl("Latencia sob carga: ainda nao medida.", 12, Ui.Txt);
        testBody.Children.Add(Ui.Wrap(
            Ui.Btn("Ping", RunPing),
            Ui.Btn("Velocidade", RunSpeed),
            Ui.Btn("Latencia sob carga", RunBufferbloat),
            Ui.Btn("TESTE COMPLETO", RunFull)));
        testBody.Children.Add(_ping);
        testBody.Children.Add(_speed);
        testBody.Children.Add(_bloat);
        testBody.Children.Add(_diag);

        var dnsCard = Card("DNS - MELHOR RESOLVEDOR", 970);
        var dnsBody = BodyOf(dnsCard);
        dnsBody.Children.Add(Ui.Lbl(
            "Compara Cloudflare, Google, Quad9, OpenDNS, AdGuard e o DNS atual, e aplica o mais rapido.",
            11, Ui.Dim));
        _dnsRanking = Ui.Mono("", 11, Ui.Txt);
        _dnsStatus = Ui.Lbl("", 11, Ui.Dim);
        _dnsChoices = Ui.List(120);
        // So aparecem depois do benchmark: antes disso nao ha o que escolher.
        _dnsChoices.Visibility = Visibility.Collapsed;
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

    /// <summary>Ao entrar na pagina, faz logo a leitura basica da rede.</summary>
    public override void OnShown()
    {
        // Evita repetir tudo quando o utilizador troca de pagina e volta em seguida.
        if ((DateTime.Now - _lastAuto).TotalSeconds < 10) return;
        AutoAnalyse(force: false);
    }

    private void AutoAnalyse(bool force)
    {
        if (_autoRunning) return;
        if (!force && (DateTime.Now - _lastAuto).TotalSeconds < 10) return;
        _autoRunning = true;
        _lastAuto = DateTime.Now;
        _info.Foreground = Ui.Br(Ui.Txt);
        _info.Text = "A ler a rede...";
        _ping.Text = "Ping: a medir...";
        _diag.Text = "";

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var route = Net.Route();
                PingStats? gateway = null, internet = null;
                if (!string.IsNullOrEmpty(route.Adapter))
                {
                    if (!string.IsNullOrEmpty(route.Gateway)) gateway = Net.PingHost(route.Gateway!, 3, 1200);
                    internet = Net.PingHost("1.1.1.1", 3, 1200);
                }
                Host.Dispatcher.Invoke(() => ApplyRoute(route, gateway, internet));
            }
            catch (Exception ex)
            {
                Log.Write("Pre-analise de rede: " + ex.Message, "ERRO");
                Host.Dispatcher.Invoke(() => _ping.Text = "Ping: falhou a pre-analise.");
            }
            finally
            {
                Host.Dispatcher.Invoke(() => _autoRunning = false);
            }
        });
    }

    /// <summary>Atualiza os cartoes a partir de uma leitura de rede.</summary>
    private void ApplyRoute(NetRoute route, PingStats? gateway, PingStats? internet)
    {
        _route = route;
        if (string.IsNullOrEmpty(route.Adapter))
        {
            _info.Foreground = Ui.Br(Ui.Gold);
            _info.Text = "Sem ligacao de rede ativa.\nVerifique o cabo ou o Wi-Fi.";
            _ping.Text = "Ping: sem ligacao.";
            _diag.Text = "";
            return;
        }

        _info.Foreground = Ui.Br(Ui.Txt);
        _info.Text =
            $"Adaptador: {route.Adapter}\n" +
            $"Gateway: {route.Gateway ?? "—"}\n" +
            $"DNS atuais: {(route.Dns.Count > 0 ? string.Join(", ", route.Dns) : "automatico")}\n" +
            $"Link: {route.Link ?? "—"}";

        if (internet != null)
        {
            _ping.Text = $"Ping internet (1.1.1.1): {internet}\n" +
                         $"Ping router: {(gateway?.ToString() ?? "sem gateway")}";
            _diag.Text = Net.Diagnose(internet, gateway, null, null);
        }

        if (_results.Count == 0 && route.Dns.Count > 0)
            _dnsStatus.Text = $"DNS atual: {string.Join(", ", route.Dns)}. Clique em BENCHMARK DNS para comparar.";
    }

    private void RunPing()
    {
        Host.RunTask(() =>
        {
            var route = Net.Route();
            var local = route.Gateway != null ? Net.PingHost(route.Gateway) : null;
            var internet = Net.PingHost("1.1.1.1");
            Host.Dispatcher.Invoke(() => ApplyRoute(route, local, internet));
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
                ApplyRoute(route, local, internet);
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
                _dnsChoices.Visibility = Visibility.Visible;
                if (_dnsChoices.Items.Count > 1) _dnsChoices.SelectedIndex = 1;
            });
            return null;
        }, "Benchmark de DNS (aprox. 6 s)...");
    }

    private void ApplyDns()
    {
        if (_dnsChoices.SelectedItem is not string choice || string.IsNullOrWhiteSpace(choice))
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
            foreach (var args in Dns.ChangeCommands(alias, provider.Primary, provider.Secondary))
            {
                var (ok, output) = Proc.Run("netsh.exe", args);
                Log.Write(ok ? $"netsh {args}" : $"netsh falhou ({args}): {output}", ok ? "INFO" : "ERRO");
            }
            Proc.Run("ipconfig.exe", "/flushdns");
            Host.Dispatcher.Invoke(() => AutoAnalyse(force: true));
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
            foreach (var args in Dns.ChangeCommands(alias, null))
                Proc.Run("netsh.exe", args);
            Proc.Run("ipconfig.exe", "/flushdns");
            Host.Dispatcher.Invoke(() => AutoAnalyse(force: true));
            return "DNS voltou a automatico (DHCP).";
        }, "A restaurar DNS automatico...");
    }
    /// <summary>
    /// Bufferbloat: ping com a rede parada e com download a decorrer. E' o que explica
    /// lag em jogo quando alguem em casa comeca a ver video, mesmo com boa velocidade.
    /// </summary>
    private void RunBufferbloat()
    {
        _bloat.Text = "Latencia sob carga: a medir (cerca de 10 s)...";
        _bloat.Foreground = Ui.Br(Ui.Gold);
        Host.RunTask(() =>
        {
            var (idle, loaded) = Net.LatencyUnderLoad(_route.Gateway ?? "1.1.1.1");
            var summary = Net.BloatSummary(idle, loaded);
            if (Host.Dispatcher.HasShutdownStarted) return null;
            Host.Dispatcher.Invoke(() =>
            {
                _bloat.Text = summary;
                _bloat.Foreground = Ui.Br(loaded <= 0 ? Ui.Gold
                    : Net.BloatGrade(Net.BloatIncrease(idle, loaded)) is "A" or "B" ? Ui.Green : Ui.Red);
            });
            return null;
        }, "Latencia sob carga...");
    }

}
