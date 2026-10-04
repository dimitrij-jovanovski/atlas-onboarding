using System.Security.Cryptography;
using System.Text;
using Atlas.Domain;
using Atlas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Atlas.Onboarding.Api;

public sealed class OnboardingOptions
{
    /// <summary>
    /// How long the create/submit call holds the connection waiting for an automated decision.
    /// Keeps "one call in, one answer out" for the common clean case without promising it for
    /// referrals (up to 48h) or branch activation.
    /// </summary>
    public int SyncWaitSeconds { get; set; } = 20;

    /// <summary>Secret used to derive per-application access tokens. From the market's vault in production.</summary>
    public string AccessTokenKey { get; set; } = default!;

    public int MaxDocumentBytes { get; set; } = 10 * 1024 * 1024;
}

/// <summary>
/// The applicant's credential for their own application: HMAC(secret, applicationId).
///
/// Deterministic, so an idempotent replay of POST /applications (the response was lost in the tunnel)
/// can hand back the same token without the server ever storing it.
/// </summary>
public sealed class AccessTokens(IOptions<OnboardingOptions> options)
{
    public const string Header = "X-Application-Token";

    public string Issue(ApplicationRef reference)
    {
        var key = Encoding.UTF8.GetBytes(options.Value.AccessTokenKey);
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(reference.ToString()));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public bool IsValid(ApplicationRef reference, string? token) =>
        token is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Issue(reference)), Encoding.UTF8.GetBytes(token));
}

/// <summary>Holds a request open until the application leaves the automated part of the flow, or a timeout.</summary>
public sealed class DecisionWaiter(IMarketDbContextFactory factory, TimeProvider clock)
{
    public async Task<ApplicationStatus> WaitAsync(ApplicationRef reference, TimeSpan maxWait, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow() + maxWait;
        while (true)
        {
            await using var db = factory.Create(reference.Market);
            var status = await db.Applications.AsNoTracking()
                .Where(a => a.Id == reference.Id)
                .Select(a => a.Status)
                .SingleAsync(ct);

            if (status != ApplicationStatus.Submitted || clock.GetUtcNow() >= deadline) return status;
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }
}

/// <summary>Customer-facing status. Deliberately does not reveal *why* an application is pending or rejected.</summary>
public static class ApiStatus
{
    public static string From(ApplicationStatus status) => status switch
    {
        ApplicationStatus.Draft => "DRAFT",
        ApplicationStatus.Submitted => "IN_PROGRESS",
        // Not "SANCTIONS_REVIEW": telling a customer they matched a sanctions list is tipping-off.
        ApplicationStatus.ReferredForReview => "PENDING_REVIEW",
        ApplicationStatus.AwaitingBranchActivation => "PENDING_BRANCH_VISIT",
        ApplicationStatus.Approved => "APPROVED",
        ApplicationStatus.Rejected => "REJECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static string NextStep(ApplicationStatus status) => status switch
    {
        ApplicationStatus.Draft => "Upload an identity document and a selfie, then submit with termsAccepted=true.",
        ApplicationStatus.Submitted => "Verification is in progress. Check back shortly.",
        ApplicationStatus.ReferredForReview => "Your application needs a further check. We will contact you, usually within 2 working days.",
        ApplicationStatus.AwaitingBranchActivation => "Please visit a branch with your identity document to sign and activate your account.",
        ApplicationStatus.Approved => "Your account is open.",
        ApplicationStatus.Rejected => "We could not open an account for you.",
        _ => "",
    };
}

public static class DocumentTypes
{
    public static bool TryParse(string? value, out DocumentType type)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "PASSPORT": type = DocumentType.Passport; return true;
            case "ID_CARD": type = DocumentType.IdCard; return true;
            case "SELFIE": type = DocumentType.Selfie; return true;
            default: type = default; return false;
        }
    }

    public static string ToApi(DocumentType type) => type switch
    {
        DocumentType.Passport => "PASSPORT",
        DocumentType.IdCard => "ID_CARD",
        DocumentType.Selfie => "SELFIE",
        _ => type.ToString(),
    };

    /// <summary>Sniffs JPEG/PNG magic bytes; returns the content type or null.</summary>
    public static string? DetectImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        return null;
    }
}
