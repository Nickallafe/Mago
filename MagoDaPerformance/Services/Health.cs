namespace MagoDaPerformance;

public sealed record HealthItem(string Label, bool Ok, string Detail);

public sealed class HealthReport
{
    public int Score { get; init; }
    public string Summary { get; init; } = "";
    public List<HealthItem> Items { get; init; } = new();
}

/// <summary>Nota simples de 0 a 100 com base em RAM, disco, drivers e backup.</summary>
public static class Health
{
    public static HealthReport Evaluate(HardwareInfo? hardware, IReadOnlyList<DriverRow>? drivers, bool hasBackup)
    {
        var items = new List<HealthItem>();
        var score = 100;

        var (ramUsed, ramTotalGb) = SystemInfo.Memory();
        if (ramUsed >= 85)
        {
            score -= 15;
            items.Add(new HealthItem("Memoria", false, $"{ramUsed:0}% em uso de {ramTotalGb:0.#} GB"));
        }
        else
        {
            items.Add(new HealthItem("Memoria", true, $"{ramUsed:0}% em uso de {ramTotalGb:0.#} GB"));
        }

        var diskUsed = SystemInfo.DiskUsedPercent();
        if (diskUsed >= 90)
        {
            score -= 15;
            items.Add(new HealthItem("Disco C:", false, $"{diskUsed:0}% ocupado"));
        }
        else if (diskUsed >= 75)
        {
            score -= 6;
            items.Add(new HealthItem("Disco C:", false, $"{diskUsed:0}% ocupado"));
        }
        else
        {
            items.Add(new HealthItem("Disco C:", true, $"{diskUsed:0}% ocupado"));
        }

        if (drivers is { Count: > 0 })
        {
            var old = drivers.Count(d => d.Old);
            if (old > 0) score -= Math.Min(20, old * 5);
            items.Add(new HealthItem("Drivers", old == 0,
                old == 0 ? $"{drivers.Count} drivers, todos recentes" : $"{old} de {drivers.Count} com mais de 2 anos"));
        }
        else
        {
            items.Add(new HealthItem("Drivers", true, "ainda nao verificados"));
        }

        if (!hasBackup)
        {
            score -= 5;
            items.Add(new HealthItem("Backup", false, "sem backup - capture antes de otimizar"));
        }
        else
        {
            items.Add(new HealthItem("Backup", true, "backup disponivel para restauro"));
        }

        score = Math.Clamp(score, 0, 100);
        var summary = score switch
        {
            >= 90 => "Excelente - nada urgente.",
            >= 75 => "Bom - vale uma limpeza de vez em quando.",
            >= 55 => "Razoavel - ha pontos a melhorar.",
            _ => "Precisa de atencao.",
        };
        return new HealthReport { Score = score, Summary = summary, Items = items };
    }
}
