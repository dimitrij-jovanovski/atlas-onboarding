using Atlas.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atlas.Infrastructure.Persistence;

/// <summary>
/// Creates each enabled market's database and schema on startup.
///
/// Only the Onboarding API runs this — it owns the schema; the worker and back office wait for it.
/// EnsureCreated rather than migrations is a time-box decision (see Known limitations in README.md).
/// </summary>
public sealed class DatabaseInitializer(
    IMarketDbContextFactory factory,
    MarketCatalog markets,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var market in markets.Enabled)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var db = factory.Create(market.Code);
                    var created = await db.Database.EnsureCreatedAsync(cancellationToken);
                    logger.LogInformation("Market {Market} store ready (created: {Created})", market.Code, created);
                    break;
                }
                catch (Exception ex) when (attempt < 10 && !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Market {Market} store not reachable yet (attempt {Attempt}); retrying", market.Code, attempt);
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                }
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
