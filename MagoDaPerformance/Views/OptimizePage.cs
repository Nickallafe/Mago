using System.Windows;
using System.Windows.Controls;

namespace MagoDaPerformance;

public sealed class OptimizePage : PageBase
{
    private readonly RadioButton _balanced;
    private readonly RadioButton _competitive;
    private readonly TextBlock _hint;
    private readonly ListBox _modulesList;
    private readonly TextBlock _modulesStatus;

    public OptimizePage(MainWindow host) : base(host, "Otimizar",
        "Cada modulo guarda o valor original antes de alterar. Usa Backup para voltar atras.")
    {
        var profile = Card("PERFIL", 380);
        var profileBody = BodyOf(profile);
        _balanced = new RadioButton
        {
            Content = "Equilibrado - seguro para uso diario",
            IsChecked = true,
            Foreground = Ui.Br(Ui.Txt),
            Margin = new Thickness(0, 4, 0, 4),
        };
        _competitive = new RadioButton
        {
            Content = "Competitivo - timers e TDR mais tolerantes",
            Foreground = Ui.Br(Ui.Txt),
            Margin = new Thickness(0, 4, 0, 8),
        };
        _hint = Ui.Lbl("Perfil atual: Equilibrado.", 11, Ui.Dim);
        _balanced.Checked += (_, _) => SetMode(false);
        _competitive.Checked += (_, _) => SetMode(true);
        profileBody.Children.Add(_balanced);
        profileBody.Children.Add(_competitive);
        profileBody.Children.Add(_hint);

        var modules = Card("MODULOS", 520);
        var modBody = BodyOf(modules);
        modBody.Children.Add(Ui.Wrap(
            Ui.Btn("GPU / HAGS", () => Host.RunTask(() => { Host.Engine.TweakGpu(); return null; }, "GPU...")),
            Ui.Btn("Windows Gaming", () => Host.RunTask(() => { Host.Engine.TweakWindowsGaming(); return null; }, "Windows Gaming...")),
            Ui.Btn("Registo multimedia", () => Host.RunTask(() => { Host.Engine.TweakReg(); return null; }, "Registo...")),
            Ui.Btn("Plano de energia", () => Host.RunTask(() => { Host.Engine.TweakPower(); return null; }, "Energia...")),
            Ui.Btn("Servicos", () => Host.RunTask(() => { Host.Engine.TweakServices(); return null; }, "Servicos...")),
            Ui.Btn("Temp + DNS", () => Host.RunTask(() => Host.Engine.CleanTempAndDns(), "Limpeza...")),
            Ui.Btn("Rede", () => Host.RunTask(() => { Host.Engine.TweakNetwork(); return null; }, "Rede...")),
            Ui.Btn("Input lag", () => Host.RunTask(() => { Host.Engine.TweakInputLag(); return null; }, "Input lag...")),
            Ui.Btn("SSD", () => Host.RunTask(() => { Host.Engine.TweakSsd(); return null; }, "SSD...")),
            Ui.Btn("Encerrar apps", CloseBackgroundApps)));

        var all = Card("TUDO DE UMA VEZ", 380);
        var allBody = BodyOf(all);
        allBody.Children.Add(Ui.Lbl(
            "Aplicar tudo cria um ponto de restauro, corre todos os modulos e grava o backup.", 11, Ui.Dim));
        allBody.Children.Add(Ui.Wrap(
            Ui.Btn("APLICAR TUDO", ApplyAll, primary: true),
            Ui.Btn("RESTAURAR BACKUP", Revert)));
        allBody.Children.Add(Ui.Lbl(
            "Reinicia o PC depois de mexer em kernel, GPU, VBS ou BCD.", 10, Ui.Gold));

        // Desfazer um modulo sem voltar ao backup inteiro.
        var undo = Card("DESFAZER UM MODULO", 420);
        var undoBody = BodyOf(undo);
        undoBody.Children.Add(Ui.Lbl(
            "Repoe apenas o que o modulo escolhido alterou. Temp, caches e apps fechados nao voltam atras.",
            11, Ui.Dim));
        _modulesList = Ui.TextList(120);
        _modulesStatus = Ui.Lbl("", 11, Ui.Dim);
        undoBody.Children.Add(_modulesList);
        undoBody.Children.Add(Ui.Wrap(
            Ui.Btn("Desfazer selecionado", RevertSelectedModule),
            Ui.Btn("Atualizar lista", LoadModules)));
        undoBody.Children.Add(_modulesStatus);

        Add(Row(profile, modules));
        Add(Row(all, undo));

        LoadModules();
    }

