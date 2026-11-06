using System.Runtime.InteropServices;
using System.Text;

namespace MagoDaPerformance;

public sealed class HardwareInfo
{
    public string Text { get; init; } = "";
    public string Gpu { get; init; } = "";
    public List<string> DiskTypes { get; init; } = new();
    public string Os { get; init; } = "";
    public string Cpu { get; init; } = "";
    public double RamGb { get; init; }
    public List<string> Disks { get; init; } = new();
}

public static class SystemInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemStatus status);

    private static ulong ToU64(FileTime t) => ((ulong)t.High << 32) | t.Low;

    private static ulong _lastIdle, _lastKernel, _lastUser;

    /// <summary>CPU global desde a chamada anterior (0-100).</summary>
    public static double CpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var idleT = ToU64(idle);
        var kernelT = ToU64(kernel);
        var userT = ToU64(user);
        var idleDelta = idleT - _lastIdle;
        var totalDelta = (kernelT - _lastKernel) + (userT - _lastUser);
        _lastIdle = idleT;
        _lastKernel = kernelT;
        _lastUser = userT;
        if (totalDelta == 0) return 0;
        return Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100);
    }

    public static (double UsedPercent, double TotalGb) Memory()
    {
        var status = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return (0, 0);
        return (status.MemoryLoad, Math.Round(status.TotalPhys / Math.Pow(1024, 3), 2));
    }

    public static double DiskUsedPercent(string drive = "C:")
    {
        try
        {
            var info = new DriveInfo(drive);
            if (!info.IsReady || info.TotalSize == 0) return 0;
            return (1.0 - (double)info.AvailableFreeSpace / info.TotalSize) * 100.0;
        }
        catch { return 0; }
    }

    private static string FirstLine(string command)
    {
        var (ok, output) = Proc.Run("powershell", "-NoProfile -NonInteractive -Command \"" + command + "\"", 20000);
        if (!ok || string.IsNullOrWhiteSpace(output)) return "";
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
    }

    public static HardwareInfo Detect()
    {
        Log.Write("A identificar hardware...");
        var os = $"{RuntimeInformation.OSDescription}";
        var cpu = FirstLine("(Get-CimInstance Win32_Processor | Select-Object -First 1).Name");
        if (string.IsNullOrWhiteSpace(cpu)) cpu = RuntimeInformation.ProcessArchitecture.ToString();

        var gpu = FirstLine("(Get-CimInstance Win32_VideoController | Select-Object -First 1).Name");
        if (string.IsNullOrWhiteSpace(gpu)) gpu = "Nao identificada";

        var (_, ramGb) = Memory();
        var disks = new List<string>();
        var diskTypes = new List<string>();
        foreach (var row in Proc.Ps("Get-PhysicalDisk | ForEach-Object { $_.FriendlyName + ' [' + $_.MediaType + '/' + $_.HealthStatus + ']' }"))
        {
            var text = row.ValueKind == System.Text.Json.JsonValueKind.String ? row.GetString() : row.ToString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            disks.Add(text);
            var open = text.LastIndexOf('[');
            var close = text.LastIndexOf(']');
            if (open >= 0 && close > open)
                diskTypes.Add(text[(open + 1)..close].Split('/')[0].Trim());
        }

        var text2 =
            $"SO: {os}\n" +
            $"CPU: {cpu}\n" +
            $"Nucleos: {Environment.ProcessorCount} logicos\n" +
            $"RAM: {ramGb} GB\n" +
            $"GPU: {gpu}\n" +
            $"Discos: {(disks.Count > 0 ? string.Join(", ", disks) : "Desconhecido")}";

        Log.Write("Hardware detetado.");
        return new HardwareInfo
        {
            Text = text2,
            Gpu = gpu,
            DiskTypes = diskTypes,
            Os = os,
            Cpu = cpu,
            RamGb = ramGb,
            Disks = disks,
        };
    }

    /// <summary>BIOS, board, Secure Boot e TPM.</summary>
    public static Dictionary<string, string?> Bios()
    {
        Log.Write("A analisar BIOS/firmware...");
        var info = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var ps =
            "$bios=Get-CimInstance Win32_BIOS; $board=Get-CimInstance Win32_BaseBoard; " +
            "$cs=Get-CimInstance Win32_ComputerSystem; $prod=Get-CimInstance Win32_ComputerSystemProduct; " +
            "$pe=(Get-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control' -ErrorAction SilentlyContinue).PEFirmwareType; " +
            "$cpu=Get-CimInstance Win32_Processor | Select-Object -First 1; " +
            "[pscustomobject]@{Vendor=$bios.Manufacturer; Version=$bios.SMBIOSBIOSVersion; Date=$bios.ReleaseDate; " +
            "Serial=$bios.SerialNumber; Board=$board.Product; BoardMaker=$board.Manufacturer; Model=$cs.Model; " +
            "Maker=$cs.Manufacturer; SKU=$prod.Name; FirmwareType=$pe; Virt=$cpu.VirtualizationFirmwareEnabled; " +
            "Hypervisor=$cs.HypervisorPresent}";
        var rows = Proc.Ps(ps, 60000);
        if (rows.Count > 0)
            foreach (var name in new[] { "Vendor", "Version", "Date", "Serial", "Board", "BoardMaker", "Model", "Maker", "SKU", "FirmwareType", "Virt", "Hypervisor" })
                info[name] = Proc.Str(rows[0], name);

        var (sbOk, sbOut) = Proc.Run("powershell",
            "-NoProfile -NonInteractive -Command \"try { if (Confirm-SecureBootUEFI) {'Ligado'} else {'Desligado'} } catch { 'N/D' }\"", 30000);
        info["SecureBoot"] = sbOk || !string.IsNullOrWhiteSpace(sbOut)
            ? sbOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "N/D"
            : "N/D";

        var tpm = Proc.Ps("try { Get-Tpm | Select-Object TpmPresent,TpmReady,ManufacturerVersion } catch { [pscustomobject]@{TpmPresent=$false} }", 40000);
        info["TpmPresent"] = tpm.Count > 0 ? Proc.Str(tpm[0], "TpmPresent") : "N/D";
        info["TpmReady"] = tpm.Count > 0 ? Proc.Str(tpm[0], "TpmReady") : "N/D";

        Log.Write("Analise de BIOS concluida.");
        return info;
    }

    public static string BiosText(Dictionary<string, string?> info)
    {
        var fw = info.GetValueOrDefault("FirmwareType") switch { "1" => "Legacy BIOS", "2" => "UEFI", var v => v ?? "?" };
        var date = info.GetValueOrDefault("Date") ?? "";
        var age = "";
        var match = System.Text.RegularExpressions.Regex.Match(date, "(20\\d{2})");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var year) && DateTime.Now.Year - year >= 2)
            age = $"\nNota: firmware de {year} - vale a pena verificar se o fabricante tem versao mais recente.";

        return
            $"Fabricante: {info.GetValueOrDefault("Maker") ?? info.GetValueOrDefault("Vendor")}\n" +
            $"Modelo: {info.GetValueOrDefault("Model")}\n" +
            $"Motherboard: {info.GetValueOrDefault("BoardMaker")} {info.GetValueOrDefault("Board")}\n" +
            $"BIOS/UEFI: {info.GetValueOrDefault("Vendor")}  {info.GetValueOrDefault("Version")}\n" +
            $"Data: {date}\n" +
            $"N. de serie / service tag: {info.GetValueOrDefault("Serial")}\n" +
            $"Firmware: {fw}\n" +
            $"Secure Boot: {info.GetValueOrDefault("SecureBoot")}\n" +
            $"TPM presente: {info.GetValueOrDefault("TpmPresent")}   pronto: {info.GetValueOrDefault("TpmReady")}\n" +
            $"Virtualizacao na firmware: {info.GetValueOrDefault("Virt")}\n" +
            $"Hypervisor ativo: {info.GetValueOrDefault("Hypervisor")}{age}";
    }

    public static string VendorSupportUrl(Dictionary<string, string?> info)
    {
        var maker = info.GetValueOrDefault("Maker") ?? info.GetValueOrDefault("Vendor") ?? "";
        var serial = (info.GetValueOrDefault("Serial") ?? "").Trim();
        var model = info.GetValueOrDefault("Model") ?? "";
        var board = info.GetValueOrDefault("Board") ?? "";
        var blob = $"{maker} {model} {board}".ToLowerInvariant();
        var query = !string.IsNullOrWhiteSpace(serial) ? serial : !string.IsNullOrWhiteSpace(board) ? board : model;

        if (blob.Contains("dell"))
            return string.IsNullOrWhiteSpace(serial)
                ? "https://www.dell.com/support"
                : $"https://www.dell.com/support/home/pt-br/product-support/servicetag/{serial}";
        if (blob.Contains("lenovo")) return $"https://pcsupport.lenovo.com/us/en/search?query={Uri.EscapeDataString(query)}";
        if (blob.Contains("hp") || blob.Contains("hewlett")) return $"https://support.hp.com/pt-pt/search?q={Uri.EscapeDataString(query)}";
        if (blob.Contains("asus")) return $"https://www.asus.com/support/search/?SearchKey={Uri.EscapeDataString(board)}";
        if (blob.Contains("msi")) return $"https://www.msi.com/search/{Uri.EscapeDataString(board)}";
        if (blob.Contains("gigabyte") || blob.Contains("aorus")) return $"https://www.gigabyte.com/Search?keyword={Uri.EscapeDataString(board)}";
        if (blob.Contains("asrock")) return "https://www.asrock.com/support/index.asp";
        if (blob.Contains("acer")) return "https://www.acer.com/support";
        if (blob.Contains("microsoft")) return "https://support.microsoft.com/surface";
        return $"https://www.google.com/search?q={Uri.EscapeDataString(maker + " " + board + " BIOS update")}";
    }
}
