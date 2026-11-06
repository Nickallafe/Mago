using Microsoft.Win32;

namespace MagoDaPerformance;

public static class Reg
{
    public static (RegistryHive Hive, string SubKey) Parse(string path)
    {
        var text = (path ?? "").Trim();
        if (text.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.LocalMachine, text[19..].TrimStart('\\'));
        if (text.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.CurrentUser, text[18..].TrimStart('\\'));
        if (text.StartsWith("HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.ClassesRoot, text[18..].TrimStart('\\'));
        if (text.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.LocalMachine, text[4..].TrimStart('\\'));
        if (text.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.CurrentUser, text[4..].TrimStart('\\'));
        if (text.StartsWith("HKCR", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.ClassesRoot, text[4..].TrimStart('\\'));
        throw new ArgumentException("caminho de registo invalido: " + path);
    }

    public static RegistryKey? Open(string path, bool writable)
    {
        var (hive, sub) = Parse(path);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        return string.IsNullOrEmpty(sub) ? baseKey : baseKey.OpenSubKey(sub, writable);
    }

    /// <summary>Abre para escrita e cria a chave se ainda nao existir.</summary>
    public static RegistryKey? OpenForWrite(string path)
    {
        var (hive, sub) = Parse(path);
        var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        if (string.IsNullOrEmpty(sub)) return baseKey;
        try
        {
            return baseKey.CreateSubKey(sub, true);
        }
        finally
        {
            baseKey.Dispose();
        }
    }

    public static string KindToName(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String => "REG_SZ",
        RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
        RegistryValueKind.DWord => "REG_DWORD",
        RegistryValueKind.QWord => "REG_QWORD",
        RegistryValueKind.MultiString => "REG_MULTI_SZ",
        RegistryValueKind.Binary => "REG_BINARY",
        _ => "REG_SZ",
    };

    public static RegistryValueKind NameToKind(string? name) => (name ?? "").ToUpperInvariant() switch
    {
        "REG_EXPAND_SZ" => RegistryValueKind.ExpandString,
        "REG_DWORD" => RegistryValueKind.DWord,
        "REG_QWORD" => RegistryValueKind.QWord,
        "REG_MULTI_SZ" => RegistryValueKind.MultiString,
        "REG_BINARY" => RegistryValueKind.Binary,
        _ => RegistryValueKind.String,
    };

    public static string? DataToString(object? value, RegistryValueKind kind) => value switch
    {
        null => null,
        int i => i.ToString(),
        long l => l.ToString(),
        string[] arr => string.Join("\n", arr),
        byte[] bytes => Convert.ToHexString(bytes),
        _ => value.ToString(),
    };

    /// <summary>Le o valor atual para o backup (existed=false quando nao existe).</summary>
    public static RegSnap Snapshot(string key, string name)
    {
        try
        {
            using var k = Open(key, false);
            var value = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value == null) return new RegSnap { Existed = false, Key = key, Name = name };
            var kind = k!.GetValueKind(name);
            return new RegSnap
            {
                Existed = true,
                Key = key,
                Name = name,
                Type = KindToName(kind),
                Data = DataToString(value, kind),
            };
        }
        catch (Exception ex)
        {
            Log.Write("Snapshot de registo falhou: " + name + " (" + ex.Message + ")", "AVISO");
            return new RegSnap { Existed = false, Key = key, Name = name };
        }
    }

    public static bool Write(string key, string name, string? type, string? data)
    {
        try
        {
            // CreateSubKey: varias chaves alvo (NVTweak, GameDVR...) podem nao existir.
            using var k = OpenForWrite(key);
            if (k == null) return false;
            var kind = NameToKind(type);
            object? value = kind switch
            {
                RegistryValueKind.DWord => (int)(Convert.ToUInt32(string.IsNullOrEmpty(data) ? "0" : data, 10)),
                RegistryValueKind.QWord => (long)Convert.ToUInt64(string.IsNullOrEmpty(data) ? "0" : data, 10),
                RegistryValueKind.MultiString => (data ?? "").Split('\n'),
                RegistryValueKind.Binary => Convert.FromHexString((data ?? "").Replace(" ", "")),
                _ => data ?? "",
            };
            k.SetValue(name, value!, kind);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Registo falhou: {name} ({ex.Message})", "ERRO");
            return false;
        }
    }

    public static bool DeleteValue(string key, string name)
    {
        try
        {
            using var k = Open(key, true);
            if (k == null) return false;
            k.DeleteValue(name, false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Remocao de valor falhou: {name} ({ex.Message})", "ERRO");
            return false;
        }
    }

    public static bool DeleteKeyTree(string key)
    {
        try
        {
            var (hive, sub) = Parse(key);
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            baseKey.DeleteSubKeyTree(sub, false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Remocao de chave falhou: {key} ({ex.Message})", "ERRO");
            return false;
        }
    }

    public static string? ReadString(string key, string name)
    {
        try
        {
            using var k = Open(key, false);
            return k?.GetValue(name)?.ToString();
        }
        catch { return null; }
    }
}
