using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MagoDaPerformance;

public sealed class AppEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Type { get; set; } = "Win32";
    public string Uninstall { get; set; } = "";
    public string Quiet { get; set; } = "";
    public string Location { get; set; } = "";
    public string Pspath { get; set; } = "";
    public string Package { get; set; } = "";
    public string RawName { get; set; } = "";

    public override string ToString()
    {
        var extra = string.IsNullOrWhiteSpace(Publisher) ? "" : $"  [{Publisher}]";
        return $"{Name}  {Version}{extra}  {Type}";
    }
}

public static class Software
{
    private const string UninstallKeys =
        "$paths=@('HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'," +
        "'HKLM:\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'," +
        "'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'); " +
        "Get-ItemProperty $paths -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName } | " +
        "Select-Object DisplayName,DisplayVersion,Publisher,UninstallString,QuietUninstallString,InstallLocation,PSPath";

    public static List<AppEntry> InstalledApps()
    {
        Log.Write("A carregar programas instalados...");
        var apps = new List<AppEntry>();

        foreach (var row in Proc.Ps(UninstallKeys, 90000))
        {
            apps.Add(new AppEntry
            {
                Name = Proc.Str(row, "DisplayName") ?? "",
                Version = Proc.Str(row, "DisplayVersion") ?? "",
                Publisher = Proc.Str(row, "Publisher") ?? "",
                Type = "Win32",
                Uninstall = Proc.Str(row, "UninstallString") ?? "",
                Quiet = Proc.Str(row, "QuietUninstallString") ?? "",
                Location = Proc.Str(row, "InstallLocation") ?? "",
                Pspath = Proc.Str(row, "PSPath") ?? "",
            });
        }

        // Get-StartApps da o nome amigavel das apps da Store (a package name e ilegivel).
        var friendly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Proc.Ps("Get-StartApps | Select-Object Name,AppID", 60000))
        {
            var appId = Proc.Str(row, "AppID") ?? "";
            var name = Proc.Str(row, "Name") ?? "";
            var bang = appId.IndexOf('!');
            if (bang > 0 && !string.IsNullOrWhiteSpace(name)) friendly[appId[..bang]] = name;
        }

        var store = Proc.Ps(
            "Get-AppxPackage | Where-Object { -not $_.NonRemovable } | " +
            "Select-Object Name,Version,Publisher,PackageFullName,InstallLocation", 90000);
        foreach (var row in store)
        {
            var raw = Proc.Str(row, "Name") ?? "";
            var publisher = Proc.Str(row, "Publisher") ?? "";
            if (publisher.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) publisher = "";
            apps.Add(new AppEntry
            {
                Name = friendly.GetValueOrDefault(FamilyName(Proc.Str(row, "PackageFullName") ?? "")) ?? raw,
                RawName = raw,
                Version = Proc.Str(row, "Version") ?? "",
                Publisher = publisher.Length > 40 ? publisher[..40] : publisher,
                Type = "Store",
                Package = Proc.Str(row, "PackageFullName") ?? "",
                Location = Proc.Str(row, "InstallLocation") ?? "",
            });
        }

