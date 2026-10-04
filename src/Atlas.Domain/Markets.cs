namespace Atlas.Domain;

/// <summary>
/// How an approved application becomes an active account in a market.
/// Annex B: MD requires the applicant to attend a branch and give a wet signature before
/// activation. That is modelled as configuration, not as a fork of the flow.
/// </summary>
public enum ActivationMode
{
    Remote,
    Branch,
}

/// <summary>
/// One identifier a market accepts. <see cref="Format"/> is a key into <see cref="IdentifierFormats"/>.
/// </summary>
public sealed record IdentifierRule(string Type, string Format);

/// <summary>
/// Everything that differs between markets. Loaded from markets.json; nothing in the code
/// branches on a market code.
/// </summary>
public sealed record MarketDefinition(
    string Code,
    bool Enabled,
    ActivationMode Activation,
    IReadOnlyList<IdentifierRule> AcceptedIdentifiers,
    int MinimumAge = 18)
{
    /// <summary>The identifier type assumed when a client sends the legacy flat <c>nationalId</c> field.</summary>
    public string DefaultIdentifierType => AcceptedIdentifiers[0].Type;

    public IdentifierRule? FindIdentifier(string type) =>
        AcceptedIdentifiers.FirstOrDefault(r => string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase));
}
