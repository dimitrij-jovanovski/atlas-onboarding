using System.Collections.Concurrent;

// Simulated IDNow (document + face match) and Refinitiv World-Check (sanctions/PEP screening).
//
// Outcomes are driven by the applicant's last name so every path can be exercised by hand:
//   contains "forged"  -> IDNow rejects the document
//   contains "match"   -> World-Check returns POSSIBLE_MATCH (application goes to manual review)
//   contains "flaky"   -> each provider returns 503 on its first call per application, then answers
//   anything else      -> VERIFIED / CLEAR
// Plus global knobs in appsettings: FakeProviders:LatencyMs and FakeProviders:FailureRate (0..1).

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var callCounts = new ConcurrentDictionary<string, int>();
var latency = app.Configuration.GetValue("FakeProviders:LatencyMs", 300);
var failureRate = app.Configuration.GetValue("FakeProviders:FailureRate", 0.0);

async Task<IResult?> Chaos(string provider, string? reference, string? lastName)
{
    await Task.Delay(latency);
    if (Random.Shared.NextDouble() < failureRate)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    if (Contains(lastName, "flaky"))
    {
        var n = callCounts.AddOrUpdate($"{provider}:{reference}", 1, (_, c) => c + 1);
        if (n <= 1) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    return null;
}

static bool Contains(string? value, string token) =>
    value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;

app.MapPost("/idnow/v1/verifications", async (IdNowRequest request) =>
{
    if (await Chaos("idnow", request.Reference, request.LastName) is { } failure) return failure;
    if (string.IsNullOrEmpty(request.DocumentImage) || string.IsNullOrEmpty(request.SelfieImage))
        return Results.BadRequest(new { error = "documentImage and selfieImage are required" });

    return Contains(request.LastName, "forged")
        ? Results.Ok(new { status = "REJECTED", reason = "DOCUMENT_TAMPERED" })
        : Results.Ok(new { status = "VERIFIED", reason = (string?)null });
});

app.MapPost("/worldcheck/v1/screenings", async (WorldCheckRequest request) =>
{
    if (await Chaos("worldcheck", request.Reference, request.LastName) is { } failure) return failure;

    var caseId = $"WC-{Guid.NewGuid():N}"[..15];
    return Contains(request.LastName, "match")
        ? Results.Ok(new { result = "POSSIBLE_MATCH", caseId })
        : Results.Ok(new { result = "CLEAR", caseId });
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

record IdNowRequest(string? Reference, string? Market, string? DocumentType, string? DocumentImage, string? SelfieImage,
    string? FirstName, string? LastName, DateOnly? DateOfBirth);

record WorldCheckRequest(string? Reference, string? Market, string? FirstName, string? LastName, DateOnly? DateOfBirth);
