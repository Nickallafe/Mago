using System.Windows;
using System.Windows.Controls;

namespace MagoDaPerformance;

public sealed class PanelPage : PageBase
{
    private readonly TextBlock _headline;
    private readonly TextBlock _vOs, _vCpu, _vGpu, _vRam, _vDisk;
    private readonly TextBlock _drvStatus;
    private readonly ListBox _drvList;
    private readonly TextBlock _ramLine, _cpuLine, _diskLine;
    private readonly TextBlock _biosLeft, _biosRight;
    private readonly ListBox _procList;
    private Dictionary<string, string?>? _biosInfo;
    private bool _biosLoaded, _biosLoading;
    private List<ProcessLoad> _procData = new();
    private bool _diagLoaded, _diagLoading;

    private bool _scanning;

    public PanelPage(MainWindow host) : base(host, "Visao geral",
        "O que o Mago encontrou no seu PC: hardware, drivers e o que pode estar a travar o desempenho.")
    {
        // ---------- o meu computador ----------
        var pc = Card("O MEU COMPUTADOR", 970);
        var pcBody = BodyOf(pc);
        _headline = Ui.Lbl("A ler o hardware...", 16, Ui.Txt, bold: true);
        _headline.Margin = new Thickness(0, 0, 0, 14);
        pcBody.Children.Add(_headline);

        var specs = new Grid();
        specs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        specs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) specs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _vOs = Spec(specs, 0, 0, "SISTEMA OPERATIVO");
        _vCpu = Spec(specs, 0, 1, "PROCESSADOR");
        _vGpu = Spec(specs, 1, 0, "GRAFICA");
        _vRam = Spec(specs, 1, 1, "MEMORIA");
        _vDisk = Spec(specs, 2, 0, "ARMAZENAMENTO");
        pcBody.Children.Add(specs);

        pcBody.Children.Add(Ui.Wrap(
            Ui.Btn("Atualizar informacoes", () => Host.RefreshHardware(), primary: true)));

        // ---------- firmware ----------
        // A BIOS e' a identidade da maquina (placa, UEFI, Secure Boot, TPM): cabe na
        // visao geral e deixa de justificar uma setima entrada no menu.
        var bios = Card("BIOS E FIRMWARE", 970);
        var biosBody = BodyOf(bios);
        biosBody.Children.Add(Ui.Lbl(
            "Placa-mae, versao do firmware, Secure Boot e TPM. Atualizacoes vem do fabricante, nao do Windows Update.",
            11, Ui.Dim));
        var biosGrid = new Grid { Margin = new Thickness(0, 8, 0, 4) };
        biosGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        biosGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _biosLeft = Ui.Mono("A analisar firmware...", 11, Ui.Txt);
        _biosRight = Ui.Mono("", 11, Ui.Txt);
        _biosRight.Margin = new Thickness(0, 0, 20, 0);
        Grid.SetColumn(_biosLeft, 0);
        Grid.SetColumn(_biosRight, 1);
        biosGrid.Children.Add(_biosLeft);
        biosGrid.Children.Add(_biosRight);
        biosBody.Children.Add(biosGrid);
        biosBody.Children.Add(Ui.Wrap(
            Ui.Btn("Reler firmware", () => AnalyzeBios(force: true), primary: true),
            Ui.Btn("Suporte do fabricante", OpenVendorSupport)));

        // ---------- drivers ----------
        var drv = Card("DRIVERS E WINDOWS UPDATE", 970);
        var drvBody = BodyOf(drv);
        drvBody.Children.Add(Ui.Lbl(
            "O Mago le a versao e a data dos drivers principais e avisa quando algum esta antigo.",
            11, Ui.Dim));
        _drvStatus = Ui.Lbl("A verificar os drivers instalados...", 12, Ui.Txt, bold: true);
        _drvStatus.Margin = new Thickness(0, 6, 0, 4);
        drvBody.Children.Add(_drvStatus);
        _drvList = Ui.List(168);
        _drvList.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        drvBody.Children.Add(_drvList);
        drvBody.Children.Add(Ui.Wrap(
            Ui.Btn("Verificar no Windows Update", CheckUpdates, primary: true),
            Ui.Btn("Instalar atualizacoes", InstallUpdates),
            Ui.Btn("Abrir Windows Update", () => Proc.OpenUri("ms-settings:windowsupdate"))));

