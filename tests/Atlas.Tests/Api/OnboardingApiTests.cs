using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas.Domain;
using Atlas.Infrastructure.Persistence;
using Atlas.Onboarding.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Tests.Api;

/// <summary>The mobile-facing contract, end to end through HTTP and real per-market databases.</summary>
public sealed class OnboardingApiTests : IDisposable
{
    private readonly TestDatabases _databases = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public OnboardingApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionStringTemplate"] = _databases.ConnectionStringTemplate,
                ["Onboarding:SyncWaitSeconds"] = "0", // no worker in these tests
                ["Seq:ServerUrl"] = "",
            })));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _databases.Dispose();
    }

    private static object TicketShapedRequest(string country = "MB", string nationalId = "0403991450016", bool terms = true) => new
    {
        firstName = "Ana",
        lastName = "Petrovska",
        dateOfBirth = "1991-03-04",
        country,
        nationalId,
        email = "ana@example.com",
        phone = "+38970000000",
        documents = new[]
        {
            new { type = "PASSPORT", image = Convert.ToBase64String(TestData.Jpeg) },
            new { type = "SELFIE", image = Convert.ToBase64String(TestData.Png) },
        },
        termsAccepted = terms,
    };

    [Fact]
    public async Task Ticket_shaped_single_call_creates_and_submits()
    {
        var response = await _client.PostAsJsonAsync("/applications", TicketShapedRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.StartsWith("MB-", body!.ApplicationId);
        Assert.Equal("IN_PROGRESS", body.Status); // decision would follow from the worker
        Assert.False(string.IsNullOrEmpty(body.AccessToken));
        Assert.Equal(new[] { "PASSPORT", "SELFIE" }, body.DocumentsReceived);
    }

    [Fact]
    public async Task Retrying_with_the_same_idempotency_key_returns_the_same_application()
    {
        var first = await Post(TicketShapedRequest(), "key-1");
        var second = await Post(TicketShapedRequest(), "key-1");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<ApplicationResponse>();
        var b = await second.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.Equal(a!.ApplicationId, b!.ApplicationId);
        Assert.Equal(a.AccessToken, b.AccessToken); // lost the first response in the tunnel? Still have the token.
        Assert.Equal(1, await CountApplications("MB"));
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_for_a_different_applicant_is_refused()
    {
        await Post(TicketShapedRequest(nationalId: "0403991450016"), "key-2");
        var other = await Post(TicketShapedRequest(nationalId: "1111111111111"), "key-2");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
    }

    [Fact]
    public async Task Save_and_resume_draft_then_upload_then_submit()
    {
        var created = await _client.PostAsJsonAsync("/applications", new
        {
            firstName = "Ana", lastName = "Petrovska", dateOfBirth = "1991-03-04", country = "MB",
            nationalId = "0403991450016", email = "ana@example.com", phone = "+38970000000",
        });
        var draft = (await created.Content.ReadFromJsonAsync<ApplicationResponse>())!;
        Assert.Equal("DRAFT", draft.Status);

        // ...train goes into a tunnel; later the app resumes with the id and token it stored...
        Assert.Equal(HttpStatusCode.OK, (await Upload(draft, "passport", TestData.Jpeg)).StatusCode);

        var tooEarly = await Send(HttpMethod.Post, $"/applications/{draft.ApplicationId}/submit", draft.AccessToken, new { termsAccepted = true });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooEarly.StatusCode); // selfie missing

        Assert.Equal(HttpStatusCode.OK, (await Upload(draft, "selfie", TestData.Png)).StatusCode);
        var submitted = await Send(HttpMethod.Post, $"/applications/{draft.ApplicationId}/submit", draft.AccessToken, new { termsAccepted = true });
        Assert.Equal("IN_PROGRESS", (await submitted.Content.ReadFromJsonAsync<ApplicationResponse>())!.Status);

        // Documents are frozen once submitted.
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(draft, "selfie", TestData.Png)).StatusCode);
    }

    [Fact]
    public async Task MF_resident_without_a_personal_number_can_apply_with_a_passport_number()
    {
        var response = await _client.PostAsJsonAsync("/applications", new
        {
            firstName = "Amir", lastName = "Haddad", dateOfBirth = "1988-07-12", country = "MF",
            identifier = new { type = "PASSPORT_NUMBER", value = "N1234567" },
            email = "amir@example.com", phone = "+1000000",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_identifier_and_unknown_market_are_rejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/applications", TicketShapedRequest(nationalId: "12345"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/applications", TicketShapedRequest(country: "MZ"))).StatusCode);
    }

    [Fact]
    public async Task Status_requires_the_applications_own_token()
    {
        var mine = (await (await _client.PostAsJsonAsync("/applications", TicketShapedRequest())).Content.ReadFromJsonAsync<ApplicationResponse>())!;
        var theirs = (await (await _client.PostAsJsonAsync("/applications", TicketShapedRequest())).Content.ReadFromJsonAsync<ApplicationResponse>())!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"/applications/{mine.ApplicationId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"/applications/{mine.ApplicationId}", theirs.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/applications/{mine.ApplicationId}", mine.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Personal_data_is_stored_only_in_the_applicants_market()
    {
        await _client.PostAsJsonAsync("/applications", TicketShapedRequest(country: "MB"));
        await _client.PostAsJsonAsync("/applications", TicketShapedRequest(country: "MD", nationalId: "0403991450"));

        foreach (var market in Markets.Catalog.Enabled.Select(m => m.Code))
        {
            await using var db = Store(market);
            var markets = await db.Applications.Select(a => a.Market).Distinct().ToListAsync();
            var documentsElsewhere = await db.Documents.CountAsync(d => !db.Applications.Any(a => a.Id == d.ApplicationId));
            Assert.All(markets, m => Assert.Equal(market, m));
            Assert.Equal(0, documentsElsewhere);
        }
        Assert.Equal(1, await CountApplications("MB"));
        Assert.Equal(1, await CountApplications("MD"));
        Assert.Equal(0, await CountApplications("MA"));
    }

    [Fact]
    public async Task Erasure_request_is_recorded_and_deferred_by_AML_retention()
    {
        var app = (await (await _client.PostAsJsonAsync("/applications", TicketShapedRequest())).Content.ReadFromJsonAsync<ApplicationResponse>())!;
        var response = await Send(HttpMethod.Post, $"/applications/{app.ApplicationId}/erasure-request", app.AccessToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DEFERRED", json.GetProperty("erasure").GetString());
    }

    // ---------------------------------------------------------------- helpers

    private Task<HttpResponseMessage> Post(object body, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/applications") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Upload(ApplicationResponse app, string type, byte[] bytes)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/applications/{app.ApplicationId}/documents/{type}")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add(AccessTokens.Header, app.AccessToken);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Add(AccessTokens.Header, token);
        return _client.SendAsync(request);
    }

    private AtlasDbContext Store(string market) =>
        _factory.Services.GetRequiredService<IMarketDbContextFactory>().Create(market);

    private async Task<int> CountApplications(string market)
    {
        await using var db = Store(market);
        return await db.Applications.CountAsync();
    }
}
