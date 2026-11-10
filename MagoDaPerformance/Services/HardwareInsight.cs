using System.Globalization;

namespace MagoDaPerformance;

public sealed record MemoryModule(int SpeedMhz, int ConfiguredMhz, double Gb, string Slot);

public sealed record CpuState(double CurrentMhz, double MaxMhz, double LoadPercent, double? TempC);

public sealed record DiskInfo(string Name, string Media, string Health, double SizeGb, double FreeGb);

public sealed record ProcessLoad(string Name, int Pid, double CpuPercent, double RamMb);

/// <summary>
/// Diagnostico do que realmente limita o desempenho: RAM abaixo do anunciado (XMP/EXPO),
/// CPU limitada por energia/temperatura, saude do disco e processos que comem CPU.
/// As somas/vereditos sao funcoes puras para poderem ser testadas sem hardware.
/// </summary>
public static class HardwareInsight
{
    public static List<MemoryModule> MemoryModules()
    {
        const string script =
            "Get-CimInstance Win32_PhysicalMemory | ForEach-Object { [pscustomobject]@{ " +
            "Speed=[int]$_.Speed; Configured=[int]$_.ConfiguredClockSpeed; " +
            "GB=[math]::Round($_.Capacity / 1GB, 1); Slot=[string]$_.DeviceLocator } }";

        var modules = new List<MemoryModule>();
        foreach (var row in Proc.Ps(script, 40000))
        {
            var speed = Number(Proc.Str(row, "Speed"));
            var configured = Number(Proc.Str(row, "Configured"));
            var gb = Number(Proc.Str(row, "GB"));
            if (speed <= 0 && configured <= 0) continue;
            modules.Add(new MemoryModule((int)speed, (int)configured, gb, Proc.Str(row, "Slot") ?? ""));
        }
        return modules;
    }

    /// <summary>
    /// Veredito da RAM. O caso que interessa: modulos de 3200 MT/s a correr a 2133 MT/s
    /// significa XMP/EXPO desligado na BIOS — ganho real em jogos por CPU.
    /// </summary>
    public static string MemorySummary(IReadOnlyList<MemoryModule> modules)
    {
        if (modules.Count == 0) return "Memoria: nao foi possivel ler os modulos.";

        var totalGb = modules.Sum(m => m.Gb);
        var announced = modules.Max(m => Math.Max(m.SpeedMhz, m.ConfiguredMhz));
        var running = modules.Min(m => m.ConfiguredMhz > 0 ? m.ConfiguredMhz : m.SpeedMhz);

        if (announced > 0 && running > 0 && running < announced * 0.95)
            return $"XMP/EXPO desligado: modulos de {announced} MT/s a correr a {running} MT/s " +
                   $"({totalGb:0.#} GB). Ativar o perfil na BIOS costuma dar ganho em jogos por CPU.";
        if (announced <= 0)
            return $"Memoria: {totalGb:0.#} GB (velocidade nao reportada pelo Windows).";
        return $"Memoria: {totalGb:0.#} GB a {running} MT/s, na velocidade anunciada ({modules.Count} modulo(s)).";
    }

    public static CpuState Cpu()
    {
        const string script =
            "$p = Get-CimInstance Win32_Processor | Select-Object -First 1; " +
            "$t = (Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction SilentlyContinue " +
            "| Select-Object -First 1).CurrentTemperature; " +
            "[pscustomobject]@{ Current=[int]$p.CurrentClockSpeed; Max=[int]$p.MaxClockSpeed; " +
            "Load=[int]$p.LoadPercentage; TempK=[double]$t }";

        foreach (var row in Proc.Ps(script, 30000))
        {
            var current = Number(Proc.Str(row, "Current"));
            var max = Number(Proc.Str(row, "Max"));
            var load = Number(Proc.Str(row, "Load"));
            var tempK = Number(Proc.Str(row, "TempK"));
            double? temp = tempK > 2000 ? Math.Round(tempK / 10.0 - 273.15, 1) : null;
            if (temp is > 120 or < 20) temp = null;
            if (max <= 0 && current <= 0) continue;
            return new CpuState(current, max, load, temp);
        }
        return new CpuState(0, 0, 0, null);
    }

