using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MagoDaPerformance;

public sealed record DnsProvider(string Name, string Primary, string Secondary, bool IsCurrent = false);

public sealed record DnsResult(DnsProvider Provider, bool Ok, double? Median, double Loss, List<double> Samples);

public static class Dns
{
    public const string AutoLabel = "Automatico (DHCP)";

    public static readonly DnsProvider[] Providers =
    {
        new("Cloudflare", "1.1.1.1", "1.0.0.1"),
        new("Google", "8.8.8.8", "8.8.4.4"),
        new("Quad9", "9.9.9.9", "149.112.112.112"),
        new("OpenDNS", "208.67.222.222", "208.67.220.220"),
        new("AdGuard", "94.140.14.14", "94.140.15.15"),
    };

    public static readonly string[] ProbeNames =
    {
        "www.google.com", "www.cloudflare.com", "www.microsoft.com", "www.amazon.com", "www.wikipedia.org",
    };

    /// <summary>Resolvedores a testar; o DNS atual entra primeiro se nao for um deles.</summary>
    public static List<DnsProvider> Candidates(string? currentIp)
    {
        var list = new List<DnsProvider>();
        currentIp = (currentIp ?? "").Trim();
        if (currentIp.Length > 0 && !Providers.Any(p => p.Primary == currentIp || p.Secondary == currentIp))
            list.Add(new DnsProvider("DNS atual", currentIp, "", true));
        foreach (var p in Providers)
            list.Add(p with { IsCurrent = currentIp.Length > 0 && (p.Primary == currentIp || p.Secondary == currentIp) });
        return list;
    }

    private static byte[] BuildQuery(string name, out int txid)
    {
        var labels = name.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length == 0) throw new ArgumentException("nome DNS vazio");
        txid = Random.Shared.Next(0, 65536);