    private void LoadModules()
    {
        var applied = Host.Engine.AppliedModules();
        _modulesList.Items.Clear();
        foreach (var module in applied) _modulesList.Items.Add(module);
        if (_modulesList.SelectedItem == null && applied.Count > 0) _modulesList.SelectedIndex = 0;
        _modulesStatus.Text = applied.Count == 0
            ? "Nenhum modulo aplicado desde a ultima captura de backup."
            : $"{applied.Count} modulo(s) com alteracoes guardadas.";
    }

    private void RevertSelectedModule()
    {
        if (_modulesList.SelectedItem is not string module)
        {
            Host.Info("Seleciona um modulo na lista.", "Desfazer modulo");
            return;
        }
        if (!Host.Confirm($"Desfazer apenas o modulo {module}?", "Desfazer modulo")) return;

        Host.RunTask(() =>
        {
            var result = Host.Engine.RevertModule(module);
            LoadModules();
            return result;
        }, "A desfazer " + module);
    }

    public override void OnShown() => LoadModules();

    private static StackPanel BodyOf(Border card) => (StackPanel)((StackPanel)card.Child).Children[1];

    private void SetMode(bool competitive)
    {
        Host.Engine.Competitive = competitive;
        _hint.Text = competitive
            ? "Competitivo: desliga VBS/HVCI (baixa a seguranca) e, em SSD, SysMain/WSearch. " +
              "O watchdog da GPU continua ligado, so' com 10 s de tolerancia."
            : "Perfil atual: Equilibrado.";
    }

    /// <summary>
    /// Fecha apps de fundo pedindo o fecho normal: o utilizador ve' exatamente quais estao
    /// a correr e quem tem trabalho por guardar fica aberto.
    /// </summary>
    private void CloseBackgroundApps()
    {
        var running = Host.Engine.RunningBackgroundApps();
        if (running.Count == 0)
        {
            Host.Info("Nenhum dos apps de fundo conhecidos esta a correr.", "Encerrar apps");
            return;
        }

        var message = "A correr agora:\n\n  " + string.Join("\n  ", running) +
                      "\n\nVou pedir o fecho normal a cada um. Quem tiver trabalho por guardar recusa e " +
                      "fica aberto (nao uso terminacao forcada). Continuar?";
        if (!Host.Confirm(message, "Encerrar apps")) return;

        Host.RunTask(() => Host.Engine.CloseBackground(), "A encerrar apps...");
    }

    private void ApplyAll()
    {
        var message = Host.Engine.Competitive
            ? "Perfil Competitivo:\n\n" +
              "- desliga VBS/HVCI (baixa a protecao do Windows e pode ser reativado sozinho)\n" +
              "- em SSD, desliga SysMain e a indexacao da pesquisa\n" +
              "- tolerancia de 10 s no watchdog da GPU (continua a recuperar de crashes)\n\n" +
              "Cria backup e ponto de restauro antes. Continuar?"
            : "Aplicar o perfil Equilibrado com backup e ponto de restauro?";
        if (!Host.Confirm(message, "Aplicar tudo")) return;

        Host.RunTask(() =>
        {
            var summary = Host.Engine.ApplyAll(label => Host.Dispatcher.Invoke(() => Host.Status("A aplicar: " + label)));
            LoadModules();
            return summary;
        }, "A aplicar otimizacoes...");
    }

    private void Revert()
    {
        if (!Host.Store.HasBackup)
        {
            Host.Info("Nao ha backup Mago para restaurar.", "Backup");
            return;
        }
        if (!Host.Confirm("Repor os valores guardados no backup Mago?", "Restaurar")) return;
        Host.RunTask(() =>
        {
            Host.Engine.RevertAll();
            LoadModules();
            return "Restauro concluido. Reinicia se alteraste BCD/VBS/GPU.";
        }, "A restaurar backup...");
    }
}
