using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MagoDaPerformance;

/// <summary>Execucao de processos externos (reg, sc, bcdedit, powercfg, netsh, powershell).</summary>
public static class Proc
{
    public static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static void EnsureAdminOrExit()
    {
        if (IsAdmin()) return;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("executavel desconhecido");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("pedido de elevacao falhou");
        Environment.Exit(0);
    }

    public static (bool Ok, string Output) Run(string exe, string args, int timeoutMs = 120000)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = new Process { StartInfo = psi };
            var sb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (false, "tempo limite excedido");
            }
            p.WaitForExit();
            return (p.ExitCode == 0, sb.ToString().Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Executa uma linha de comando pela shell (UninstallString, por exemplo).</summary>
    public static (bool Ok, string Output) RunShell(string commandLine, int timeoutMs = 300000)
        => Run("cmd.exe", "/c " + commandLine, timeoutMs);

    /// <summary>
    /// Executa um comando PowerShell e devolve as linhas como JSON.
    /// O script e escrito num .ps1 temporario para evitar problemas de aspas.
    /// </summary>
    public static List<JsonElement> Ps(string command, int timeoutMs = 90000)
    {
        var scriptFile = Path.Combine(Path.GetTempPath(), "mago_ps_" + Guid.NewGuid().ToString("N") + ".ps1");
        var outFile = Path.ChangeExtension(scriptFile, ".json");
        try
        {
            // O comando corre dentro de um script block (& { }), que aceita varias
            // instrucoes e blocos try/catch; o resultado e depois convertido em JSON.
            var script = "$ErrorActionPreference='SilentlyContinue'\n$__mago = & {\n" + command +
                         "\n}\n$__mago | ConvertTo-Json -Depth 6 -Compress | Out-File -FilePath '" + outFile + "' -Encoding utf8\n";
            File.WriteAllText(scriptFile, script, new UTF8Encoding(true));
            Run("powershell", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptFile + "\"", timeoutMs);
            if (!File.Exists(outFile)) return new();
            var raw = File.ReadAllText(outFile, Encoding.UTF8).Trim().TrimStart('\uFEFF');
            if (string.IsNullOrEmpty(raw)) return new();
            using var doc = JsonDocument.Parse(raw);
            var list = new List<JsonElement>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                list.AddRange(doc.RootElement.EnumerateArray().Select(e => e.Clone()));
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                list.Add(doc.RootElement.Clone());
            return list;
        }
        catch (Exception ex)
        {
            Log.Write("PowerShell falhou: " + ex.Message, "AVISO");
            return new();
        }
        finally
        {
            try { File.Delete(scriptFile); } catch { }
            try { File.Delete(outFile); } catch { }
        }
    }

    public static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => p.Value.ToString(),
                };
        return null;
    }

    public static bool OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }
}
