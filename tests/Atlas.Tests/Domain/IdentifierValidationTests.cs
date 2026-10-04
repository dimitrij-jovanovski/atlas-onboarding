using Atlas.Domain;

namespace Atlas.Tests.Domain;

/// <summary>
/// Annex B formats. The point of these tests is the cases where "nationalId: required string" from the
/// ticket would have been wrong: different shapes per market, and MF residents with no national ID at all.
/// </summary>
public class IdentifierValidationTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(TestData.Now);

    private static IReadOnlyList<ValidationError> Validate(string market, string type, string value) =>
        ApplicantValidator.Validate(TestData.Applicant(market, identifierType: type, identifierValue: value),
            Markets.Catalog.Get(market), Today);

    [Theory]
    [InlineData("MA", "PERSONAL_NUMBER", "0403991450016")]
    [InlineData("MB", "PERSONAL_NUMBER", "0403991450016")]
    [InlineData("MB", "PERSONAL_NUMBER", "0403 991 450 016")] // spaces typed by the user are normalised
    [InlineData("MC", "UNIFIED_CITIZEN_ID", "12345678A")]
    [InlineData("MC", "UNIFIED_CITIZEN_ID", "AB1234C5z")]     // lower case is normalised
    [InlineData("MD", "CITIZEN_NUMBER", "0403991450")]
    [InlineData("ME", "CIVIL_NUMBER", "0403991450")]
    [InlineData("MF", "PERSONAL_NUMBER", "0403991450016")]
    [InlineData("MF", "PASSPORT_NUMBER", "P1234567")]         // non-citizen residents
    public void Accepts_valid_identifiers(string market, string type, string value) =>
        Assert.Empty(Validate(market, type, value));

    [Theory]
    [InlineData("MA", "PERSONAL_NUMBER", "040399145001")]     // 12 digits
    [InlineData("MB", "PERSONAL_NUMBER", "04039914500AB")]
    [InlineData("MC", "UNIFIED_CITIZEN_ID", "123456789")]     // position 9 must be a letter
    [InlineData("MC", "UNIFIED_CITIZEN_ID", "12345678")]
    [InlineData("MD", "CITIZEN_NUMBER", "0403991450016")]     // MB-style number is not an MD number
    [InlineData("ME", "CIVIL_NUMBER", "040399145")]
    [InlineData("MF", "PASSPORT_NUMBER", "P12")]
    public void Rejects_malformed_identifiers(string market, string type, string value) =>
        Assert.Contains(Validate(market, type, value), e => e.Field == "identifier.value");

    [Theory]
    [InlineData("MA")]
    [InlineData("MB")]
    [InlineData("MC")]
    [InlineData("MD")]
    [InlineData("ME")]
    public void Passport_number_is_only_accepted_where_Annex_B_allows_it(string market) =>
        Assert.Contains(Validate(market, "PASSPORT_NUMBER", "P1234567"), e => e.Field == "identifier.type");

    [Fact]
    public void Applicant_must_be_an_adult()
    {
        var minor = TestData.Applicant() with { DateOfBirth = Today.AddYears(-17) };
        Assert.Contains(ApplicantValidator.Validate(minor, Markets.Catalog.Get("MB"), Today), e => e.Field == "dateOfBirth");
    }

    [Fact]
    public void Shipped_market_configuration_matches_Annex_B()
    {
        Assert.Equal(new[] { "MA", "MB", "MC", "MD", "ME", "MF" }, Markets.Catalog.Enabled.Select(m => m.Code).Order());
        Assert.Equal(ActivationMode.Branch, Markets.Catalog.Get("MD").Activation);
        Assert.All(Markets.Catalog.Enabled.Where(m => m.Code != "MD"), m => Assert.Equal(ActivationMode.Remote, m.Activation));
    }

    [Fact]
    public void A_market_that_is_not_enabled_cannot_take_applications()
    {
        var catalog = new MarketCatalog([
            new MarketDefinition("MG", Enabled: false, ActivationMode.Remote, [new IdentifierRule("PERSONAL_NUMBER", IdentifierFormats.Digits13)]),
        ]);
        Assert.Null(catalog.FindEnabled("MG"));
    }
}
