using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagoDaPerformance;

public sealed class RegSnap
{
    [JsonPropertyName("existed")] public bool Existed { get; set; }
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
}

public sealed class SvcSnap
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("start")] public string Start { get; set; } = "unknown";
    [JsonPropertyName("running")] public bool Running { get; set; }
}

public sealed class BcdSnap
{
    [JsonPropertyName("existed")] public bool Existed { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
}

public sealed class StartupSnap
{
    [JsonPropertyName("existed")] public bool Existed { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
}

/// <summary>Alteracoes fora do registo/servicos que o restauro tem de desfazer.</summary>
public sealed class TouchedFlags
{
    [JsonPropertyName("defrag_task")] public bool DefragTask { get; set; }
    [JsonPropertyName("memory_compression")] public bool MemoryCompression { get; set; }
    [JsonPropertyName("tcp_globals")] public bool TcpGlobals { get; set; }
    [JsonPropertyName("trim")] public bool Trim { get; set; }

    public bool Any => DefragTask || MemoryCompression || TcpGlobals || Trim;
}

public sealed class BackupData
{
    [JsonPropertyName("created")] public string? Created { get; set; }
    [JsonPropertyName("registry")] public Dictionary<string, RegSnap> Registry { get; set; } = new();
    [JsonPropertyName("services")] public Dictionary<string, SvcSnap> Services { get; set; } = new();
    [JsonPropertyName("bcd")] public Dictionary<string, BcdSnap> Bcd { get; set; } = new();
    [JsonPropertyName("power_scheme")] public string? PowerScheme { get; set; }
    [JsonPropertyName("startup")] public Dictionary<string, StartupSnap> Startup { get; set; } = new();
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();
    [JsonPropertyName("touched")] public TouchedFlags Touched { get; set; } = new();
}

public sealed class BackupStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();
    public string Path { get; }
    public BackupData Data { get; private set; }

    public BackupStore(string path)
    {
        Path = path;
        Data = Load();
    }

    private BackupData Load()
    {
        try
        {
            if (!File.Exists(Path)) return new BackupData();
            var json = File.ReadAllText(Path);
            var data = JsonSerializer.Deserialize<BackupData>(json, Options);
            return data ?? new BackupData();
        }
        catch (Exception ex)
        {
            Log.Write("Backup ilegivel, a comecar de novo: " + ex.Message, "AVISO");
            return new BackupData();
        }
    }

    public void Reload() => Data = Load();

    public bool HasBackup =>
        Data.Registry.Count > 0 || Data.Services.Count > 0 || Data.Bcd.Count > 0 ||
        !string.IsNullOrEmpty(Data.PowerScheme) || Data.Startup.Count > 0;

    public void Save()
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(Data.Created)) Data.Created = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path) ?? ".");
            // Escreve num temporario e troca: um corte a meio nao corrompe o backup.
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Data, Options));
            File.Move(temp, Path, true);
        }
    }

    public void Reset()
    {
        lock (_lock) Data = new BackupData();
    }

    public void RememberReg(string key, string name, RegSnap snap, bool overwrite = false)
    {
        lock (_lock)
        {
            var token = key + "::" + name;
            if (overwrite || !Data.Registry.ContainsKey(token)) Data.Registry[token] = snap;
        }
    }

    public void RememberService(string name, SvcSnap snap, bool overwrite = false)
    {
        lock (_lock)
        {
            var token = name.ToLowerInvariant();
            if (overwrite || !Data.Services.ContainsKey(token)) Data.Services[token] = snap;
        }
    }

    public void RememberBcd(string name, BcdSnap snap, bool overwrite = false)
    {
        lock (_lock)
        {
            if (overwrite || !Data.Bcd.ContainsKey(name)) Data.Bcd[name] = snap;
        }
    }

    public void RememberPower(string? guid, bool overwrite = false)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(guid)) return;
            if (overwrite || string.IsNullOrEmpty(Data.PowerScheme)) Data.PowerScheme = guid;
        }
    }

    public void RememberStartup(string token, StartupSnap snap)
    {
        lock (_lock) Data.Startup[token] = snap;
    }
}
