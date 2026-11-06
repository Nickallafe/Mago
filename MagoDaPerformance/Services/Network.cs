using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MagoDaPerformance;

public sealed class PingStats
{
    public int Sent { get; init; }
    public int Received { get; init; }
    public double Loss { get; init; }
    public double? Min { get; init; }
    public double? Avg { get; init; }
    public double? Max { get; init; }
    public double? Jitter { get; init; }
    public bool HasSamples => Received > 0;

    public override string ToString() => HasSamples
        ? $"min {Min:0.0} / media {Avg:0.0} / max {Max:0.0} ms · jitter {Jitter:0.0} ms · perda {Loss:0.0}%"
        : $"sem resposta · perda {Loss:0.0}%";
}

public sealed record NetRoute(string Adapter, string? Gateway, List<string> Dns, string? Link);

public static class Net
{
    private const string DownUrl = "https://speed.cloudflare.com/__down?bytes=";
    private const string UpUrl = "https://speed.cloudflare.com/__up";
    private const string Agent = "MagoDaPerformance/6.0";

    private static HttpClient Client(int timeoutSeconds) => new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(6),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        DefaultRequestHeaders = { { "User-Agent", Agent }, { "Accept", "*/*" }, { "Cache-Control", "no-store" } },
    };

    public static PingStats PingHost(string host, int count = 5, int timeoutMs = 1500)
    {
        var times = new List<double>();
        using var ping = new Ping();
        var buffer = new byte[32];
        for (var i = 0; i < count; i++)
        {
            try
            {
                var reply = ping.Send(host, timeoutMs, buffer);
                if (reply?.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
            }
            catch { }
        }
        return Summarize(times, count);
    }

    public static PingStats Summarize(IReadOnlyList<double> samples, int sent)
    {
        sent = Math.Max(sent, samples.Count);
        if (samples.Count == 0)
            return new PingStats { Sent = sent, Received = 0, Loss = 100 };

        var jitter = 0.0;
        if (samples.Count > 1)
        {
            for (var i = 1; i < samples.Count; i++) jitter += Math.Abs(samples[i] - samples[i - 1]);
            jitter /= samples.Count - 1;
        }
        return new PingStats
        {
            Sent = sent,
            Received = samples.Count,
            Loss = sent == 0 ? 0 : Math.Round((sent - samples.Count) * 100.0 / sent, 1),
            Min = Math.Round(samples.Min(), 1),
            Avg = Math.Round(samples.Average(), 1),
            Max = Math.Round(samples.Max(), 1),
            Jitter = Math.Round(jitter, 1),
        };
    }

    /// <summary>Blocos de 25 MB ate ter 2 s de janela, descartando a rampa TCP/TLS.</summary>
    public static (long Bytes, double Seconds) MeasureDownload(
        long chunkBytes = 25_000_000, double maxSeconds = 10, double settleSeconds = 0.75,
        double windowSeconds = 2, int maxChunks = 4, int timeoutSeconds = 15)
    {
        using var http = Client(timeoutSeconds);
        var clock = Stopwatch.StartNew();
        long total = 0, baseBytes = 0;
        double? baseTime = null;

        for (var chunk = 0; chunk < Math.Max(1, maxChunks); chunk++)
        {
            using var response = http.GetAsync(DownUrl + chunkBytes, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var stream = response.Content.ReadAsStream();
            var buffer = new byte[262_144];
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                total += read;
                var elapsed = clock.Elapsed.TotalSeconds;
                if (baseTime == null && elapsed >= settleSeconds)
                {
                    baseTime = elapsed;
                    baseBytes = total;
                }
                if (baseTime != null && elapsed - baseTime >= windowSeconds)
                    return (total - baseBytes, Math.Max(elapsed - baseTime.Value, 0.001));
                if (elapsed >= maxSeconds) break;
            }
            if (clock.Elapsed.TotalSeconds >= maxSeconds) break;
        }

        var end = clock.Elapsed.TotalSeconds;
        if (baseTime == null || total <= baseBytes) return (total, Math.Max(end, 0.001));
        return (total - baseBytes, Math.Max(end - baseTime.Value, 0.001));
    }

    /// <summary>Blocos de 4 MB com ligacao reaquecida; handshake nao entra na conta.</summary>
    public static (long Bytes, double Seconds) MeasureUpload(
        int chunkBytes = 4_000_000, double windowSeconds = 2, int maxChunks = 6, int timeoutSeconds = 40)
    {
        chunkBytes = Math.Max(chunkBytes, 1_000_000);
        var payload = new byte[chunkBytes];
        using var http = Client(timeoutSeconds);

        // aquecimento: abre TCP/TLS numa transferencia minima
        using (var warm = new ByteArrayContent(new byte[1024]))
        {
            warm.Headers.ContentType = new("application/octet-stream");
            http.PostAsync(UpUrl, warm).GetAwaiter().GetResult().Dispose();
        }

        var clock = Stopwatch.StartNew();
        long total = 0;
        for (var i = 0; i < Math.Max(1, maxChunks); i++)
        {
            using var body = new ByteArrayContent(payload);
            body.Headers.ContentType = new("application/octet-stream");
            using var response = http.PostAsync(UpUrl, body).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            total += chunkBytes;
            if (clock.Elapsed.TotalSeconds >= windowSeconds) break;
        }
        return (total, Math.Max(clock.Elapsed.TotalSeconds, 0.001));
    }

    public static double BitsPerSecond(long bytes, double seconds)
        => bytes <= 0 || seconds <= 0 ? 0 : bytes * 8.0 / seconds;

    public static string FormatMbps(double bps)
    {
        if (bps <= 0) return "--";
        var mbps = bps / 1e6;
        return mbps >= 100 ? $"{mbps:0} Mbps" : $"{mbps:0.0} Mbps";
    }

    public static string Diagnose(PingStats? ping, PingStats? gateway, double? downBps, double? upBps)
    {
        var notes = new List<string>();
        if (gateway is { HasSamples: true } && gateway.Loss >= 1)
            notes.Add("ja ha perda de pacotes no router: cabo, Wi-Fi ou placa de rede");
        else if (gateway?.Avg is > 8)
            notes.Add("latencia alta ate ao router: o problema comeca na rede local");

        if (ping is { HasSamples: true } && ping.Loss >= 5)
            notes.Add("perda de pacotes para a internet: ligacao instavel");
        else if (ping?.Avg is >= 80)
            notes.Add("latencia alta para a internet com rede local limpa: problema fora de casa (ISP/rota)");

        if (ping?.Jitter is >= 15) notes.Add("jitter elevado: mau para jogos e chamadas");
        if (downBps is { } down && down / 1e6 < 20)
            notes.Add($"download de {down / 1e6:0} Mbps e pouco para 4K e atualizacoes grandes");
        if (upBps is { } up && up / 1e6 < 3)
            notes.Add($"upload de {up / 1e6:0.0} Mbps limita chamadas e backups na nuvem");

        if (notes.Count > 0) return "Atencao - " + string.Join("; ", notes) + ".";
        if (ping?.Avg != null) return "Ligacao saudavel: latencia baixa, sem perda relevante e velocidade estavel.";
        return "Sem medicoes suficientes para diagnosticar.";
    }

    /// <summary>Adaptador com rota predefinida, gateway e DNS atuais.</summary>
    public static NetRoute Route()
    {
        string? adapter = null, gateway = null, link = null;
        var dns = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var props = nic.GetIPProperties();
            var gw = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            var dnsServers = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString()).Distinct().ToList();
            if (gw == null && dnsServers.Count == 0) continue;

            adapter = nic.Name;
            gateway = gw?.Address.ToString();
            dns = dnsServers;
            link = nic.Speed > 0 ? $"{nic.Speed / 1_000_000} Mbps" : null;
            break;
        }
        return new NetRoute(adapter ?? "", gateway, dns, link);
    }
}
