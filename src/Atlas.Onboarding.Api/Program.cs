using System.Text.Json.Serialization;
using Atlas.Infrastructure;
using Atlas.Infrastructure.Persistence;
using Atlas.Infrastructure.Web;
using Atlas.Onboarding.Api;

var builder = WebApplication.CreateBuilder(args);

// Identity documents arrive base64-encoded in the ticket's one-call shape: allow a few MB per image.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 25 * 1024 * 1024);

builder.Services.AddAtlasCore("onboarding-api");
builder.Services.AddHostedService<DatabaseInitializer>(); // this service owns the schema
builder.Services.AddOptions<OnboardingOptions>().BindConfiguration("Onboarding");
builder.Services.AddSingleton<AccessTokens>();
builder.Services.AddSingleton<DecisionWaiter>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

var app = builder.Build();

app.UseExceptionHandler();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapApplicationEndpoints();

app.Run();

public partial class Program { }
