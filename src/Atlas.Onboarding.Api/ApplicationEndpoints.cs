using System.Security.Cryptography;
using System.Text;
using Atlas.Domain;
using Atlas.Infrastructure.Audit;
using Atlas.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Atlas.Onboarding.Api;

public sealed record IdentifierDto(string? Type, string? Value);

public sealed record DocumentDto(string? Type, string? Image);

/// <summary>
/// The ticket's request shape, kept so mobile can call it unchanged, with one addition:
/// <see cref="Identifier"/> ({type, value}) for markets where the national ID is not the only option
/// (MF passport holders). The flat <see cref="NationalId"/> still works and maps to the market's default type.
/// </summary>
public sealed record CreateApplicationRequest(
    string? FirstName,
    string? LastName,
    DateOnly? DateOfBirth,
    string? Country,
    string? NationalId,
    IdentifierDto? Identifier,
    string? Email,
    string? Phone,
    List<DocumentDto>? Documents,
    bool? TermsAccepted);

public sealed record SubmitRequest(bool TermsAccepted);

public sealed record ApplicationResponse(
    string ApplicationId,
    string Status,
    string NextStep,
    IReadOnlyList<string> DocumentsReceived,
    string? AccessToken = null);

/// <summary>
/// Mobile-facing API.
///
///   POST /applications                          create (draft), or create + submit in one call (ticket shape)
///   PUT  /applications/{id}/documents/{type}    upload one image as raw bytes (save-and-resume)
///   POST /applications/{id}/submit              submit a draft
///   GET  /applications/{id}?waitSeconds=n       status, optionally long-polling for a decision
///   POST /applications/{id}/erasure-request     right-to-erasure request (GC §5, deferred by §4)
///
/// Everything after create requires the X-Application-Token returned by create.
/// </summary>
public static class ApplicationEndpoints
{
    public static void MapApplicationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/applications");
        group.MapPost("/", Create);
        group.MapPut("/{applicationId}/documents/{documentType}", UploadDocument);
        group.MapPost("/{applicationId}/submit", Submit);
        group.MapGet("/{applicationId}", Get);
        group.MapPost("/{applicationId}/erasure-request", RequestErasure);
    }

    private const int MaxLongPollSeconds = 30;

    private static async Task<IResult> Create(
        CreateApplicationRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromServices] MarketCatalog markets,
        [FromServices] IMarketDbContextFactory factory,
        [FromServices] AccessTokens tokens,
        [FromServices] DecisionWaiter waiter,
        [FromServices] IOptions<OnboardingOptions> options,
        [FromServices] TimeProvider clock,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var market = markets.FindEnabled(request.Country);
        if (market is null)
            return Invalid("country", $"Unknown market, or market not enabled: '{request.Country}'.");

        var errors = new Dictionary<string, string[]>();
        var now = clock.GetUtcNow().UtcDateTime;

        var identifierType = request.Identifier?.Type ?? (request.NationalId is not null ? market.DefaultIdentifierType : null);
        var identifierValue = request.Identifier?.Value ?? request.NationalId;
        if (identifierType is null || identifierValue is null)
            errors["identifier"] = ["An identifier is required: either identifier {type, value} or nationalId."];
        if (request.DateOfBirth is null)
            errors["dateOfBirth"] = ["Date of birth is required."];

        var applicant = new ApplicantDetails(
            request.FirstName ?? "", request.LastName ?? "", request.DateOfBirth ?? default,
            request.Email ?? "", request.Phone ?? "", identifierType ?? "", identifierValue ?? "");

        if (errors.Count == 0)
            foreach (var e in ApplicantValidator.Validate(applicant, market, DateOnly.FromDateTime(now)))
                errors[e.Field] = [e.Message];

        var documents = ParseDocuments(request.Documents, options.Value.MaxDocumentBytes, errors);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var fingerprint = Fingerprint(market.Code, applicant, documents);

        await using var db = factory.Create(market.Code);

        if (idempotencyKey is not null)
        {
            var existing = await FindByIdempotencyKey(db, idempotencyKey, ct);
            if (existing is not null)
                return await Replay(existing, fingerprint, db, tokens, waiter, options.Value, ct);
        }

        var application = OnboardingApplication.Create(market, applicant, now, idempotencyKey, fingerprint);
        var actor = Actor.Applicant(application.Id);
        db.Applications.Add(application);
        db.Audit(actor, AuditActions.ApplicationCreated, application.Id, now);

        foreach (var doc in documents)
        {
            db.Documents.Add(NewDocument(application.Id, doc.Type, doc.ContentType, doc.Bytes, now));
            db.Audit(actor, AuditActions.DocumentUploaded, application.Id, now, DocumentTypes.ToApi(doc.Type));
        }

        if (request.TermsAccepted == true)
        {
            application.Submit(true, documents.Select(d => d.Type).ToList(), now);
            db.VerificationJobs.Add(VerificationJob.For(application, now));
            db.Audit(actor, AuditActions.ApplicationSubmitted, application.Id, now);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (idempotencyKey is not null)
        {
            // Same key, two replicas, same moment: the unique index lets exactly one win. Answer as the winner did.
            await using var fresh = factory.Create(market.Code);
            var winner = await FindByIdempotencyKey(fresh, idempotencyKey, ct);
            if (winner is null) throw;
            return await Replay(winner, fingerprint, fresh, tokens, waiter, options.Value, ct);
        }

        loggerFactory.CreateLogger("Atlas.Onboarding")
            .LogInformation("Application {ApplicationRef} created with status {Status}", application.Reference.ToString(), application.Status);

        var status = await MaybeWait(application.Reference, application.Status, options.Value.SyncWaitSeconds, waiter, ct);
        var body = new ApplicationResponse(
            application.Reference.ToString(),
            ApiStatus.From(status),
            ApiStatus.NextStep(status),
            documents.Select(d => DocumentTypes.ToApi(d.Type)).ToList(),
            tokens.Issue(application.Reference));

        return Results.Created($"/applications/{application.Reference}", body);
    }

    private static async Task<IResult> UploadDocument(
        string applicationId,
        string documentType,
        HttpRequest http,
        [FromServices] MarketCatalog markets,
        [FromServices] IMarketDbContextFactory factory,
        [FromServices] AccessTokens tokens,
        [FromServices] IOptions<OnboardingOptions> options,
        [FromServices] TimeProvider clock,
        CancellationToken ct)
    {
        if (!TryAuthorize(applicationId, http, tokens, markets, out var reference, out var failure)) return failure!;
        if (!DocumentTypes.TryParse(documentType, out var type))
            return Invalid("documentType", "Expected PASSPORT, ID_CARD or SELFIE.");

        var max = options.Value.MaxDocumentBytes;
        using var buffer = new MemoryStream();
        await http.Body.CopyToAsync(buffer, ct);
        if (buffer.Length == 0) return Invalid("body", "Empty document.");
        if (buffer.Length > max) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var bytes = buffer.ToArray();
        var contentType = DocumentTypes.DetectImage(bytes);
        if (contentType is null) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = factory.Create(reference.Market);
        var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == reference.Id, ct);
        if (application is null) return Results.NotFound();
        application.EnsureAcceptsDocuments();

        var existing = await db.Documents.SingleOrDefaultAsync(d => d.ApplicationId == application.Id && d.Type == type, ct);
        if (existing is null)
        {
            db.Documents.Add(NewDocument(application.Id, type, contentType, bytes, now));
        }
        else
        {
            existing.Content = bytes;
            existing.ContentType = contentType;
            existing.Sha256 = Sha256Hex(bytes);
            existing.UploadedAtUtc = now;
        }

        db.Audit(Actor.Applicant(application.Id), AuditActions.DocumentUploaded, application.Id, now, DocumentTypes.ToApi(type));
        await db.SaveChangesAsync(ct);

        return Results.Ok(await Describe(db, application.Reference, application.Status, ct));
    }

    private static async Task<IResult> Submit(
        string applicationId,
        SubmitRequest request,
        HttpRequest http,
        [FromServices] MarketCatalog markets,
        [FromServices] IMarketDbContextFactory factory,
        [FromServices] AccessTokens tokens,
        [FromServices] DecisionWaiter waiter,
        [FromServices] IOptions<OnboardingOptions> options,
        [FromServices] TimeProvider clock,
        CancellationToken ct)
    {
        if (!TryAuthorize(applicationId, http, tokens, markets, out var reference, out var failure)) return failure!;

        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = factory.Create(reference.Market);
        var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == reference.Id, ct);
        if (application is null) return Results.NotFound();

        var uploaded = await db.Documents.Where(d => d.ApplicationId == application.Id).Select(d => d.Type).ToListAsync(ct);
        if (application.Submit(request.TermsAccepted, uploaded, now))
        {
            db.VerificationJobs.Add(VerificationJob.For(application, now));
            db.Audit(Actor.Applicant(application.Id), AuditActions.ApplicationSubmitted, application.Id, now);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // A concurrent submit already created the job (primary key on ApplicationId). Same outcome.
            }
        }

        var status = await MaybeWait(reference, application.Status, options.Value.SyncWaitSeconds, waiter, ct);
        return Results.Ok(await Describe(db, reference, status, ct));
    }

    private static async Task<IResult> Get(
        string applicationId,
        int? waitSeconds,
        HttpRequest http,
        [FromServices] MarketCatalog markets,
        [FromServices] IMarketDbContextFactory factory,
        [FromServices] AccessTokens tokens,
        [FromServices] DecisionWaiter waiter,
        CancellationToken ct)
    {
        if (!TryAuthorize(applicationId, http, tokens, markets, out var reference, out var failure)) return failure!;

        // Returns status only, no personal data, so this read is not written to the access log;
        // otherwise every poll would add a row.
        await using var db = factory.Create(reference.Market);
        var status = await db.Applications.AsNoTracking()
            .Where(a => a.Id == reference.Id).Select(a => (ApplicationStatus?)a.Status).SingleOrDefaultAsync(ct);
        if (status is null) return Results.NotFound();

        var final = await MaybeWait(reference, status.Value, Math.Clamp(waitSeconds ?? 0, 0, MaxLongPollSeconds), waiter, ct);
        return Results.Ok(await Describe(db, reference, final, ct));
    }

    private static async Task<IResult> RequestErasure(
        string applicationId,
        HttpRequest http,
        [FromServices] MarketCatalog markets,
        [FromServices] IMarketDbContextFactory factory,
        [FromServices] AccessTokens tokens,
        [FromServices] TimeProvider clock,
        CancellationToken ct)
    {
        if (!TryAuthorize(applicationId, http, tokens, markets, out var reference, out var failure)) return failure!;

        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = factory.Create(reference.Market);
        var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == reference.Id, ct);
        if (application is null) return Results.NotFound();

        application.RequestErasure(now);
        db.Audit(Actor.Applicant(application.Id), AuditActions.ErasureRequested, application.Id, now);
        await db.SaveChangesAsync(ct);

        return Results.Accepted(value: new
        {
            applicationId = reference.ToString(),
            erasure = "DEFERRED",
            retainUntil = application.RetainUntilUtc,
            reason = "Onboarding records are retained for ten years under anti-money-laundering obligations. " +
                     "Your request is recorded and your data will be erased when that period ends.",
        });
    }

    // ---------------------------------------------------------------- helpers

    private sealed record ParsedDocument(DocumentType Type, string ContentType, byte[] Bytes);

    private static List<ParsedDocument> ParseDocuments(List<DocumentDto>? input, int maxBytes, Dictionary<string, string[]> errors)
    {
        var parsed = new List<ParsedDocument>();
        if (input is null) return parsed;

        for (var i = 0; i < input.Count; i++)
        {
            var field = $"documents[{i}]";
            if (!DocumentTypes.TryParse(input[i].Type, out var type))
            {
                errors[field + ".type"] = ["Expected PASSPORT, ID_CARD or SELFIE."];
                continue;
            }

            byte[] bytes;
            try { bytes = Convert.FromBase64String(input[i].Image ?? ""); }
            catch (FormatException) { errors[field + ".image"] = ["Not valid base64."]; continue; }

            if (bytes.Length == 0 || bytes.Length > maxBytes) { errors[field + ".image"] = [$"Image must be 1 byte to {maxBytes} bytes."]; continue; }
            var contentType = DocumentTypes.DetectImage(bytes);
            if (contentType is null) { errors[field + ".image"] = ["Image must be JPEG or PNG."]; continue; }
            if (parsed.Any(p => p.Type == type)) { errors[field + ".type"] = ["Duplicate document type."]; continue; }

            parsed.Add(new ParsedDocument(type, contentType, bytes));
        }

        return parsed;
    }

    private static StoredDocument NewDocument(Guid applicationId, DocumentType type, string contentType, byte[] bytes, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationId = applicationId,
        Type = type,
        ContentType = contentType,
        Content = bytes,
        Sha256 = Sha256Hex(bytes),
        UploadedAtUtc = now,
    };

    private static bool TryAuthorize(string applicationId, HttpRequest http, AccessTokens tokens, MarketCatalog markets,
        out ApplicationRef reference, out IResult? failure)
    {
        failure = null;
        if (!ApplicationRef.TryParse(applicationId, out reference) || markets.FindEnabled(reference.Market) is null)
        {
            failure = Results.NotFound();
            return false;
        }

        if (!tokens.IsValid(reference, http.Headers[AccessTokens.Header].FirstOrDefault()))
        {
            failure = Results.Unauthorized();
            return false;
        }

        return true;
    }

    private static Task<OnboardingApplication?> FindByIdempotencyKey(AtlasDbContext db, string key, CancellationToken ct) =>
        db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.IdempotencyKey == key, ct);

    private static async Task<IResult> Replay(OnboardingApplication existing, string fingerprint, AtlasDbContext db,
        AccessTokens tokens, DecisionWaiter waiter, OnboardingOptions options, CancellationToken ct)
    {
        if (existing.RequestFingerprint != fingerprint)
            return Results.Problem(
                title: "This Idempotency-Key was already used for a different application.",
                type: "idempotency_key_reused",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        var status = await MaybeWait(existing.Reference, existing.Status, options.SyncWaitSeconds, waiter, ct);
        var body = await Describe(db, existing.Reference, status, ct) with { AccessToken = tokens.Issue(existing.Reference) };
        return Results.Ok(body);
    }

    private static async Task<ApplicationStatus> MaybeWait(ApplicationRef reference, ApplicationStatus status, int seconds,
        DecisionWaiter waiter, CancellationToken ct) =>
        status == ApplicationStatus.Submitted && seconds > 0
            ? await waiter.WaitAsync(reference, TimeSpan.FromSeconds(seconds), ct)
            : status;

    private static async Task<ApplicationResponse> Describe(AtlasDbContext db, ApplicationRef reference, ApplicationStatus status, CancellationToken ct)
    {
        var docs = await db.Documents.AsNoTracking()
            .Where(d => d.ApplicationId == reference.Id)
            .Select(d => d.Type)
            .ToListAsync(ct);
        return new ApplicationResponse(
            reference.ToString(),
            ApiStatus.From(status),
            ApiStatus.NextStep(status),
            docs.Select(DocumentTypes.ToApi).OrderBy(x => x).ToList());
    }

    private static string Fingerprint(string market, ApplicantDetails a, IEnumerable<ParsedDocument> documents)
    {
        var canonical = string.Join('|',
            market, a.FirstName.Trim(), a.LastName.Trim(), a.DateOfBirth.ToString("O"),
            a.IdentifierType.ToUpperInvariant(), IdentifierFormats.Normalise(a.IdentifierValue),
            a.Email.Trim().ToLowerInvariant(), a.Phone.Trim(),
            string.Join(',', documents.OrderBy(d => d.Type).Select(d => $"{d.Type}:{Sha256Hex(d.Bytes)}")));
        return Sha256Hex(Encoding.UTF8.GetBytes(canonical));
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
