using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.Domain;

/// <summary>The set of markets Atlas knows about, loaded from markets.json.</summary>
public sealed class MarketCatalog
{
    private readonly Dictionary<string, MarketDefinition> _markets;

    public MarketCatalog(IEnumerable<MarketDefinition> markets)
    {
        _markets = new Dictionary<string, MarketDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in markets)
        {
            if (m.AcceptedIdentifiers.Count == 0)
                throw new InvalidOperationException($"Market {m.Code} accepts no identifiers.");
            foreach (var rule in m.AcceptedIdentifiers)
                if (!IdentifierFormats.IsKnown(rule.Format))
                    throw new InvalidOperationException($"Market {m.Code}: unknown identifier format '{rule.Format}'.");
            _markets.Add(m.Code, m);
        }
    }

    public IEnumerable<MarketDefinition> All => _markets.Values;

    /// <summary>
    /// Markets that may take applications. Annex B note 3: a market added later must not go live until
    /// the annex is reissued, so a market is disabled until someone deliberately enables it.
    /// </summary>
    public IEnumerable<MarketDefinition> Enabled => _markets.Values.Where(m => m.Enabled);

    public MarketDefinition? FindEnabled(string? code) =>
        code is not null && _markets.TryGetValue(code, out var m) && m.Enabled ? m : null;

    public MarketDefinition Get(string code) =>
        _markets.TryGetValue(code, out var m) ? m : throw new KeyNotFoundException($"Unknown market {code}.");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    public static MarketCatalog FromJson(string json)
    {
        var file = JsonSerializer.Deserialize<MarketsFile>(json, JsonOptions)
                   ?? throw new InvalidOperationException("markets.json is empty.");
        return new MarketCatalog(file.Markets);
    }

    private sealed record MarketsFile(List<MarketDefinition> Markets);
}
