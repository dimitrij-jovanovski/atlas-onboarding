using Atlas.Domain;
using Atlas.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Events;

namespace Atlas.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Shared wiring for every Atlas service: market catalogue, per-market persistence, clock, logging.
    /// Everything is resolved lazily from DI so test hosts can override configuration.
    /// </summary>
    public static IServiceCollection AddAtlasCore(this IServiceCollection services, string serviceName)
    {
        services.AddSingleton(sp => LoadMarkets(sp.GetRequiredService<IConfiguration>()));
        services.AddOptions<DatabaseOptions>().BindConfiguration("Database");
        services.AddSingleton<IMarketDbContextFactory, MarketDbContextFactory>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddSerilog((sp, log) =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            log.MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Service", serviceName)
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Service}: {Message:lj}{NewLine}{Exception}");

            // Logs go to a central sink, so they must never contain customer personal data (GC §1):
            // log application references and outcomes, never names, identifiers or documents.
            var seqUrl = config["Seq:ServerUrl"];
            if (!string.IsNullOrWhiteSpace(seqUrl)) log.WriteTo.Seq(seqUrl);
        });

        return services;
    }

    private static MarketCatalog LoadMarkets(IConfiguration config)
    {
        var path = config["Markets:File"];
        if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(AppContext.BaseDirectory, "markets.json");
        return MarketCatalog.FromJson(File.ReadAllText(path));
    }
}