        apps = apps.Where(a => !string.IsNullOrWhiteSpace(a.Name))
                   .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                   .ToList();
        for (var i = 0; i < apps.Count; i++) apps[i].Id = i.ToString();
        Log.Write($"{apps.Count} aplicativos encontrados.");
        return apps;
    }

    /// <summary>De "Nome_1.0.0.0_x64__abc" para "Nome_abc" (package family name).</summary>
    internal static string FamilyName(string packageFullName)
    {
        var parts = (packageFullName ?? "").Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 5 ? parts[0] + "_" + parts[^1] : parts.FirstOrDefault() ?? "";
    }

    public static string PspathToReg(string pspath)
    {
        if (string.IsNullOrWhiteSpace(pspath)) return "";
        var text = pspath;
        var idx = text.IndexOf("Registry::", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) text = text[(idx + "Registry::".Length)..];
        return text.Replace("Microsoft.PowerShell.Core\\Registry::", "")
                   .Replace("HKEY_LOCAL_MACHINE", "HKLM")
                   .Replace("HKEY_CURRENT_USER", "HKCU");
    }

    public static bool IsSafeDeletePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string norm;
        try { norm = Path.GetFullPath(path).TrimEnd('\\'); } catch { return false; }
        if (norm.Length < 12) return false;

        var low = norm.ToLowerInvariant();
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\').ToLowerInvariant();
        if (low == windir || low.StartsWith(windir + "\\")) return false;

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        }.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.TrimEnd('\\').ToLowerInvariant());

        foreach (var root in roots)
        {
            if (low == root) return false;
            if (low.StartsWith(root + "\\") && low.Length > root.Length + 4) return true;
        }
        return false;
    }

    /// <summary>Desinstala pelo metodo oficial; force remove tambem a entrada e a pasta.</summary>
    public static void Uninstall(AppEntry app, bool force, Func<string, bool> askDeleteFolder)
    {
        Log.Write($"{(force ? "Forcar" : "Desinstalar")}: {app.Name}");
        if (app.Type == "Store")
        {
            var package = app.Package;
            var (ok, output) = Proc.Run("powershell",
                "-NoProfile -NonInteractive -Command \"Remove-AppxPackage -Package '" + package + "'\"", 180000);
            Log.Write(ok ? "App Store removida" : "Falha ao remover AppX: " + output, ok ? "INFO" : "ERRO");
            if (force && !ok)
            {
                var (ok2, out2) = Proc.Run("powershell",
                    "-NoProfile -NonInteractive -Command \"Get-AppxPackage -AllUsers | Where-Object { $_.PackageFullName -eq '" +
                    package + "' } | Remove-AppxPackage -AllUsers\"", 180000);
                Log.Write(ok2 ? "AppX AllUsers removida" : "Falha AllUsers: " + out2, ok2 ? "INFO" : "ERRO");
            }
            return;
        }

        var cmd = !string.IsNullOrWhiteSpace(app.Quiet) ? app.Quiet : app.Uninstall;
        cmd = cmd.Trim();
        if (!string.IsNullOrEmpty(cmd))
        {
            if (cmd.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                var guid = Regex.Match(cmd, @"\{[0-9A-Fa-f\-]{36}\}");
                if (guid.Success) cmd = $"msiexec /x {guid.Value} /passive /norestart";
            }
            Log.Write("A executar: " + cmd);
            var (ok, output) = Proc.RunShell(cmd);
            Log.Write(ok ? "Desinstalador terminou" : "Desinstalador falhou ou foi cancelado", ok ? "INFO" : "ERRO");
            if (!ok && output.Length > 0) Log.Write(output, "AVISO");
        }
        else if (!force)
        {
            Log.Write("Sem UninstallString. Usa remocao forcada.", "AVISO");
        }

        if (!force) return;

        var key = PspathToReg(app.Pspath);
        if (!string.IsNullOrEmpty(key))
        {
            var ok = Reg.DeleteKeyTree(key);
            Log.Write(ok ? "Entrada de registo removida: " + key : "Falha a apagar chave uninstall", ok ? "INFO" : "ERRO");
        }

        var location = (app.Location ?? "").Trim().Trim('"');
        if (string.IsNullOrEmpty(location) || !Directory.Exists(location)) return;
        if (!IsSafeDeletePath(location))
        {
            Log.Write("Pasta nao apagada (caminho protegido ou invalido): " + location, "AVISO");
            return;
        }
        if (!askDeleteFolder(location)) return;
        try
        {
            Directory.Delete(location, true);
            Log.Write("Pasta removida: " + location);
        }
        catch (Exception ex) { Log.Write(ex.Message, "ERRO"); }
    }

    // ---------- arranque ----------

    public sealed record StartupItem(string Name, string Hive, string Data, string HivePath)
    {
        public override string ToString() => $"{Name}   [{Hive}]   {Data}";
    }

    private static readonly (string Path, string Short)[] RunKeys =
    {
        (@"Software\Microsoft\Windows\CurrentVersion\Run", "HKCU"),
        (@"Software\Microsoft\Windows\CurrentVersion\Run", "HKLM"),
    };

    public static List<StartupItem> StartupItems()
    {
        var items = new List<StartupItem>();
        foreach (var (path, hive) in RunKeys)
        {
            var root = hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
            try
            {
                using var key = root.OpenSubKey(path);
                if (key == null) continue;
                foreach (var name in key.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var data = key.GetValue(name)?.ToString() ?? "";
                    items.Add(new StartupItem(name, hive, data, $"{hive}\\{path}"));
                }
            }
            catch (Exception ex)
            {
                Log.Write($"{hive} Run: {ex.Message}", "AVISO");
            }
        }
        Log.Write($"{items.Count} itens de arranque.");
        return items;
    }

    public static bool DisableStartup(StartupItem item, BackupStore backup)
    {
        var snap = Reg.Snapshot(item.HivePath, item.Name);
        backup.RememberStartup(item.HivePath + "::" + item.Name,
            new StartupSnap { Existed = snap.Existed, Data = snap.Data ?? item.Data });
        backup.Save();

        var ok = Reg.DeleteValue(item.HivePath, item.Name);
        Log.Write(ok ? "Arranque desativado: " + item.Name : "Falha ao desativar arranque: " + item.Name,
            ok ? "INFO" : "ERRO");
        return ok;
    }
}