        // ---------- diagnostico rapido ----------
        var diagCard = Card("DIAGNOSTICO RAPIDO", 970);
        var diagBody = BodyOf(diagCard);
        diagBody.Children.Add(Ui.Lbl(
            "RAM abaixo do anunciado (XMP/EXPO), CPU limitada ou processos a comer CPU: " +
            "e' aqui que costuma estar o desempenho que falta.", 11, Ui.Dim));
        _ramLine = Ui.Lbl("RAM: a ler...", 11, Ui.Txt);
        _cpuLine = Ui.Lbl("CPU: a ler...", 11, Ui.Txt);
        _diskLine = Ui.Lbl("Discos: a ler...", 11, Ui.Txt);
        diagBody.Children.Add(_ramLine);
        diagBody.Children.Add(_cpuLine);
        diagBody.Children.Add(_diskLine);
        _procList = Ui.TextList(112);
        diagBody.Children.Add(_procList);
        diagBody.Children.Add(Ui.Wrap(
            Ui.Btn("Analisar de novo", () => LoadDiagnostics(force: true), primary: true),
            Ui.Btn("Fechar processo selecionado", CloseSelectedProcess),
            Ui.Btn("Teste de carga (15 s)", () => Host.RunTask(() => Host.Engine.StressTest(15), "Teste de carga..."))));

