using System.Windows;
using System.Windows.Controls;

namespace MagoDaPerformance;

public sealed class OptimizePage : PageBase
{
    private readonly RadioButton _balanced;
    private readonly RadioButton _competitive;
    private readonly TextBlock _hint;

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
            Content = "Competitivo - VBS, timers e TDR agressivos",
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
            Ui.Btn("Temp + DNS", () => Host.RunTask(() => { Host.Engine.CleanTempAndDns(); return null; }, "Limpeza...")),
            Ui.Btn("Rede", () => Host.RunTask(() => { Host.Engine.TweakNetwork(); return null; }, "Rede...")),
            Ui.Btn("Input lag", () => Host.RunTask(() => { Host.Engine.TweakInputLag(); return null; }, "Input lag...")),
            Ui.Btn("SSD", () => Host.RunTask(() => { Host.Engine.TweakSsd(); return null; }, "SSD...")),
            Ui.Btn("Encerrar apps", () =>
            {
                if (Host.Confirm("Fecha browsers, Spotify, OneDrive, Epic e steamwebhelper. Continuar?", "Encerrar apps"))
                    Host.RunTask(() => { Host.Engine.KillBackground(); return null; }, "A encerrar apps...");
            })));

        var all = Card("TUDO DE UMA VEZ", 380);
        var allBody = BodyOf(all);
        allBody.Children.Add(Ui.Lbl(
            "Aplicar tudo cria um ponto de restauro, corre todos os modulos e grava o backup.", 11, Ui.Dim));
        allBody.Children.Add(Ui.Wrap(
            Ui.Btn("APLICAR TUDO", ApplyAll, primary: true),
            Ui.Btn("RESTAURAR BACKUP", Revert)));
        allBody.Children.Add(Ui.Lbl(
            "Reinicia o PC depois de mexer em kernel, GPU, VBS ou BCD.", 10, Ui.Gold));

        Add(Row(profile, modules));
        Add(Row(all));
    }

    private static StackPanel BodyOf(Border card) => (StackPanel)((StackPanel)card.Child).Children[1];

    private void SetMode(bool competitive)
    {
        Host.Engine.Competitive = competitive;
        _hint.Text = competitive
            ? "Perfil atual: Competitivo (SysMain/WSearch desativados em disco SSD)."
            : "Perfil atual: Equilibrado.";
    }

    private void ApplyAll()
    {
        var message = Host.Engine.Competitive
            ? "Perfil Competitivo altera VBS, timers e TDR. Continuar?"
            : "Aplicar o perfil Equilibrado com backup e ponto de restauro?";
        if (!Host.Confirm(message, "Aplicar tudo")) return;

        Host.RunTask(() =>
        {
            Host.Engine.ApplyAll(label => Host.Dispatcher.Invoke(() => Host.Status("A aplicar: " + label)));
            return "Otimizacoes aplicadas. Reinicia o PC se mexeste em kernel/GPU.";
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
            return "Restauro concluido. Reinicia se alteraste BCD/VBS/GPU.";
        }, "A restaurar backup...");
    }
}