        var ms = new MemoryStream();
        ms.WriteByte((byte)(txid >> 8));
        ms.WriteByte((byte)(txid & 0xFF));
        foreach (var b in new byte[] { 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }) ms.WriteByte(b);
        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        ms.WriteByte(0);
        ms.Write(new byte[] { 0, 1, 0, 1 }, 0, 4);
        return ms.ToArray();
    }

    /// <summary>Latencia (ms) de uma consulta A feita diretamente ao resolvedor.</summary>
    public static (bool Ok, double? Ms, int? Rcode) Probe(string server, string name, int timeoutMs = 1200)
    {
        byte[] packet;
        int txid;
        try { packet = BuildQuery(name, out txid); }
        catch { return (false, null, null); }

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.ReceiveTimeout = timeoutMs;
            udp.Connect(new IPEndPoint(IPAddress.Parse(server), 53));

            var clock = Stopwatch.StartNew();
            udp.Send(packet, packet.Length);
            while (true)
            {
                IPEndPoint? remote = null;
                var data = udp.Receive(ref remote);
                if (data.Length < 12) continue;
                var id = (data[0] << 8) | data[1];
                if (id != txid) continue;
                var flags = (data[2] << 8) | data[3];
                var answers = (data[6] << 8) | data[7];
                var rcode = flags & 0x000F;
                return (rcode == 0 && answers > 0, clock.Elapsed.TotalMilliseconds, rcode);
            }
        }
        catch { return (false, null, null); }
    }

    public static double? Median(IEnumerable<double> values)
    {
        var ordered = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList();
        if (ordered.Count == 0) return null;
        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 1
            ? Math.Round(ordered[middle], 1)
            : Math.Round((ordered[middle - 1] + ordered[middle]) / 2.0, 1);
    }

    /// <summary>Cada servidor leva uma consulta de aquecimento; a latencia reportada e a mediana.</summary>
    public static List<DnsResult> Benchmark(IEnumerable<DnsProvider>? candidates = null, IEnumerable<string>? names = null, int timeoutMs = 1200)
    {
        var list = (candidates ?? Candidates(null)).ToList();
        var probeNames = (names ?? ProbeNames).ToList();
        var results = new List<DnsResult>();

        foreach (var provider in list)
        {
            var warm = Probe(provider.Primary, probeNames[0], timeoutMs);
            if (!warm.Ok)
            {
                results.Add(new DnsResult(provider, false, null, 100, new List<double>()));
                continue;
            }
            var samples = new List<double>();
            var failures = 0;
            foreach (var name in probeNames)
            {
                var probe = Probe(provider.Primary, name, timeoutMs);
                if (probe.Ok && probe.Ms != null) samples.Add(Math.Round(probe.Ms!.Value, 1));
                else failures++;
            }
            var loss = probeNames.Count == 0 ? 0 : Math.Round(failures * 100.0 / probeNames.Count);
            results.Add(new DnsResult(provider, samples.Count > 0, Median(samples), loss, samples));
        }

        return results
            .OrderBy(r => r.Ok ? 0 : 1)
            .ThenBy(r => r.Loss)
            .ThenBy(r => r.Median ?? 1e9)
            .ToList();
    }

    public static string Ranking(IEnumerable<DnsResult> results)
    {
        var lines = new List<string>();
        var index = 1;
        foreach (var r in results)
        {
            var mark = r.Provider.IsCurrent ? "   <- atual" : "";
            lines.Add(r.Ok
                ? $"{index}. {r.Provider.Name,-12} {r.Provider.Primary,-15} {r.Median,6:0.0} ms   perda {r.Loss:0}%{mark}"
                : $"{index}. {r.Provider.Name,-12} {r.Provider.Primary,-15} sem resposta{mark}");
            index++;
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static string Recommendation(IEnumerable<DnsResult> results)
    {
        var usable = results.Where(r => r.Ok && r.Median != null).ToList();
        if (usable.Count == 0)
            return "Nenhum resolvedor respondeu - a rede pode estar a bloquear consultas DNS externas.";
        var best = usable[0];
        var current = usable.FirstOrDefault(r => r.Provider.IsCurrent);
        var text = $"Melhor desempenho: {best.Provider.Name} ({best.Provider.Primary}) com {best.Median:0.0} ms.";
        if (current == null) return text;
        if (ReferenceEquals(current, best) || current.Provider.Primary == best.Provider.Primary)
            return text + " E o DNS que ja estas a usar.";
        return text + $" O DNS atual ({current.Provider.Primary}) responde em {current.Median:0.0} ms.";
    }

    public static List<string> Choices(IEnumerable<DnsResult>? results = null)
    {
        var list = new List<string> { AutoLabel };
        foreach (var r in results ?? Candidates(null).Select(p => new DnsResult(p, true, null, 0, new())))
        {
            if (string.IsNullOrEmpty(r.Provider.Primary)) continue;
            if (r.Provider.Name == "DNS atual")
            {
                list.Add($"{r.Provider.Name} ({r.Provider.Primary})");
                continue;
            }
            var label = $"{r.Provider.Name} - {r.Provider.Primary}";
            if (!string.IsNullOrEmpty(r.Provider.Secondary)) label += $" / {r.Provider.Secondary}";
            list.Add(label);
        }
        return list;
    }

    public static DnsProvider? ChoiceToProvider(string? choice, IEnumerable<DnsResult>? results = null)
    {
        choice = (choice ?? "").Trim();
        if (choice.Length == 0 || choice.StartsWith("Automatico")) return null;
        foreach (var r in results ?? Candidates(null).Select(p => new DnsResult(p, true, null, 0, new())))
            if (!string.IsNullOrEmpty(r.Provider.Primary) && choice.StartsWith(r.Provider.Name))
                return r.Provider;
        return null;
    }

    /// <summary>
    /// Argumentos completos de netsh para fixar (ou limpar) o DNS de um adaptador.
    /// O nome vai entre aspas porque pode ter espacos ("Ethernet 2").
    /// </summary>
    public static List<string> ChangeCommands(string alias, string? primary, string? secondary = "")
    {
        if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("adaptador vazio");
        var target = $"name=\"{alias}\"";
        if (string.IsNullOrWhiteSpace(primary))
            return new List<string> { $"interface ipv4 set dns {target} dhcp" };

        var commands = new List<string>
        {
            $"interface ipv4 set dns {target} static {primary} primary",
        };
        if (!string.IsNullOrWhiteSpace(secondary))
            commands.Add($"interface ipv4 add dns {target} {secondary} index=2");
        return commands;
    }
}
