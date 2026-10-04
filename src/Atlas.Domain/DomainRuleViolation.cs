namespace Atlas.Domain;

/// <summary>Thrown when a requested transition breaks a business or compliance rule.</summary>
public sealed class DomainRuleViolation(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
