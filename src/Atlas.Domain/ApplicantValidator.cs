namespace Atlas.Domain;

public sealed record ApplicantDetails(
    string FirstName,
    string LastName,
    DateOnly DateOfBirth,
    string Email,
    string Phone,
    string IdentifierType,
    string IdentifierValue);

public sealed record ValidationError(string Field, string Message);

public static class ApplicantValidator
{
    public static IReadOnlyList<ValidationError> Validate(ApplicantDetails a, MarketDefinition market, DateOnly today)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(a.FirstName)) errors.Add(new("firstName", "First name is required."));
        if (string.IsNullOrWhiteSpace(a.LastName)) errors.Add(new("lastName", "Last name is required."));
        if (string.IsNullOrWhiteSpace(a.Email) || !a.Email.Contains('@')) errors.Add(new("email", "A valid email is required."));
        if (string.IsNullOrWhiteSpace(a.Phone)) errors.Add(new("phone", "Phone is required."));

        if (a.DateOfBirth > today)
            errors.Add(new("dateOfBirth", "Date of birth cannot be in the future."));
        else if (AgeOn(a.DateOfBirth, today) < market.MinimumAge)
            errors.Add(new("dateOfBirth", $"Applicant must be at least {market.MinimumAge}."));

        var rule = market.FindIdentifier(a.IdentifierType ?? "");
        if (rule is null)
        {
            var accepted = string.Join(", ", market.AcceptedIdentifiers.Select(r => r.Type));
            errors.Add(new("identifier.type", $"Market {market.Code} accepts: {accepted}."));
        }
        else if (string.IsNullOrWhiteSpace(a.IdentifierValue)
                 || !IdentifierFormats.IsValid(rule.Format, IdentifierFormats.Normalise(a.IdentifierValue)))
        {
            errors.Add(new("identifier.value", $"Not a valid {rule.Type} for market {market.Code}."));
        }

        return errors;
    }

    private static int AgeOn(DateOnly dob, DateOnly today)
    {
        var age = today.Year - dob.Year;
        if (dob > today.AddYears(-age)) age--;
        return age;
    }
}
