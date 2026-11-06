using System.Diagnostics;

namespace MagoDaPerformance;

public sealed class TweakEngine
{
    public const string UltimateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    public static readonly (string Key, string Name)[] TrackedRegs =
    {
        (@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrLevel"),
        (@"HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NVTweak", "DisplayPowerSaving"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation"),
        (@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled"),
        (@"HKCU\Software\Microsoft\GameBar", "AllowAutoGameMode"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled"),
        (@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness"),
        (@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex"),
        (@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority"),
        (@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority"),
        (@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category"),
        (@"HKCU\System\GameConfigStore", "GameDVR_Enabled"),
        (@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
        (@"HKCU\Control Panel\Mouse", "MouseSpeed"),
        (@"HKCU\Control Panel\Mouse", "MouseThreshold1"),
        (@"HKCU\Control Panel\Mouse", "MouseThreshold2"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnablePrefetcher"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled"),
        (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting"),
    };

    public static readonly string[] TrackedServices = { "DiagTrack", "dmwappushservice", "MapsBroker", "lfsvc", "SysMain", "WSearch" };
    public static readonly string[] TrackedBcd = { "disabledynamictick", "useplatformclock" };

    private readonly object _lock = new();

    public BackupStore Backup { get; }
    public bool Competitive { get; set; }
    public string GpuName { get; set; } = "";
    public List<string> DiskTypes { get; set; } = new();

    public TweakEngine(BackupStore backup) => Backup = backup;

    public bool NvidiaPresent => GpuName.Contains("nvidia", StringComparison.OrdinalIgnoreCase);
    public bool HasHdd => DiskTypes.Any(t => t.Equals("HDD", StringComparison.OrdinalIgnoreCase));

    // ---------- registo ----------

    public RegSnap SnapshotReg(string key, string name, bool overwrite = false)
    {
        var snap = Reg.Snapshot(key, name);
        Backup.RememberReg(key, name, snap, overwrite);
        return snap;
    }

    public bool SetReg(string key, string name, string type, string data)
    {
        SnapshotReg(key, name);
        var ok = Reg.Write(key, name, type, data);
        Log.Write(ok ? $"[OK] Registo {name}={data}" : $"[FALHA] Registo {name}", ok ? "INFO" : "ERRO");
        return ok;
    }

    // ---------- servicos ----------

    public SvcSnap SnapshotService(string name, bool overwrite = false)
    {
        var (okQc, outQc) = Proc.Run("sc.exe", "qc \"" + name + "\"");
        var start = "unknown";
        if (okQc)
        {
            foreach (var line in outQc.Split('\n'))
            {
                if (!line.ToUpperInvariant().Contains("START_TYPE")) continue;
                var digit = line.Where(char.IsDigit).FirstOrDefault();
                start = digit switch { '2' => "auto", '3' => "demand", '4' => "disabled", _ => "demand" };
            }
        }
        var (okQ, outQ) = Proc.Run("sc.exe", "query \"" + name + "\"");
        var running = okQ && outQ.ToUpperInvariant().Contains("RUNNING");
        var snap = new SvcSnap { Name = name, Start = start, Running = running };
        Backup.RememberService(name, snap, overwrite);
        return snap;
    }

    public bool DisableService(string name)
    {
        SnapshotService(name);
        var (okCfg, outCfg) = Proc.Run("sc.exe", "config \"" + name + "\" start= disabled");
        var (_, outStop) = Proc.Run("sc.exe", "stop \"" + name + "\"");
        Log.Write(okCfg ? $"[OK] Servico {name} desativado" : $"[FALHA] Servico {name}", okCfg ? "INFO" : "ERRO");
        if (!okCfg && outCfg.Length > 0) Log.Write(outCfg, "AVISO");
        if (outStop.Length > 0) Log.Write(outStop, "AVISO");
        return okCfg;
    }

    // ---------- BCD / energia ----------

    public BcdSnap SnapshotBcd(string name, bool overwrite = false)
    {
        var (ok, output) = Proc.Run("bcdedit.exe", "/enum {current}");
        string? value = null;
        if (ok)
        {
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) value = parts[^1];
            }
        }
        var snap = new BcdSnap { Existed = value != null, Value = value };
        Backup.RememberBcd(name, snap, overwrite);
        return snap;
    }

    public string? CurrentPowerGuid()
    {
        var (ok, output) = Proc.Run("powercfg.exe", "/getactivescheme");
        if (!ok) return null;
        foreach (var token in output.Split(new[] { ' ', '(', ')', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            if (token.Length == 36 && token.Count(c => c == '-') == 4)
                return token;
        return null;
    }

    // ---------- ponto de restauro ----------

    public bool CreateRestorePoint()
    {
        Log.Write("A tentar criar ponto de restauro do Windows...");
        var (ok, output) = Proc.Run("powershell",
            "-NoProfile -NonInteractive -Command \"try { Checkpoint-Computer -Description 'MagoDaPerformance_v6' -RestorePointType 'MODIFY_SETTINGS'; 'OK' } catch { $_.Exception.Message }\"",
            90000);
        if (ok && output.Contains("OK"))
        {
            Log.Write("Ponto de restauro criado.");
            return true;
        }
        Log.Write("Ponto de restauro indisponivel (limite de 24h ou Protecao do sistema desligada).", "AVISO");
        if (output.Length > 0) Log.Write(output, "AVISO");
        return false;
    }

    // ---------- modulos ----------

    public void TweakGpu()
    {
        Log.Write("GPU: HAGS...");
        SetReg(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", "REG_DWORD", "2");
        if (Competitive)
        {
            Log.Write("Competitivo: TdrLevel=0.", "AVISO");
            SetReg(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrLevel", "REG_DWORD", "0");
        }
        if (NvidiaPresent)
            SetReg(@"HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NVTweak", "DisplayPowerSaving", "REG_DWORD", "0");
        else
            Log.Write("NVIDIA nao detetada.");
    }

    public void TweakWindowsGaming()
    {
        SetReg(@"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", "REG_DWORD", "26");
        SetReg(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", "REG_DWORD", "1");
        SetReg(@"HKCU\Software\Microsoft\GameBar", "AllowAutoGameMode", "REG_DWORD", "1");
        if (!Competitive) return;

        SetReg(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", "REG_DWORD", "0");
        SetReg(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", "REG_DWORD", "0");
        var (ok, output) = Proc.Run("powershell", "-NoProfile -NonInteractive -Command \"Disable-MMAgent -MemoryCompression\"");
        Log.Write(ok ? "Compressao de memoria off" : "Falha MMAgent: " + output, ok ? "INFO" : "AVISO");
    }

    public void TweakReg()
    {
        SetReg(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", "REG_DWORD", "0");
        const string games = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
        SetReg(games, "GPU Priority", "REG_DWORD", "8");
        SetReg(games, "Priority", "REG_DWORD", "6");
        SetReg(games, "Scheduling Category", "REG_SZ", "High");
        SetReg(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", "REG_DWORD", "0");
        SetReg(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", "REG_DWORD", "0");
    }

    public void TweakPower()
    {
        Backup.RememberPower(CurrentPowerGuid());
        Proc.Run("powercfg.exe", "-duplicatescheme " + UltimateGuid);
        var (ok, output) = Proc.Run("powercfg.exe", "-setactive " + UltimateGuid);
        Log.Write(ok ? "Ultimate Performance ativo" : "Falha no plano de energia: " + output, ok ? "INFO" : "AVISO");
    }

    public void TweakServices()
    {
        var targets = new List<string> { "DiagTrack", "dmwappushservice", "MapsBroker", "lfsvc" };
        if (Competitive && !HasHdd)
        {
            targets.Add("SysMain");
            targets.Add("WSearch");
        }
        else if (Competitive && HasHdd)
        {
            Log.Write("HDD detetado: SysMain/WSearch mantidos.", "AVISO");
        }
        foreach (var svc in targets) DisableService(svc);
    }

    public void CleanTempAndRam()
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var folders = new[]
        {
            Path.Combine(windir, "Temp"),
            Environment.GetEnvironmentVariable("TEMP"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
        }.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().ToList();

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder!, "*", SearchOption.AllDirectories))
                    try { File.Delete(file); } catch { }
                Log.Write("Limpeza pedida: " + folder);
            }
            catch (Exception ex) { Log.Write("Falha a limpar " + folder + ": " + ex.Message, "AVISO"); }
        }
        Proc.Run("ipconfig.exe", "/flushdns");
        Log.Write("Temp + DNS: concluido.");
    }

    public void TweakNetwork()
    {
        SetReg(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            "NetworkThrottlingIndex", "REG_DWORD", "4294967295");
        Proc.Run("netsh.exe", "int tcp set global autotuninglevel=normal");
        if (Competitive)
        {
            Proc.Run("netsh.exe", "int tcp set global ecncapability=disabled");
            Proc.Run("netsh.exe", "int tcp set global timestamps=disabled");
        }
        Log.Write("Rede: modulo concluido.");
    }

    public void TweakInputLag()
    {
        SetReg(@"HKCU\Control Panel\Mouse", "MouseSpeed", "REG_SZ", "0");
        SetReg(@"HKCU\Control Panel\Mouse", "MouseThreshold1", "REG_SZ", "0");
        SetReg(@"HKCU\Control Panel\Mouse", "MouseThreshold2", "REG_SZ", "0");
        if (!Competitive) return;

        SnapshotBcd("disabledynamictick");
        SnapshotBcd("useplatformclock");
        Proc.Run("bcdedit.exe", "/set disabledynamictick yes");
        Proc.Run("bcdedit.exe", "/set useplatformclock false");
        Log.Write("bcdedit aplicado.", "AVISO");
    }

    public void TweakSsd()
    {
        Proc.Run("fsutil.exe", "behavior set DisableDeleteNotify 0");
        Proc.Run("schtasks.exe", "/change /tn \"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag\" /disable");
        if (Competitive)
        {
            SetReg(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters",
                "EnablePrefetcher", "REG_DWORD", "0");
            Proc.Run("powercfg.exe", "-h off");
        }
        Log.Write("SSD: modulo concluido.");
    }

    public void KillBackground()
    {
        var apps = new[] { "chrome.exe", "msedge.exe", "opera.exe", "firefox.exe", "spotify.exe", "OneDrive.exe", "epicgameslauncher.exe", "steamwebhelper.exe" };
        foreach (var app in apps)
        {
            var (ok, _) = Proc.Run("taskkill.exe", "/f /im " + app + " /t", 20000);
            Log.Write((ok ? "Encerrado: " : "Ausente: ") + app);
        }
    }

    public void ApplyAll(Action<string>? progress = null)
    {
        lock (_lock)
        {
            CreateRestorePoint();
            void Step(string label, Action action)
            {
                progress?.Invoke(label);
                Log.Write("A aplicar: " + label);
                action();
            }
            Step("GPU", TweakGpu);
            Step("Windows Gaming", TweakWindowsGaming);
            Step("Registo multimedia", TweakReg);
            Step("Plano de energia", TweakPower);
            Step("Servicos", TweakServices);
            Step("Temp e DNS", CleanTempAndRam);
            Step("Rede", TweakNetwork);
            Step("Input lag", TweakInputLag);
            Step("SSD", TweakSsd);
            Backup.Save();
            Log.Write("Pacote aplicado. Reinicia para kernel/GPU/BCD.");
        }
    }

    public void RevertAll()
    {
        lock (_lock)
        {
            foreach (var snap in Backup.Data.Registry.Values)
            {
                if (snap.Existed) Reg.Write(snap.Key, snap.Name, snap.Type, snap.Data);
                else Reg.DeleteValue(snap.Key, snap.Name);
            }
            foreach (var snap in Backup.Data.Services.Values)
            {
                var start = snap.Start is "auto" or "demand" or "disabled" ? snap.Start : "demand";
                Proc.Run("sc.exe", $"config \"{snap.Name}\" start= {start}");
                if (snap.Running) Proc.Run("sc.exe", "start \"" + snap.Name + "\"");
            }
            foreach (var (name, snap) in Backup.Data.Bcd)
            {
                if (snap.Existed && !string.IsNullOrEmpty(snap.Value))
                    Proc.Run("bcdedit.exe", $"/set {name} {snap.Value}");
                else
                    Proc.Run("bcdedit.exe", "/deletevalue " + name);
            }
            if (!string.IsNullOrEmpty(Backup.Data.PowerScheme))
                Proc.Run("powercfg.exe", "-setactive " + Backup.Data.PowerScheme);

            foreach (var (token, snap) in Backup.Data.Startup)
            {
                var split = token.Split("::", 2);
                if (split.Length == 2 && snap.Existed) Reg.Write(split[0], split[1], "REG_SZ", snap.Data ?? "");
            }
            Log.Write("Backup Mago restaurado.");
        }
    }

    public string CaptureNow()
    {
        lock (_lock)
        {
            Backup.Reset();
            foreach (var (key, name) in TrackedRegs) SnapshotReg(key, name, true);
            foreach (var svc in TrackedServices) SnapshotService(svc, true);
            foreach (var item in TrackedBcd) SnapshotBcd(item, true);
            Backup.RememberPower(CurrentPowerGuid(), true);
            Backup.Data.Created = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            Backup.Data.Notes = new List<string> { "Captura manual do estado do sistema" };
            Backup.Save();

            Directory.CreateDirectory(Paths.SnapshotDir);
            var dest = Path.Combine(Paths.SnapshotDir, $"mago_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.Copy(Paths.BackupFile, dest, true);
            Log.Write($"Backup guardado em {Paths.BackupFile} e {dest}");
            return dest;
        }
    }

    // ---------- utilitarios ----------

    public void VisualEffectsPerf() =>
        SetReg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", "REG_DWORD", "2");

    public void CleanShaderCache()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new[]
        {
            Path.Combine(local, "NVIDIA", "DXCache"),
            Path.Combine(local, "NVIDIA", "GLCache"),
            Path.Combine(local, "D3DSCache"),
            Path.Combine(local, "AMD", "DxCache"),
            Path.Combine(userProfile, "AppData", "LocalLow", "NVIDIA", "PerDriverVersion", "DXCache"),
        };
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                try { File.Delete(file); } catch { }
            Log.Write("Cache limpo: " + folder);
        }
        Log.Write("Shaders: o primeiro jogo depois disto pode ir mais lento a compilar.");
    }

    public void DisableFastStartup() =>
        SetReg(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", "REG_DWORD", "0");

    public void FlushDns()
    {
        var (ok, output) = Proc.Run("ipconfig.exe", "/flushdns");
        Log.Write(ok ? "DNS limpo" : "Falha DNS: " + output, ok ? "INFO" : "ERRO");
        CacheFlushed?.Invoke();
    }

    public event Action? CacheFlushed;

    public void RestartExplorer()
    {
        Proc.Run("taskkill.exe", "/f /im explorer.exe");
        Proc.Run("cmd.exe", "/c start explorer.exe");
        Log.Write("Explorer reiniciado.");
    }

    /// <summary>Teste de stresse de 15 s: CPU sintetica + amostras de CPU e RAM.</summary>
    public string StressTest(int seconds = 15)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var workers = new List<Task>();
        for (var i = 0; i < Math.Max(Environment.ProcessorCount, 4); i++)
            workers.Add(Task.Run(() =>
            {
                long x = 0;
                while (!cts.IsCancellationRequested) { x++; _ = x * x * x; }
            }, cts.Token));

        var cpu = new List<double>();
        var ram = new List<double>();
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(500);
            cpu.Add(SystemInfo.CpuUsage());
            ram.Add(SystemInfo.Memory().UsedPercent);
        }
        cts.Cancel();
        try { Task.WaitAll(workers.ToArray(), 1000); } catch { }

        var line = $"CPU {cpu.DefaultIfEmpty(0).Average():0.0}% | RAM {ram.DefaultIfEmpty(0).Average():0.0}%";
        Log.Write(line);
        return line;
    }
}
