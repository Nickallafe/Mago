namespace MagoDaPerformance;

/// <summary>Uma alteracao relida: o que se esperava e o que esta' realmente no sistema.</summary>
public sealed record VerifiedItem(string Label, string Expected, string Actual, bool Ok);

/// <summary>
/// Resultado de reler um modulo depois de aplicado. Uma escrita sem erro so' prova que a API
/// aceitou o pedido: o Windows pode reverter (VBS/HVCI volta sozinho) ou ignorar (politica de
/// grupo). O relatorio existe para o utilizador ver a diferenca entre "pedi" e "esta' feito".
/// </summary>
public sealed record VerificationReport(string Module, IReadOnlyList<VerifiedItem> Items)
{
    public int Confirmed => Items.Count(item => item.Ok);
    public int Failed => Items.Count - Confirmed;

    public string Describe()
    {
        if (Items.Count == 0) return $"{Module}: nada para confirmar.";

        var head = Failed == 0
            ? $"\u2713 {Module}: {Confirmed} alteracao(oes) confirmadas."
            : $"\u26A0 {Module}: {Confirmed} confirmadas, {Failed} nao pegaram.";

        if (Failed == 0) return head;

        return head + "\n" + string.Join("\n",
            Items.Where(item => !item.Ok)
                .Select(item => $"  - {item.Label}: esperado {item.Expected}, esta' {item.Actual}"));
    }
}
