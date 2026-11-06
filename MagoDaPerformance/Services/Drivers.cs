using System.Globalization;
using System.Text.Json;

namespace MagoDaPerformance;

public sealed class DriverRow
{
    public string Device { get; init; } = "";
    public string Class { get; init; } = "";
    public string Version { get; init; } = "";
    public string Date { get; init; } = "";
    public string Provider { get; init; } = "";
    public bool Old { get; init; }
}

/// <summary>Le os drivers assinados do sistema e consulta o Windows Update.</summary>
public static class Drivers
{
    /// <summary>Acima desta idade o driver e sinalizado como possivelmente desatualizado.</summary>
    private const int OldMonths = 24;

    public static List<DriverRow> Scan()
    {
        Log.Write("A verificar drivers...");
        const string ps =
            "$classes=@('Display','Net','MEDIA','HDC','SCSIADAPTER'); " +
            "Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceName -and $_.DriverVersion -and ($classes -contains $_.DeviceClass) } | " +
            "ForEach-Object { [pscustomobject]@{ Device=$_.DeviceName; Class=$_.DeviceClass; Version=$_.DriverVersion; " +
            "Date=$(if ($_.DriverDate) { $_.DriverDate.ToString('yyyy-MM-dd') } else { '' }); Provider=$_.DriverProviderName } }";

        var rows = Proc.Ps(ps, 150000);
        var result = new List<DriverRow>();
        foreach (var row in rows)
        {
            var device = Proc.Str(row, "Device");
            if (string.IsNullOrWhiteSpace(device)) continue;
            var provider = Proc.Str(row, "Provider") ?? "";
            // Dispositivos virtuais/miniports tem drivers genericos com datas antigas:
            // nao indicam nada ao utilizador e nunca "desatualizam" sozinhos.
            if (IsVirtualOrGeneric(device)) continue;

            var date = Proc.Str(row, "Date") ?? "";
            DateTime? parsed = DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d) ? d : null;
            var old = parsed.HasValue && parsed.Value < DateTime.Today.AddMonths(-OldMonths);
            result.Add(new DriverRow
            {
                Device = device.Trim(),
                Class = Proc.Str(row, "Class") ?? "",
                Version = (Proc.Str(row, "Version") ?? "").Trim(),
                Date = date,
                Provider = provider,
                Old = old,
            });
        }

        // Um dispositivo pode aparecer com varias versoes; fica a mais recente.
        var unique = result
            .GroupBy(r => r.Device, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.Date, StringComparer.Ordinal).First())
            .OrderByDescending(r => r.Old)
            .ThenBy(r => r.Date, StringComparer.Ordinal)
            .ToList();

        Log.Write($"{unique.Count} drivers analisados, {unique.Count(r => r.Old)} antigos.");
        return unique;
    }

    private static readonly string[] JunkDevices =
    {
        "WAN Miniport", "Virtual", "Hyper-V", "Root ", "Kernel Debug", "Storage Spaces",
        "Generic", "Microsoft Basic", "Standard ", "Remote Desktop", "Terminal",
    };

    private static bool IsVirtualOrGeneric(string device)
    {
        foreach (var junk in JunkDevices)
            if (device.Contains(junk, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Procura atualizacoes de driver no Windows Update.</summary>
    public static (bool Ok, string Message, List<string> Titles) CheckUpdates()
    {
        Log.Write("A consultar o Windows Update por drivers...");
        const string ps =
            "$s=New-Object -ComObject Microsoft.Update.Session; " +
            "try { $r=$s.CreateUpdateSearcher().Search(\"IsInstalled=0 and Type='Driver'\"); " +
            "$r.Updates | ForEach-Object { $_.Title } } catch { 'ERRO: ' + $_.Exception.Message }";

        var raw = Proc.Ps(ps, 300000)
            .Select(r => r.ValueKind == JsonValueKind.String ? r.GetString() : r.ToString())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .ToList();

        var error = raw.FirstOrDefault(t => t.StartsWith("ERRO:", StringComparison.OrdinalIgnoreCase));
        if (error != null)
            return (false, "Nao foi possivel consultar o Windows Update.\n" + error[5..].Trim(), new());

        if (raw.Count == 0)
        {
            Log.Write("Sem atualizacoes de driver no Windows Update.");
            return (true, "O Windows Update nao tem atualizacoes de driver para este PC neste momento.", new());
        }

        Log.Write($"{raw.Count} atualizacoes de driver no Windows Update.");
        return (true, $"{raw.Count} atualizacao(oes) de driver disponivel(is) no Windows Update:\n\n" +
                      string.Join("\n", raw.Take(15).Select(t => "- " + t)), raw);
    }

    /// <summary>Descarrega e instala as atualizacoes de driver do Windows Update.</summary>
    public static (bool Ok, string Message) InstallUpdates()
    {
        Log.Write("A instalar atualizacoes de driver...");
        const string ps =
            "$s=New-Object -ComObject Microsoft.Update.Session; " +
            "try { $res=$s.CreateUpdateSearcher().Search(\"IsInstalled=0 and Type='Driver'\"); " +
            "if ($res.Updates.Count -eq 0) { 'NADA' } else { " +
            "$col=New-Object -ComObject Microsoft.Update.UpdateColl; " +
            "foreach($u in $res.Updates){ if(-not $u.EulaAccepted){ [void]$u.AcceptEula() }; [void]$col.Add($u) }; " +
            "$d=$s.CreateUpdateDownloader(); $d.Updates=$col; [void]$d.Download(); " +
            "$i=$s.CreateUpdateInstaller(); $i.Updates=$col; $r=$i.Install(); " +
            "'OK|' + $r.ResultCode + '|' + $r.RebootRequired + '|' + $col.Count } } catch { 'ERRO: ' + $_.Exception.Message }";

        var lines = Proc.Ps(ps, 900000)
            .Select(r => r.ValueKind == JsonValueKind.String ? r.GetString() : r.ToString())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .ToList();

        var line = lines.FirstOrDefault() ?? "";
        if (line.StartsWith("ERRO:", StringComparison.OrdinalIgnoreCase))
            return (false, "Falha ao instalar atualizacoes.\n" + line[5..].Trim());
        if (line == "NADA")
            return (true, "Nao havia atualizacoes de driver para instalar.");
        if (!line.StartsWith("OK|", StringComparison.Ordinal))
            return (false, "Resposta inesperada do Windows Update: " + line);

        var parts = line.Split('|');
        var code = parts.Length > 1 ? parts[1] : "?";
        var reboot = parts.Length > 2 && parts[2].Equals("True", StringComparison.OrdinalIgnoreCase);
        var count = parts.Length > 3 ? parts[3] : "?";
        var ok = code == "2"; // 2 = orc (succeeded)
        Log.Write($"Windows Update terminou com resultado {code} ({count} drivers).");
        return (ok,
            ok
                ? $"Windows Update instalou {count} atualizacao(oes) de driver." + (reboot ? "\n\nReinicie o PC para concluir." : "")
                : $"O Windows Update devolveu o resultado {code}.\nVeja o Windows Update para detalhes.");
    }
}
