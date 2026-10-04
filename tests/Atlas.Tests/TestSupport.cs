using Atlas.Domain;
using Atlas.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Atlas.Tests;

public static class Markets
{
    /// <summary>The real markets.json, so the tests check the configuration that ships.</summary>
    public static MarketCatalog Catalog { get; } =
        MarketCatalog.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "markets.json")));
}

public sealed class ManualClock(DateTime startUtc) : TimeProvider
{
    private DateTimeOffset _now = new(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc));
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>A throwaway set of per-market SQLite databases in a temp folder.</summary>
public sealed class TestDatabases : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "atlas-tests-" + Guid.NewGuid().ToString("N"));

    public string ConnectionStringTemplate => $"Data Source={Path.Combine(Directory, "atlas_{market}.db")}";

    public DatabaseOptions Options => new() { Provider = "Sqlite", ConnectionStringTemplate = ConnectionStringTemplate };

    public IMarketDbContextFactory CreateFactory()
    {
        var factory = new MarketDbContextFactory(Microsoft.Extensions.Options.Options.Create(Options), Markets.Catalog);
        foreach (var market in Markets.Catalog.Enabled)
        {
            using var db = factory.Create(market.Code);
            db.Database.EnsureCreated();
        }
        return factory;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { /* best effort */ }
    }
}

public static class TestData
{
    public static readonly DateTime Now = new(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Smallest byte sequences that pass the JPEG / PNG sniffing.</summary>
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

    public static ApplicantDetails Applicant(string market = "MB", string lastName = "Petrovska",
        string? identifierType = null, string? identifierValue = null) => new(
        "Ana", lastName, new DateOnly(1991, 3, 4), "ana@example.com", "+38970000000",
        identifierType ?? Markets.Catalog.Get(market).DefaultIdentifierType,
        identifierValue ?? market switch
        {
            "MC" => "12345678A",
            "MD" or "ME" => "0403991450",
            _ => "0403991450016",
        });

    public static OnboardingApplication Submitted(string market = "MB", string lastName = "Petrovska")
    {
        var app = OnboardingApplication.Create(Markets.Catalog.Get(market), Applicant(market, lastName), Now);
        app.Submit(true, [DocumentType.Passport, DocumentType.Selfie], Now);
        return app;
    }
}
