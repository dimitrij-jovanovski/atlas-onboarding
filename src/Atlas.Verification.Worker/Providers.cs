using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Atlas.Domain;

namespace Atlas.Verification.Worker;

// ---------------------------------------------------------------- contracts

public sealed record DocumentVerificationRequest(
    string Reference,
    string Market,
    DocumentType DocumentType,
    byte[] DocumentImage,
    byte[] SelfieImage,
    string FirstName,
    string LastName,
    DateOnly DateOfBirth);

public sealed record DocumentVerificationResult(DocumentCheckOutcome Outcome, string? Reason);

public sealed record ScreeningRequest(string Reference, string Market, string FirstName, string LastName, DateOnly DateOfBirth);

public sealed record ScreeningResult(ScreeningOutcome Outcome, string CaseReference);

/// <summary>IDNow: document authenticity and face match.</summary>
public interface IDocumentVerificationProvider
{
    Task<DocumentVerificationResult> VerifyAsync(DocumentVerificationRequest request, CancellationToken ct);
}

/// <summary>Refinitiv World-Check: sanctions and PEP screening.</summary>
public interface IScreeningProvider
{
    Task<ScreeningResult> ScreenAsync(ScreeningRequest request, CancellationToken ct);
}

/// <summary>The provider could not give an answer right now (down, timing out, throttling). Retry later.</summary>
public sealed class ProviderUnavailableException(string provider, string message, Exception? inner = null)
    : Exception($"{provider}: {message}", inner);

// ---------------------------------------------------------------- HTTP implementations

/// <summary>
/// Thin REST clients. The wire format matches Atlas.FakeProviders; the real IDNow / World-Check
/// contracts were not available (credentials "in the vault"), so these are the seam to replace.
/// </summary>
public sealed class HttpDocumentVerificationProvider(IHttpClientFactory httpClients) : IDocumentVerificationProvider
{
    public const string ClientName = "idnow";

    public async Task<DocumentVerificationResult> VerifyAsync(DocumentVerificationRequest r, CancellationToken ct)
    {
        var body = new
        {
            reference = r.Reference,
            market = r.Market,
            documentType = r.DocumentType.ToString().ToUpperInvariant(),
            documentImage = Convert.ToBase64String(r.DocumentImage),
            selfieImage = Convert.ToBase64String(r.SelfieImage),
            firstName = r.FirstName,
            lastName = r.LastName,
            dateOfBirth = r.DateOfBirth,
        };

        var response = await ProviderHttp.PostAsync<IdNowResponse>(httpClients.CreateClient(ClientName), "idnow", "v1/verifications", body, ct);
        return response.Status switch
        {
            "VERIFIED" => new DocumentVerificationResult(DocumentCheckOutcome.Verified, null),
            "REJECTED" => new DocumentVerificationResult(DocumentCheckOutcome.Failed, response.Reason),
            _ => throw new ProviderUnavailableException("idnow", $"Unexpected status '{response.Status}'."),
        };
    }

    private sealed record IdNowResponse([property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("reason")] string? Reason);
}

public sealed class HttpScreeningProvider(IHttpClientFactory httpClients) : IScreeningProvider
{
    public const string ClientName = "worldcheck";

    public async Task<ScreeningResult> ScreenAsync(ScreeningRequest r, CancellationToken ct)
    {
        var body = new
        {
            reference = r.Reference,
            market = r.Market,
            firstName = r.FirstName,
            lastName = r.LastName,
            dateOfBirth = r.DateOfBirth,
        };

        var response = await ProviderHttp.PostAsync<WorldCheckResponse>(httpClients.CreateClient(ClientName), "worldcheck", "v1/screenings", body, ct);
        return response.Result switch
        {
            "CLEAR" => new ScreeningResult(ScreeningOutcome.Clear, response.CaseId),
            // Anything that is not an unambiguous CLEAR is treated as a possible match and goes to a human.
            _ => new ScreeningResult(ScreeningOutcome.PossibleMatch, response.CaseId),
        };
    }

    private sealed record WorldCheckResponse([property: JsonPropertyName("result")] string Result, [property: JsonPropertyName("caseId")] string CaseId);
}

internal static class ProviderHttp
{
    public static async Task<T> PostAsync<T>(HttpClient client, string provider, string path, object body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(path, body, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(provider, "connection failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException(provider, "timed out", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500)
                throw new ProviderUnavailableException(provider, $"HTTP {(int)response.StatusCode}");

            // A 4xx means we sent something wrong. Retrying will not fix it, but failing the customer for our
            // bug is worse; it is retried with backoff and logged as an error for someone to look at.
            if (!response.IsSuccessStatusCode)
                throw new ProviderUnavailableException(provider, $"HTTP {(int)response.StatusCode} (request rejected — check integration)");

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
                   ?? throw new ProviderUnavailableException(provider, "empty response");
        }
    }
}
