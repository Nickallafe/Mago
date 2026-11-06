using System.Windows;

namespace MagoDaPerformance;

public static class Program
{
    /// <summary>Pagina a abrir; definida por --page=nome (usado em testes e atalhos).</summary>
    internal static string? StartPage { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        foreach (var arg in args)
        {
            foreach (var prefix in new[] { "--page=", "/page=" })
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    StartPage = arg[prefix.Length..].Trim().ToLowerInvariant();
        }
        if (args.Any(a => a.Equals("--selfcheck", StringComparison.OrdinalIgnoreCase)))
            return SelfCheck();

        // Diagnostico: abre a janela e fecha sozinha (usado para validar a UI sem UAC).
        if (args.Any(a => a.Equals("--smoke", StringComparison.OrdinalIgnoreCase)))
            return Smoke();

        try
        {
            Proc.EnsureAdminOrExit();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Esta app precisa de correr como administrador.\nO pedido de elevacao foi cancelado ou falhou.\n\n" + ex.Message,
                "MAGO DA PERFORMANCE", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        try
        {
            app.Run(new MainWindow());
            return 0;
        }
        catch (Exception ex)
        {
            Dump("main", ex);
            MessageBox.Show(ex.Message, "Mago da Performance", MessageBoxButton.OK, MessageBoxImage.Error);
            return 4;
        }
    }

    private static int Smoke()
    {
        try { return RunSmoke(); }
        catch (Exception ex) { Dump("smoke", ex); return 4; }
    }

    private static int RunSmoke()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { timer.Stop(); app.Shutdown(); };
        timer.Start();
        app.Run(window);
        return 0;
    }

    internal static void Dump(string where, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.AppendAllText(Path.Combine(Paths.DataDir, "crash.txt"),
                $"[{DateTime.Now:O}] {where}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Valida o runtime sem abrir a interface (usado por CI e pelo launcher).</summary>
    private static int SelfCheck()
    {
        var problems = new List<string>();
        foreach (var dir in new[] { Paths.DataDir, Paths.SnapshotDir })
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { problems.Add($"pasta de dados inacessivel ({dir}): {ex.Message}"); }
        }
        try
        {
            using var _ = File.AppendText(Paths.LogFile);
        }
        catch (Exception ex) { problems.Add($"log inacessivel ({Paths.LogFile}): {ex.Message}"); }

        if (!OperatingSystem.IsWindows()) problems.Add("sistema operativo nao e Windows");
        if (!Environment.Is64BitOperatingSystem) problems.Add("Windows nao e x64");

        foreach (var p in problems) Console.WriteLine(p);
        if (problems.Count > 0) return 3;
        Console.WriteLine("ok");
        return 0;
    }
}