    public static string CpuSummary(CpuState state)
    {
        if (state.MaxMhz <= 0 && state.CurrentMhz <= 0)
            return "CPU: frequencia nao reportada por este Windows.";

        var clock = state.MaxMhz > 0
            ? $"{state.CurrentMhz / 1000.0:0.00} GHz (max {state.MaxMhz / 1000.0:0.00} GHz)"
            : $"{state.CurrentMhz:0} MHz";
        var temp = state.TempC is { } t ? $" | {t:0} C" : "";
        var hint = "";
        if (state.MaxMhz > 0 && state.LoadPercent >= 60 && state.CurrentMhz < state.MaxMhz * 0.7)
            hint = " - abaixo do maximo com carga alta: possivel limite termico ou de energia";
        return $"CPU: {clock}, {state.LoadPercent:0}% de carga{temp}{hint}.";
    }

    public static List<DiskInfo> Disks()
    {
        const string script =
            "Get-PhysicalDisk | ForEach-Object { [pscustomobject]@{ Name=[string]$_.FriendlyName; " +
            "Media=[string]$_.MediaType; Health=[string]$_.HealthStatus; " +
            "GB=[math]::Round($_.Size / 1GB, 0) } }";

        var free = FreeSpaceByLetter();
        var disks = new List<DiskInfo>();
        foreach (var row in Proc.Ps(script, 40000))
        {
            var name = Proc.Str(row, "Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var gb = Number(Proc.Str(row, "GB"));
            var letter = free.Keys.FirstOrDefault(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
            disks.Add(new DiskInfo(
                name.Trim(),
                Proc.Str(row, "Media") ?? "",
                Proc.Str(row, "Health") ?? "",
                gb,
                letter != null ? free[letter] : -1));
        }
        return disks;
    }

    public static string DiskSummary(IReadOnlyList<DiskInfo> disks)
    {
        if (disks.Count == 0) return "Discos: informacao indisponivel.";
        var lines = new List<string>();
        foreach (var disk in disks)
        {
            var media = string.IsNullOrWhiteSpace(disk.Media) ? "" : $" {disk.Media}";
            var health = string.IsNullOrWhiteSpace(disk.Health) ? "" : $", saude {disk.Health}";
            var size = disk.SizeGb > 0 ? $"{disk.SizeGb:0} GB" : "tamanho desconhecido";
            var free = disk.FreeGb >= 0 ? $", {disk.FreeGb:0} GB livres" : "";
            lines.Add($"{disk.Name} ({size}{media}{health}{free})");
        }
        return "Discos: " + string.Join("; ", lines) + ".";
    }

    /// <summary>Processos que mais CPU consomem (percentagem normalizada como no Gestor de Tarefas).</summary>
    public static List<ProcessLoad> TopProcesses(int count = 6)
    {
        var script =
            "Get-CimInstance Win32_PerfFormattedData_PerfProc_Process | " +
            "Where-Object { $_.Name -notmatch '^(_Total|Idle)$' } | " +
            "Sort-Object PercentProcessorTime -Descending | Select-Object -First " + Math.Max(1, count) + " | " +
            "ForEach-Object { [pscustomobject]@{ Name=[string]$_.Name; Pid=[int]$_.IDProcess; " +
            "Cpu=[int]$_.PercentProcessorTime; RamMB=[math]::Round($_.WorkingSetPrivate / 1MB, 0) } }";

        var cpuCount = Math.Max(Environment.ProcessorCount, 1);
        var list = new List<ProcessLoad>();
        foreach (var row in Proc.Ps(script, 40000))
        {
            var name = Proc.Str(row, "Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            // O contador e' por nucleo (pode passar 100); dividir aproxima o Gestor de Tarefas.
            var cpu = Math.Round(Number(Proc.Str(row, "Cpu")) / cpuCount, 1);
            list.Add(new ProcessLoad(name.Trim(), (int)Number(Proc.Str(row, "Pid")), cpu, Number(Proc.Str(row, "RamMB"))));
        }
        return list;
    }

    private static Dictionary<string, double> FreeSpaceByLetter()
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                map[drive.Name.TrimEnd('\\')] = drive.AvailableFreeSpace / Math.Pow(1024, 3);
            }
        }
        catch { }
        return map;
    }

    private static double Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
