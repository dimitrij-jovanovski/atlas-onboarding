using System.Text.RegularExpressions;

namespace Atlas.Domain;

/// <summary>
/// Format validation for customer identifiers, keyed by the format names used in markets.json.
///
/// Annex B gives formats "for validation purposes only". It does not give the check-digit
/// algorithms (MA/MB have one, ME's "differs"), so only the shape is validated here. Guessing an
/// algorithm would reject real customers if the guess is wrong — see OPEN-QUESTIONS.md.
/// </summary>
public static partial class IdentifierFormats
{
    public const string Digits13 = "digits-13";
    public const string Digits10 = "digits-10";
    public const string UnifiedCitizenId = "alnum-9-letter-last";
    public const string Passport = "passport";

    public static bool IsKnown(string format) => format is Digits13 or Digits10 or UnifiedCitizenId or Passport;

    /// <summary>Uppercases and strips the spaces and dashes people type into ID fields.</summary>
    public static string Normalise(string value) =>
        new string(value.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();

    public static bool IsValid(string format, string normalisedValue) => format switch
    {
        Digits13 => Digits13Regex().IsMatch(normalisedValue),
        Digits10 => Digits10Regex().IsMatch(normalisedValue),
        UnifiedCitizenId => UnifiedCitizenIdRegex().IsMatch(normalisedValue),
        Passport => PassportRegex().IsMatch(normalisedValue),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown identifier format."),
    };

    [GeneratedRegex("^[0-9]{13}$")]
    private static partial Regex Digits13Regex();

    [GeneratedRegex("^[0-9]{10}$")]
    private static partial Regex Digits10Regex();

    // MC: 9 characters, alphanumeric, letter in position 9.
    [GeneratedRegex("^[A-Z0-9]{8}[A-Z]$")]
    private static partial Regex UnifiedCitizenIdRegex();

    // Assumption: ICAO 9303 document numbers are up to 9 alphanumeric characters. Kept loose on purpose.
    [GeneratedRegex("^[A-Z0-9]{6,9}$")]
    private static partial Regex PassportRegex();
}