        Add(pc);
        Add(bios);
        Add(drv);
        Add(diagCard);
    }

    private static StackPanel BodyOf(Border card) => (StackPanel)((StackPanel)card.Child).Children[1];

    private static TextBlock Spec(Grid grid, int row, int column, string label)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 20, 14) };
        stack.Children.Add(Ui.Lbl(label, 9, Ui.Dim, bold: true, wrap: false));
        var value = Ui.Lbl("-", 13, Ui.Txt);
        stack.Children.Add(value);
        Grid.SetRow(stack, row);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
        return value;
    }

    public void SetHardware(HardwareInfo hw)
    {
        var os = FriendlyOs();
        var ram = hw.RamGb > 0 ? $"{hw.RamGb:0.#} GB" : "?";
        _headline.Text = $"{os}  ·  {Environment.ProcessorCount} nucleos  ·  {ram} de RAM";
        _vOs.Text = os;
        _vCpu.Text = string.IsNullOrWhiteSpace(hw.Cpu) ? "-" : hw.Cpu;
        _vGpu.Text = string.IsNullOrWhiteSpace(hw.Gpu) ? "-" : hw.Gpu;
        _vRam.Text = ram;
        _vDisk.Text = hw.Disks.Count > 0 ? string.Join("  •  ", hw.Disks) : "Nao identificado";
    }

    /// <summary>"Microsoft Windows 10.0.26100" nao diz nada ao utilizador.</summary>
    private static string FriendlyOs()
    {
        var v = Environment.OSVersion.Version;
        var name = v.Build >= 22000 ? "Windows 11" : v.Build >= 10240 ? "Windows 10" : "Windows";
        return $"{name} (build {v.Build})";
    }

    /// <summary>Le os drivers em segundo plano (nao bloqueia a janela).</summary>
    public void BeginScan()
    {
        if (_scanning) return;
        _scanning = true;
        _drvStatus.Foreground = Ui.Br(Ui.Txt);
        _drvStatus.Text = "A verificar os drivers instalados...";
        _drvList.Items.Clear();
        System.Threading.Tasks.Task.Run(() =>
        {
            List<DriverRow> rows;
            try { rows = Drivers.Scan(); }
            catch (Exception ex) { Log.Write("Drivers: " + ex.Message, "ERRO"); rows = new(); }
            Host.Dispatcher.Invoke(() =>
            {
                FillDrivers(rows);
                _scanning = false;
            });
        });
    }

    private void FillDrivers(List<DriverRow> rows)
    {
        _drvList.Items.Clear();
        if (rows.Count == 0)
        {
            _drvStatus.Text = "Nao foi possivel ler a lista de drivers.";
            _drvStatus.Foreground = Ui.Br(Ui.Gold);
            _drvList.Items.Add(Ui.Lbl("Sem dados de drivers neste sistema.", 11, Ui.Dim));
            return;
        }

        var old = rows.Count(r => r.Old);
        if (old == 0)
        {
            _drvStatus.Text = $"{rows.Count} drivers verificados - todos recentes.";
            _drvStatus.Foreground = Ui.Br(Ui.Green);
        }
        else
        {
            _drvStatus.Text = $"{rows.Count} drivers verificados - {old} com mais de 2 anos. Veja o Windows Update.";
            _drvStatus.Foreground = Ui.Br(Ui.Gold);
        }

        foreach (var row in rows.Take(12)) _drvList.Items.Add(DriverItem(row));
        // Altura justa: com poucos drivers a caixa nao fica com espaco vazio nem barra.
        _drvList.Height = Math.Min(168, 26 * Math.Min(rows.Count, 12) + 10);
    }

    private static UIElement DriverItem(DriverRow row)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        var name = Ui.Lbl(row.Device, 12, Ui.Txt, wrap: false);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = $"{row.Device}\nClasse: {row.Class}\nFabricante: {row.Provider}";
        var meta = Ui.Lbl($"v{row.Version} · {row.Date}", 11, Ui.Dim, wrap: false);
        var badge = Ui.Lbl(row.Old ? "DESATUALIZADO" : "recente", 10,
            row.Old ? Ui.Gold : Ui.Green, bold: true, wrap: false);

        Grid.SetColumn(name, 0);
        Grid.SetColumn(meta, 1);
        Grid.SetColumn(badge, 2);
        grid.Children.Add(name);
        grid.Children.Add(meta);
        grid.Children.Add(badge);
        return grid;
    }

    private void CheckUpdates()
    {
        Host.RunTask(() =>
        {
            var (ok, message, titles) = Drivers.CheckUpdates();
            Host.Dispatcher.Invoke(() =>
            {
                if (!ok)
                {
                    _drvStatus.Text = "Nao foi possivel consultar o Windows Update.";
                    _drvStatus.Foreground = Ui.Br(Ui.Gold);
                }
                else if (titles.Count == 0)
                {
                    _drvStatus.Text = "Windows Update: nenhuma atualizacao de driver pendente.";
                    _drvStatus.Foreground = Ui.Br(Ui.Green);
                }
                else
                {
                    _drvStatus.Text = $"Windows Update: {titles.Count} atualizacao(oes) de driver disponivel(is).";
                    _drvStatus.Foreground = Ui.Br(Ui.Gold);
                }
            });
            return message;
        }, "A consultar o Windows Update...");
    }

    private void InstallUpdates()
    {
        if (!Host.Confirm(
                "Isto descarrega e instala as atualizacoes de driver do Windows Update.\n" +
                "Pode demorar e pode ser necessario reiniciar o PC. Continuar?",
                "Atualizar drivers")) return;

        Host.RunTask(() =>
        {
            var (ok, message) = Drivers.InstallUpdates();
            Host.Dispatcher.Invoke(() =>
            {
                _drvStatus.Text = ok ? "Atualizacoes de driver concluidas." : "Falha ao instalar atualizacoes de driver.";
                _drvStatus.Foreground = Ui.Br(ok ? Ui.Green : Ui.Gold);
            });
            BeginScan();
            return message;
        }, "A instalar atualizacoes de driver...");
    }

    public override void OnShown()
    {
        LoadDiagnostics();
        AnalyzeBios();
    }

    // ---------- BIOS e firmware ----------

    /// <summary>Uma so' leitura; o botao "Reler firmware" forca outra.</summary>
    private void AnalyzeBios(bool force = false)
    {
        if (_biosLoading || (_biosLoaded && !force)) return;
        _biosLoading = true;
        _biosLeft.Text = "A analisar firmware...";
        _biosRight.Text = "";

        System.Threading.Tasks.Task.Run(() =>
        {
            Dictionary<string, string?> info;
            string text;
            try
            {
                info = SystemInfo.Bios();
                text = SystemInfo.BiosText(info);
            }
            catch (Exception ex)
            {
                Log.Write("BIOS: " + ex.Message, "ERRO");
                info = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                text = "Nao foi possivel ler o firmware (" + ex.Message + ")";
            }

            Host.Dispatcher.Invoke(() =>
            {
                _biosInfo = info;
                var lines = text.Split('\n');
                var half = (lines.Length + 1) / 2;
                _biosLeft.Text = string.Join("\n", lines.Take(half));
                _biosRight.Text = string.Join("\n", lines.Skip(half));
                _biosLoaded = true;
                _biosLoading = false;
            });
        });
    }

    private void OpenVendorSupport()
    {
        if (_biosInfo == null)
        {
            Host.Info("Espere a leitura do firmware terminar.", "BIOS");
            return;
        }

        var url = SystemInfo.VendorSupportUrl(_biosInfo);
        Log.Write("A abrir suporte do fabricante: " + url);
        Proc.OpenUri(url);
    }

    // ---------- diagnostico rapido ----------

    private void LoadDiagnostics(bool force = false)
    {
        if (_diagLoading) return;
        if (!force && _diagLoaded) return;
        _diagLoading = true;
        _ramLine.Text = "RAM: a ler...";
        _cpuLine.Text = "CPU: a ler...";
        _diskLine.Text = "Discos: a ler...";

        System.Threading.Tasks.Task.Run(() =>
        {
            string ram, cpu, disk;
            var procs = new List<ProcessLoad>();
            try { ram = HardwareInsight.MemorySummary(HardwareInsight.MemoryModules()); }
            catch (Exception ex) { ram = "RAM: falhou a leitura (" + ex.Message + ")"; }
            try { cpu = HardwareInsight.CpuSummary(HardwareInsight.Cpu()); }
            catch (Exception ex) { cpu = "CPU: falhou a leitura (" + ex.Message + ")"; }
            try { disk = HardwareInsight.DiskSummary(HardwareInsight.Disks()); }
            catch (Exception ex) { disk = "Discos: falhou a leitura (" + ex.Message + ")"; }
            try { procs = HardwareInsight.TopProcesses(6); }
            catch (Exception ex) { Log.Write("Top processos: " + ex.Message, "AVISO"); }

            Host.Dispatcher.Invoke(() =>
            {
                _ramLine.Text = ram;
                _ramLine.Foreground = Ui.Br(ram.StartsWith("XMP/EXPO", StringComparison.OrdinalIgnoreCase) ? Ui.Gold : Ui.Txt);
                _cpuLine.Text = cpu;
                _cpuLine.Foreground = Ui.Br(cpu.Contains("possivel limite", StringComparison.OrdinalIgnoreCase) ? Ui.Gold : Ui.Txt);
                _diskLine.Text = disk;
                _diskLine.Foreground = Ui.Br(disk.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase) ? Ui.Red : Ui.Txt);

                _procData = procs;
                _procList.Items.Clear();
                foreach (var p in procs)
                    _procList.Items.Add($"{p.Name}  (PID {p.Pid})   CPU {p.CpuPercent:0.0}%   RAM {p.RamMb:0} MB");
                if (procs.Count == 0) _procList.Items.Add("Sem dados de processos.");

                _diagLoaded = true;
                _diagLoading = false;
            });
        });
    }

    private void CloseSelectedProcess()
    {
        var index = _procList.SelectedIndex;
        if (index < 0 || index >= _procData.Count)
        {
            Host.Info("Selecione um processo na lista.", "Fechar processo");
            return;
        }

        var p = _procData[index];
        if (!Host.Confirm(
                $"Pedir o fecho de {p.Name} (PID {p.Pid})?\n\n" +
                "Apps com trabalho por guardar recusam e ficam abertos. Nao uso terminacao forcada.",
                "Fechar processo"))
            return;

        Host.Info(TweakEngine.CloseProcessGracefully(p.Pid, p.Name), "Fechar processo");
        LoadDiagnostics(force: true);
    }
}
