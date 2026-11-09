using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace MagoDaPerformance;

public sealed record UpdateInfo(string Version, string PageUrl, string? InstallerUrl, string Notes);

/// <summary>
/// Auto-atualizacao pelo GitHub Releases: consulta a ultima versao, descarrega o
/// instalador e corre-o em silencio (/VERYSILENT), reabrindo o app no fim.
/// </summary>
public static class Updater
{
    public const string Repository = "Nickallafe/Mago";
    private const string ApiUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const long MinimumInstallerBytes = 1_000_000;

    // Criado sob demanda: CreateClient usa DisplayVersion, que depende de CurrentVersion.
    private static HttpClient? _http;
    private static HttpClient Http => _http ??= CreateClient();

    /// <summary>Versao desta copia (vem do &lt;Version&gt; do csproj).</summary>
    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    public static string DisplayVersion =>
        $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(CurrentVersion.Build, 0)}";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MagoDaPerformance/" + DisplayVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version ReadCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return Normalize(version);
    }

    /// <summary>Compara sempre com 4 componentes: 0.2.4 e 0.2.4.0 sao a mesma versao.</summary>
    internal static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    internal static bool IsNewer(Version current, Version latest) => Normalize(latest) > Normalize(current);

    /// <summary>Escolhe o instalador da release (ignora .sha256, .msi e o codigo-fonte).</summary>
    internal static string? PickInstaller(IEnumerable<(string Name, string Url)> assets) =>
        assets.FirstOrDefault(a =>
                a.Name.StartsWith("MagoSetup-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Url;

    /// <summary>Interpreta a resposta de /releases/latest. Devolve null sem tag valida.</summary>
    internal static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        var page = root.TryGetProperty("html_url", out var pageElement) ? pageElement.GetString() ?? "" : "";
        var notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? "" : "";
        if (notes.Length > 4000) notes = notes[..4000];

        var assets = new List<(string Name, string Url)>();
        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (name.Length > 0 && url.Length > 0) assets.Add((name, url));
            }
        }

        return new UpdateInfo(version.ToString(), page, PickInstaller(assets), notes);
    }

    /// <summary>Consulta a ultima release. Devolve null quando ja estamos atualizados.</summary>
    public static UpdateInfo? CheckForUpdate()
    {
        var json = Http.GetStringAsync(ApiUrl).GetAwaiter().GetResult();
        var info = ParseRelease(json);
        if (info == null) throw new InvalidOperationException("release sem versao valida");
        if (!IsNewer(CurrentVersion, Version.Parse(info.Version))) return null;
        return info;
    }

    /// <summary>Descarrega o instalador para %LOCALAPPDATA%\MagoDaPerformance\updates.</summary>
    public static string Download(UpdateInfo info, Action<string>? progress = null)
    {
        if (string.IsNullOrEmpty(info.InstallerUrl))
            throw new InvalidOperationException("a release nao tem instalador anexado");

        Directory.CreateDirectory(Paths.UpdateDir);
        foreach (var old in Directory.EnumerateFiles(Paths.UpdateDir, "MagoSetup-*.exe"))
        {
            try { File.Delete(old); } catch { }
        }

        var name = Path.GetFileName(new Uri(info.InstallerUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(name)) name = $"MagoSetup-{info.Version}.exe";
        var destination = Path.Combine(Paths.UpdateDir, name);
        var partial = destination + ".part";

        progress?.Invoke($"A descarregar a versao {info.Version}...");
        try
        {
            using var response = Http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            using (var source = response.Content.ReadAsStream())
            using (var target = File.Create(partial))
            {
                var buffer = new byte[81920];
                long received = 0;
                var lastPercent = -1;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    target.Write(buffer, 0, read);
                    received += read;
                    if (total <= 0) continue;
                    var percent = (int)(received * 100 / total);
                    if (percent == lastPercent) continue;
                    lastPercent = percent;
                    progress?.Invoke($"A descarregar a versao {info.Version}... {percent}%");
                }
            }
            if (new FileInfo(partial).Length < MinimumInstallerBytes)
                throw new InvalidOperationException("o instalador descarregado esta incompleto");
            File.Move(partial, destination, true);
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }

        Log.Write($"Atualizacao {info.Version} descarregada em {destination}.");
        return destination;
    }

    /// <summary>Corre o instalador em silencio e reabre o app quando ele terminar.</summary>
    public static bool InstallAndRestart(string installerPath)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(installerPath)) return false;

        // cmd espera o instalador terminar e so depois arranca a versao nova.
        var arguments = $"/c \"\"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS & start \"\" \"{exe}\"\"";
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var process = Process.Start(psi);
            if (process == null) return false;
            Log.Write("Instalador de atualizacao iniciado.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Falha ao iniciar o instalador: " + ex.Message, "ERRO");
            return false;
        }
    }

    /// <summary>
    /// So substitui uma copia instalada pelo setup: um exe portatil em publish\ nao deve
    /// instalar uma segunda copia em Program Files.
    /// </summary>
    public static bool IsInstalledCopy()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        var dir = Path.GetDirectoryName(exe) ?? "";
        return dir.EndsWith("Mago da Performance", StringComparison.OrdinalIgnoreCase);
    }
}
