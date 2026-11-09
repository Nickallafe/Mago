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

    /// <summary>Grava logo o backup: um modulo avulso tambem tem de poder ser restaurado.</summary>
    private void Persist()
    {
        try { Backup.Save(); }
        catch (Exception ex) { Log.Write("Falha ao gravar backup: " + ex.Message, "ERRO"); }
    }

    public RegSnap SnapshotReg(string key, string name, bool overwrite = false)
    {
        var snap = Reg.Snapshot(key, name);
        Backup.RememberReg(key, name, snap, overwrite);
        Persist();
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
        Persist();
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

    /// <summary>O bcdedit traduz os booleanos ("Sim"/"Nao"); normaliza para yes/no.</summary>
    internal static string? NormalizeBcdToken(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        if (v is "yes" or "sim" or "true" or "on" or "ativado" or "habilitado") return "yes";
        if (v is "no" or "nao" or "não" or "false" or "off" or "desativado" or "desabilitado") return "no";
        return value?.Trim();
    }

    internal static string? ReadBcdValue(string name)
    {
        var (ok, output) = Proc.Run("bcdedit.exe", "/enum {current}");
        if (!ok) return null;
        string? value = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) value = NormalizeBcdToken(parts[^1]);
        }
        return value;
    }

    public BcdSnap SnapshotBcd(string name, bool overwrite = false)
    {
        var value = ReadBcdValue(name);
        var snap = new BcdSnap { Existed = value != null, Value = value };
        Backup.RememberBcd(name, snap, overwrite);
        Persist();
        return snap;
    }

    /// <summary>
    /// Grava no BCD e confirma por leitura. O bcdedit mostra Sim/Nao em pt-BR, mas aceita
    /// os tokens em ingles; sem esta verificacao uma falha ficava invisivel no log.
    /// </summary>
    public bool SetBcd(string name, string value, bool snapshot = true)
    {
        if (snapshot) SnapshotBcd(name);
        var (ok, output) = Proc.Run("bcdedit.exe", $"/set {name} {value}");
        if (!ok)
        {
            Log.Write($"bcdedit /set {name} {value} falhou: {output.Trim()}", "ERRO");
            return false;
        }
        var read = ReadBcdValue(name);
        var expected = NormalizeBcdToken(value);
        if (!string.Equals(read, expected, StringComparison.OrdinalIgnoreCase))
            Log.Write($"bcdedit {name}: esperado {expected}, lido {read ?? "ausente"}", "AVISO");
        return true;
    }

    // ---------- netsh / tarefas agendadas / hibernacao ----------

    /// <summary>Nome mostrado por `netsh int tcp show global` -> parametro de /set.</summary>
    internal static readonly Dictionary<string, string> TcpParamNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Receive Window Auto-Tuning Level"] = "autotuninglevel",
        ["ECN Capability"] = "ecncapability",
        ["RFC 1323 Timestamps"] = "timestamps",
        ["Receive-Side Scaling State"] = "rss",
        ["Receive Segment Coalescing State"] = "rsc",
        ["Add-On Congestion Control Provider"] = "congestionprovider",
    };

    internal static Dictionary<string, string> ReadTcpGlobals()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var (ok, output) = Proc.Run("netsh.exe", "int tcp show global", 30000);
        if (!ok) return map;
        foreach (var raw in output.Split('\n'))
        {
            var idx = raw.IndexOf(':');
            if (idx <= 0) continue;
            var key = raw[..idx].Trim();
            var value = raw[(idx + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0) continue;
            map[key] = value;
        }
        return map;
    }

    /// <summary>Repoe so os parametros que o Mago altera, com o valor que o PC tinha.</summary>
    internal static void RestoreTcpGlobals(Dictionary<string, string> before)
    {
        foreach (var (display, value) in before)
        {
            if (!TcpParamNames.TryGetValue(display, out var param)) continue;
            var (ok, output) = Proc.Run("netsh.exe", $"int tcp set global {param}={value.Replace(" ", "")}");
            Log.Write(ok ? $"TCP {param}={value} reposto." : $"TCP {param} falhou: {output.Trim()}", ok ? "INFO" : "AVISO");
        }
    }

    private static bool? DefragTaskEnabled()
    {
        var (ok, output) = Proc.Run("powershell",
            "-NoProfile -NonInteractive -Command \"(Get-ScheduledTask -TaskPath '\\Microsoft\\Windows\\Defrag\\' -TaskName 'ScheduledDefrag').State\"",
            30000);
        if (!ok) return null;
        var text = output.Trim();
        if (text.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) return false;
        return text.Length > 0 ? true : null;
    }

    private static bool? MemoryCompressionEnabled()
    {
        var (ok, output) = Proc.Run("powershell",
            "-NoProfile -NonInteractive -Command \"(Get-MMAgent).MemoryCompression\"", 30000);
        if (!ok) return null;
        var text = output.Trim();
        if (text.Equals("True", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Equals("False", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static bool HibernationOn()
    {
        var raw = Reg.ReadString(@"HKLM\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled");
        return raw == null || raw.Trim() != "0";
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
        if (Backup.Data.Touched.MemoryCompressionWasEnabled == null)
            Backup.Data.Touched.MemoryCompressionWasEnabled = MemoryCompressionEnabled();
        var (ok, output) = Proc.Run("powershell", "-NoProfile -NonInteractive -Command \"Disable-MMAgent -MemoryCompression\"");
        if (ok) Backup.Data.Touched.MemoryCompression = true;
        Persist();
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

    private static readonly System.Text.RegularExpressions.Regex GuidPattern = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    public void TweakPower()
    {
        Backup.RememberPower(CurrentPowerGuid());
        Persist();
        // -duplicatescheme devolve um GUID novo; o GUID do template nao serve para -setactive.
        var (_, dupOut) = Proc.Run("powercfg.exe", "-duplicatescheme " + UltimateGuid);
        var duplicated = GuidPattern.Match(dupOut);
        var target = duplicated.Success ? duplicated.Value : UltimateGuid;

        // O nome do esquema e traduzido ("Desempenho maximo" em pt-BR), por isso nunca
        // comparamos com um literal: usamos o nome que o proprio powercfg devolveu.
        var nameMatch = System.Text.RegularExpressions.Regex.Match(dupOut, @"\(([^)]+)\)\s*$");
        var templateName = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : "";

        // Reaproveita um esquema com o mesmo nome para nao acumular duplicados a cada utilizacao.
        if (duplicated.Success && templateName.Length > 0)
        {
            var (_, list) = Proc.Run("powercfg.exe", "-list");
            foreach (var line in list.Split('\n'))
            {
                if (!line.Contains(templateName, StringComparison.OrdinalIgnoreCase)) continue;
                var existing = GuidPattern.Match(line);
                if (!existing.Success || existing.Value.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                Proc.Run("powercfg.exe", "-delete " + target);
                target = existing.Value;
                Log.Write("Esquema de energia existente reaproveitado.");
                break;
            }
        }

        var (ok, output) = Proc.Run("powercfg.exe", "-setactive " + target);
        Log.Write(ok ? "Plano de energia ativo (" + target + ")" : "Falha no plano de energia: " + output,
            ok ? "INFO" : "AVISO");
        if (ok && templateName.Length > 0) CleanDuplicatePowerSchemes(target, templateName);
    }

    /// <summary>Mantem um unico esquema do template e apaga os duplicados que se acumularam.</summary>
    private int CleanDuplicatePowerSchemes(string keepGuid, string templateName)
    {
        var (ok, list) = Proc.Run("powercfg.exe", "-list");
        if (!ok) return 0;
        var active = CurrentPowerGuid() ?? "";
        var removed = 0;
        foreach (var line in list.Split('\n'))
        {
            if (!line.Contains(templateName, StringComparison.OrdinalIgnoreCase)) continue;
            var match = GuidPattern.Match(line);
            if (!match.Success) continue;
            var guid = match.Value;
            if (guid.Equals(keepGuid, StringComparison.OrdinalIgnoreCase)) continue;
            if (guid.Equals(active, StringComparison.OrdinalIgnoreCase)) continue;
            if (guid.Equals(Backup.Data.PowerScheme, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Proc.Run("powercfg.exe", "-delete " + guid).Ok) continue;
            removed++;
            Log.Write("Esquema de energia duplicado removido: " + guid);
        }
        if (removed > 0) Log.Write($"{removed} esquema(s) duplicado(s) de {templateName} removido(s).");
        return removed;
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

    /// <summary>Limpa ficheiros temporarios e a cache DNS. Nao liberta RAM.</summary>
    public void CleanTempAndDns()
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
        // Guarda os valores reais: o restauro nao pode adivinhar o que era "padrao" neste PC.
        if (Backup.Data.Touched.TcpBefore.Count == 0)
            Backup.Data.Touched.TcpBefore = ReadTcpGlobals();
        Proc.Run("netsh.exe", "int tcp set global autotuninglevel=normal");
        if (Competitive)
        {
            Proc.Run("netsh.exe", "int tcp set global ecncapability=disabled");
            Proc.Run("netsh.exe", "int tcp set global timestamps=disabled");
        }
        Backup.Data.Touched.TcpGlobals = true;
        Backup.Save();
        Log.Write("Rede: modulo concluido.");
    }

    public void TweakInputLag()
    {
        SetReg(@"HKCU\Control Panel\Mouse", "MouseSpeed", "REG_SZ", "0");
        SetReg(@"HKCU\Control Panel\Mouse", "MouseThreshold1", "REG_SZ", "0");
        SetReg(@"HKCU\Control Panel\Mouse", "MouseThreshold2", "REG_SZ", "0");
        if (!Competitive) return;

        SetBcd("disabledynamictick", "yes");
        SetBcd("useplatformclock", "no");
        Log.Write("bcdedit aplicado.", "AVISO");
    }

    public void TweakSsd()
    {
        if (Backup.Data.Touched.TrimBefore == null &&
            int.TryParse(Reg.ReadString(@"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem", "DisableDeleteNotify"),
                out var trimBefore))
            Backup.Data.Touched.TrimBefore = trimBefore;
        Proc.Run("fsutil.exe", "behavior set DisableDeleteNotify 0");
        // Desativar a desfragmentacao agendada so faz sentido em SSD; em HDD e util.
        if (HasHdd)
            Log.Write("HDD detetado: desfragmentacao agendada mantida.", "AVISO");
        else
        {
            if (Backup.Data.Touched.DefragWasEnabled == null)
                Backup.Data.Touched.DefragWasEnabled = DefragTaskEnabled();
            Proc.Run("schtasks.exe", "/change /tn \"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag\" /disable");
        }
        if (Competitive)
        {
            SetReg(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters",
                "EnablePrefetcher", "REG_DWORD", "0");
            if (Backup.Data.Touched.HibernateWasEnabled == null)
                Backup.Data.Touched.HibernateWasEnabled = HibernationOn();
            Proc.Run("powercfg.exe", "-h off");
        }
        Backup.Data.Touched.Trim = true;
        if (!HasHdd) Backup.Data.Touched.DefragTask = true;
        Backup.Save();
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
            Step("Temp e DNS", CleanTempAndDns);
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
                    SetBcd(name, snap.Value, snapshot: false);
                else
                    Proc.Run("bcdedit.exe", "/deletevalue " + name);
            }
            if (!string.IsNullOrEmpty(Backup.Data.PowerScheme))
                Proc.Run("powercfg.exe", "-setactive " + Backup.Data.PowerScheme);

            // Alteracoes que nao cabem num valor de registo: repor o estado guardado.
            var touched = Backup.Data.Touched;
            if (touched.DefragTask)
            {
                if (touched.DefragWasEnabled == false)
                {
                    Proc.Run("schtasks.exe", "/change /tn \"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag\" /disable");
                    Log.Write("Desfragmentacao agendada mantida desligada (estava assim antes).");
                }
                else
                {
                    Proc.Run("schtasks.exe", "/change /tn \"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag\" /enable");
                    Log.Write("Desfragmentacao agendada reativada.");
                }
            }
            if (touched.MemoryCompression)
            {
                if (touched.MemoryCompressionWasEnabled != false)
                {
                    Proc.Run("powershell", "-NoProfile -NonInteractive -Command \"Enable-MMAgent -MemoryCompression\"");
                    Log.Write("Compressao de memoria reativada.");
                }
                else Log.Write("Compressao de memoria ja estava desligada: mantida.");
            }
            if (touched.HibernateWasEnabled == true)
            {
                Proc.Run("powercfg.exe", "-h on");
                Log.Write("Hibernacao reativada.");
            }
            if (touched.TcpGlobals)
            {
                if (touched.TcpBefore.Count > 0) RestoreTcpGlobals(touched.TcpBefore);
                else
                {
                    Proc.Run("netsh.exe", "int tcp set global autotuninglevel=normal");
                    Proc.Run("netsh.exe", "int tcp set global ecncapability=disabled");
                    Proc.Run("netsh.exe", "int tcp set global timestamps=disabled");
                    Log.Write("Parametros TCP repostos.");
                }
            }
            if (touched.Trim)
            {
                var target = touched.TrimBefore ?? 0;
                Proc.Run("fsutil.exe", $"behavior set DisableDeleteNotify {target}");
                Log.Write(target == 0 ? "TRIM reativado." : "TRIM reposto no estado original (desativado).");
            }
            Backup.Data.Touched = new TouchedFlags();
            Backup.Save();

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
