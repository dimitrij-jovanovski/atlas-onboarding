using System.Collections.Concurrent;
using Atlas.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Atlas.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    /// <summary>"SqlServer" (default, matches /platform) or "Sqlite" (no Docker needed).</summary>
    public string Provider { get; set; } = "SqlServer";

    /// <summary>Connection string with a {market} placeholder, used when a market has no explicit entry.</summary>
    public string ConnectionStringTemplate { get; set; } = default!;

    /// <summary>
    /// Explicit per-market connection strings. In production each of these points at a database in
    /// that market's own country (GC §1); locally they are all on one server, but still separate databases.
    /// </summary>
    public Dictionary<string, string> Markets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ConnectionStringFor(string market) =>
        Markets.TryGetValue(market, out var explicitConnection)
            ? explicitConnection
            : ConnectionStringTemplate.Replace("{market}", market.ToUpperInvariant());
}

public interface IMarketDbContextFactory
{
    /// <summary>Opens the data store for exactly one market. There is no API that spans markets.</summary>
    AtlasDbContext Create(string market);
}

public sealed class MarketDbContextFactory(IOptions<DatabaseOptions> options, MarketCatalog markets) : IMarketDbContextFactory
{
    private readonly ConcurrentDictionary<string, DbContextOptions<AtlasDbContext>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AtlasDbContext Create(string market)
    {
        // Throws for unknown markets: data for a market Atlas does not know about has nowhere to go.
        var definition = markets.Get(market);
        var contextOptions = _cache.GetOrAdd(definition.Code, Build);
        return new AtlasDbContext(contextOptions);
    }

    private DbContextOptions<AtlasDbContext> Build(string market)
    {
        var db = options.Value;
        var connectionString = db.ConnectionStringFor(market);
        var builder = new DbContextOptionsBuilder<AtlasDbContext>();

        switch (db.Provider.ToLowerInvariant())
        {
            case "sqlserver":
                builder.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3));
                break;
            case "sqlite":
                EnsureSqliteDirectory(connectionString);
                builder.UseSqlite(connectionString);
                break;
            default:
                throw new InvalidOperationException($"Unsupported database provider '{db.Provider}'.");
        }

        return builder.Options;
    }

    private static void EnsureSqliteDirectory(string connectionString)
    {
        var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }
}
