using System.Globalization;

namespace MagoDaPerformance;

/// <summary>
/// Combinacao de teclas do atalho global, em texto ("Ctrl+Alt+F"). Fica fora da UI para poder
/// ser testada sem teclado: o que aqui se aceita e' exatamente o que o RegisterHotKey recebe.
/// </summary>
public readonly record struct Hotkey(uint Modifiers, uint Key)
{
    /// <summary>Atalho de fabrica; e' o valor guardado nas preferencias de quem nunca o mudou.</summary>
    public const string DefaultText = "Ctrl+Alt+F";

    // Mesmos valores das constantes MOD_* do RegisterHotKey.
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Win = 0x0008;

    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Enter"] = 0x0D,
        ["Tab"] = 0x09,
        ["Escape"] = 0x1B,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
    };

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint modifiers = 0;
        uint key = 0;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= Control;
                    continue;
                case "ALT":
                    modifiers |= Alt;
                    continue;
                case "SHIFT":
                    modifiers |= Shift;
                    continue;
                case "WIN" or "WINDOWS":
                    modifiers |= Win;
                    continue;
            }

            if (key != 0) return false;              // duas teclas principais nao formam atalho
            if (!TryParseKey(part, out key)) return false;
        }

        // Sem modificador, o atalho engolia a tecla em todos os programas: nunca aceitar.
        if (modifiers == 0 || key == 0) return false;

        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    /// <summary>Texto normalizado, com os modificadores sempre pela mesma ordem.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if ((Modifiers & Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & Alt) != 0) parts.Add("Alt");
        if ((Modifiers & Shift) != 0) parts.Add("Shift");
        if ((Modifiers & Win) != 0) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static bool TryParseKey(string part, out uint key)
    {
        key = 0;

        if (part.Length == 1)
        {
            var ch = char.ToUpperInvariant(part[0]);
            if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))
            {
                key = ch;
                return true;
            }
            return false;
        }

        if ((part[0] is 'F' or 'f') &&
            int.TryParse(part.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            number is >= 1 and <= 24)
        {
            key = (uint)(0x70 + number - 1);
            return true;
        }

        return NamedKeys.TryGetValue(part, out key);
    }

    private static string KeyName(uint key)
    {
        if ((key >= 'A' && key <= 'Z') || (key >= '0' && key <= '9')) return ((char)key).ToString();
        if (key >= 0x70 && key <= 0x87) return "F" + (key - 0x70 + 1);
        return NamedKeys.FirstOrDefault(pair => pair.Value == key).Key ?? "?";
    }
}
