using System.Windows;
using System.Windows.Controls;

namespace MagoDaPerformance;

public sealed class BackupPage : PageBase
{
    private readonly TextBlock _info;
    private readonly ListBox _points;
    private readonly TextBlock _pointsStatus;
    private bool _loaded;

    public BackupPage(MainWindow host) : base(host, "Backup e restauro",
        "O backup guarda os valores originais de registo, servicos, BCD, energia e arranque.")
    {
        var card = Card("BACKUP MAGO", 480);
        var body = BodyOf(card);
        _info = Ui.Mono("", 11, Ui.Txt);
        body.Children.Add(_info);
        body.Children.Add(Ui.Wrap(
            Ui.Btn("CAPTURAR AGORA", Capture, primary: true),
            Ui.Btn("Restaurar backup Mago", Restore),
            Ui.Btn("Exportar", Export),
            Ui.Btn("Importar", Import),
            Ui.Btn("Abrir snapshots", () => { Directory.CreateDirectory(Paths.SnapshotDir); Proc.OpenUri(Paths.SnapshotDir); })));

        var pointsCard = Card("PONTOS DE RESTAURO DO WINDOWS", 480);
        var pointsBody = BodyOf(pointsCard);
        _points = Ui.List(200);
        _pointsStatus = Ui.Lbl("", 11, Ui.Dim);
        pointsBody.Children.Add(Ui.Wrap(
            Ui.Btn("Criar ponto", () => Host.RunTask(() =>
            {
                Host.Engine.CreateRestorePoint();
                return null;
            }, "A criar ponto de restauro...")),
            Ui.Btn("Atualizar lista", LoadPoints),
            Ui.Btn("Restaurar ponto selecionado", RestorePoint)));
        pointsBody.Children.Add(_points);
        pointsBody.Children.Add(_pointsStatus);

        Add(Row(card, pointsCard));
    }

    public override void OnShown()
    {
        RefreshInfo();
        if (_loaded) return;
        _loaded = true;
        LoadPoints();
    }

    private static StackPanel BodyOf(Border card) => (StackPanel)((StackPanel)card.Child).Children[1];

    public void RefreshInfo()
    {
        var data = Host.Store.Data;
        var snaps = Directory.Exists(Paths.SnapshotDir)
            ? Directory.GetFiles(Paths.SnapshotDir, "*.json").Length
            : 0;
        _info.Text =
            $"Ultimo backup Mago: {data.Created ?? "nunca"}\n" +
            $"Chaves de registo: {data.Registry.Count}   |   Servicos: {data.Services.Count}   |   BCD: {data.Bcd.Count}\n" +
            $"Plano de energia: {data.PowerScheme ?? "—"}\n" +
            $"Snapshots extra na pasta: {snaps}\n" +
            $"Ficheiro ativo: {Paths.BackupFile}";
    }

    private void Capture()
    {
        Host.RunTask(() =>
        {
            var dest = Host.Engine.CaptureNow();
            Host.Dispatcher.Invoke(RefreshInfo);
            return "Estado atual capturado.\nCopia extra:\n" + dest;
        }, "A capturar backup...");
    }

    private void Restore()
    {
        if (!Host.Store.HasBackup) { Host.Info("Nao ha backup Mago para restaurar.", "Backup"); return; }
        if (!Host.Confirm("Repor os valores guardados no backup Mago?", "Restaurar")) return;

        Host.RunTask(() =>
        {
            Host.Engine.RevertAll();
            return "Restauro concluido. Reinicia se alteraste BCD/VBS/GPU.";
        }, "A restaurar backup...");
    }

    private void Export()
    {
        if (!File.Exists(Paths.BackupFile)) { Host.Info("Ainda nao existe mago_backup.json. Guarda um backup primeiro.", "Exportar"); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar backup",
            FileName = "mago_backup.json",
            DefaultExt = ".json",
            Filter = "JSON (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.Copy(Paths.BackupFile, dialog.FileName, true);
            Log.Write("Backup exportado para " + dialog.FileName);
            Host.Info("Backup exportado.", "Exportar");
        }
        catch (Exception ex) { Host.Info(ex.Message, "Exportar"); }
    }

    private void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Importar backup", Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var json = File.ReadAllText(dialog.FileName);
            if (!json.Contains("\"registry\"", StringComparison.OrdinalIgnoreCase))
            {
                Host.Info("Ficheiro nao parece um backup Mago.", "Importar");
                return;
            }
            Directory.CreateDirectory(Paths.DataDir);
            File.Copy(dialog.FileName, Paths.BackupFile, true);
            Host.Store.Reload();
            RefreshInfo();
            Log.Write("Backup importado de " + dialog.FileName);
            Host.Info("Backup carregado. Usa Restaurar backup Mago para aplicar.", "Importar");
        }
        catch (Exception ex) { Host.Info(ex.Message, "Importar"); }
    }

    private void LoadPoints()
    {
        _pointsStatus.Text = "A ler pontos de restauro...";
        System.Threading.Tasks.Task.Run(() =>
        {
            var rows = Proc.Ps(
                "Get-ComputerRestorePoint | Select-Object SequenceNumber, Description, " +
                "@{n='Date';e={$_.ConvertToDateTime($_.CreationTime).ToString('yyyy-MM-dd HH:mm')}}", 40000);
            Host.Dispatcher.Invoke(() =>
            {
                _points.Items.Clear();
                foreach (var row in rows)
                {
                    var seq = Proc.Str(row, "SequenceNumber") ?? "?";
                    var date = Proc.Str(row, "Date") ?? "";
                    var desc = Proc.Str(row, "Description") ?? "";
                    _points.Items.Add(new RestorePointRow(seq, date, desc));
                }
                _pointsStatus.Text = _points.Items.Count == 0
                    ? "Nenhum ponto listado (Protecao do sistema pode estar desligada)."
                    : $"{_points.Items.Count} pontos de restauro.";
            });
        });
    }

    private void RestorePoint()
    {
        if (_points.SelectedItem is not RestorePointRow point)
        {
            Host.Info("Seleciona um ponto na lista.", "Restauro");
            return;
        }
        if (!Host.Confirm($"Isto reinicia o PC e restaura o ponto {point.Sequence}. Continuar?", "Restauro Windows")) return;
        Log.Write($"A pedir Restore-Computer {point.Sequence}...");
        Proc.Run("powershell", $"-NoProfile -NonInteractive -Command \"Restore-Computer -RestorePoint {point.Sequence} -Confirm:$false\"", 30000);
        Host.Info("O Windows vai reiniciar se o ponto for aceite.", "Restauro");
    }

    private sealed record RestorePointRow(string Sequence, string Date, string Description)
    {
        public override string ToString() => $"{Sequence}  |  {Date}  |  {Description}";
    }
}
