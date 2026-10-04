using Atlas.Infrastructure;
using Atlas.Verification.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddAtlasCore(VerificationProcessor.ServiceIdentity);
builder.Services.AddOptions<WorkerOptions>().BindConfiguration("Worker");

builder.Services.AddHttpClient(HttpDocumentVerificationProvider.ClientName, (sp, client) =>
    ConfigureProvider(client, sp.GetRequiredService<IConfiguration>().GetSection("Providers:IdNow")));
builder.Services.AddHttpClient(HttpScreeningProvider.ClientName, (sp, client) =>
    ConfigureProvider(client, sp.GetRequiredService<IConfiguration>().GetSection("Providers:WorldCheck")));
builder.Services.AddSingleton<IDocumentVerificationProvider, HttpDocumentVerificationProvider>();
builder.Services.AddSingleton<IScreeningProvider, HttpScreeningProvider>();

builder.Services.AddSingleton<VerificationProcessor>();
builder.Services.AddHostedService<VerificationWorker>();

builder.Build().Run();

static void ConfigureProvider(HttpClient client, IConfigurationSection section)
{
    client.BaseAddress = new Uri(section["BaseUrl"] ?? throw new InvalidOperationException($"{section.Path}:BaseUrl is not configured."));
    client.Timeout = TimeSpan.FromSeconds(section.GetValue("TimeoutSeconds", 10));
    // Per-service credential from the vault in production; never a shared login (GC §2).
    var apiKey = section["ApiKey"];
    if (!string.IsNullOrEmpty(apiKey)) client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
}
