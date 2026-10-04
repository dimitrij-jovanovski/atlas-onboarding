namespace Atlas.Domain;

/// <summary>
/// Public application identifier: "{market}-{guid}", e.g. "MB-3f2a...".
///
/// The market prefix lets any service route a request to the right market's data store without a
/// cross-market lookup table — such a table would itself be a place where data from every country
/// sits together. The customer's national identifier is never used as a key (Annex B, note 2).
/// </summary>
public readonly record struct ApplicationRef(string Market, Guid Id)
{
    public override string ToString() => $"{Market}-{Id:N}";

    public static bool TryParse(string? value, out ApplicationRef reference)
    {
        reference = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var dash = value.IndexOf('-');
        if (dash != 2) return false;

        var market = value[..2].ToUpperInvariant();
        if (!char.IsLetter(market[0]) || !char.IsLetter(market[1])) return false;
        if (!Guid.TryParse(value[(dash + 1)..], out var id)) return false;

        reference = new ApplicationRef(market, id);
        return true;
    }
}
