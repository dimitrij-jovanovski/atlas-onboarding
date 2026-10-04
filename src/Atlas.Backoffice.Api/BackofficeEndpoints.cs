using System.Security.Claims;
using Atlas.Domain;
using Atlas.Infrastructure.Audit;
using Atlas.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Backoffice.Api;

public sealed record ReviewDecisionRequest(string? Decision, string? Notes);

public sealed record BranchActivationRequest(bool WetSignatureCaptured);

/// <summary>
/// Staff-facing API. Separate from the mobile API because it has different users, a different
/// authentication model, and must never be reachable from the public internet.
///
/// Every staff member is scoped to one market by a claim; they cannot see or act on any other
/// market's applications. Every read of personal data and every decision is written to the access log
/// as that named person (GC §2).
/// </summary>
public static class BackofficeEndpoints
{
    /// <summary>Compliance's planning figure for manual review (GC §3).</summary>
    private static readonly TimeSpan ReviewTarget = TimeSpan.FromHours(48);

    public static void MapBackofficeEndpoints(this IEndpointRouteBuilder app)
    {
        var review = app.MapGroup("/review").RequireAuthorization(StaffRoles.ComplianceOfficer);
        review.MapGet("/queue", Queue);
        review.MapGet("/applications/{applicationId}", ReviewDetail);
        review.MapGet("/applications/{applicationId}/documents/{documentType}", Document);
        review.MapPost("/applications/{applicationId}/decision", Decide);
        review.MapGet("/applications/{applicationId}/audit", AuditTrail);

        var branch = app.MapGroup("/branch").RequireAuthorization(StaffRoles.BranchStaff);
        branch.MapGet("/applications/{applicationId}", BranchDetail);
        branch.MapPost("/applications/{applicationId}/activate", Activate);
    }

    /// <summary>The officer's own market's referrals, oldest first. No personal data in the list.</summary>
    private static async Task<IResult> Queue(ClaimsPrincipal user, [FromServices] IMarketDbContextFactory factory,
        [FromServices] TimeProvider clock, CancellationToken ct)
    {
        var market = user.StaffMarket();
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = factory.Create(market);

        var items = await db.Applications.AsNoTracking()
            .Where(a => a.Status == ApplicationStatus.ReferredForReview)
            .OrderBy(a => a.SubmittedAtUtc)
            .Select(a => new { a.Id, a.Market, a.SubmittedAtUtc, a.ScreeningCaseReference })
            .ToListAsync(ct);

        return Results.Ok(items.Select(a => new
        {
            applicationId = new ApplicationRef(a.Market, a.Id).ToString(),
            submittedAtUtc = a.SubmittedAtUtc,
            dueByUtc = a.SubmittedAtUtc + ReviewTarget,
            overdue = a.SubmittedAtUtc + ReviewTarget < now,
            screeningCaseReference = a.ScreeningCaseReference,
        }));
    }

    private static async Task<IResult> ReviewDetail(string applicationId, ClaimsPrincipal user,
        [FromServices] IMarketDbContextFactory factory, [FromServices] TimeProvider clock, CancellationToken ct)
    {
        if (!TryScope(applicationId, user, out var reference, out var failure)) return failure!;
        await using var db = factory.Create(reference.Market);
        var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == reference.Id, ct);
        if (application is null) return Results.NotFound();

        db.Audit(Actor.Staff(user.StaffId()), AuditActions.ApplicationRead, application.Id, Now(clock), "review detail");
        await db.SaveChangesAsync(ct);
        return Results.Ok(Detail(application));
    }

    private static async Task<IResult> Document(string applicationId, string documentType, ClaimsPrincipal user,
        [FromServices] IMarketDbContextFactory factory, [FromServices] TimeProvider clock, CancellationToken ct)
    {
        if (!TryScope(applicationId, user, out var reference, out var failure)) return failure!;
        if (!Enum.TryParse<DocumentType>(documentType.Replace("_", ""), ignoreCase: true, out var type))
            return Results.NotFound();

        await using var db = factory.Create(reference.Market);
        var document = await db.Documents.AsNoTracking()
            .SingleOrDefaultAsync(d => d.ApplicationId == reference.Id && d.Type == type, ct);
        if (document is null) return Results.NotFound();

        db.Audit(Actor.Staff(user.StaffId()), AuditActions.DocumentRead, reference.Id, Now(clock), type.ToString());
        await db.SaveChangesAsync(ct);
        return Results.File(document.Content, document.ContentType);
    }

    private static async Task<IResult> Decide(string applicationId, ReviewDecisionRequest request, ClaimsPrincipal user,
        [FromServices] IMarketDbContextFactory factory, [FromServices] MarketCatalog markets,
        [FromServices] TimeProvider clock, [FromServices] ILoggerFactory loggers, CancellationToken ct)
    {
        if (!TryScope(applicationId, user, out var reference, out var failure)) return failure!;
        if (!Enum.TryParse<ReviewDecision>(request.Decision, ignoreCase: true, out var decision))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["decision"] = ["Expected APPROVE or REJECT."] });

        await using var db = factory.Create(reference.Market);
        var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == reference.Id, ct);
        if (application is null) return Results.NotFound();

        var now = Now(clock);
        application.RecordReviewDecision(user.StaffId(), user.StaffMarket(), decision, request.Notes ?? "",
            markets.Get(application.Market).Activation, now);
        db.Audit(Actor.Staff(user.StaffId()), AuditActions.ReviewDecision, application.Id, now, $"{decision}; status={application.Status}");
        await db.SaveChangesAsync(ct);

        loggers.CreateLogger("Atlas.Review").LogInformation("Application {ApplicationRef} reviewed by {StaffId}: {Status}",
            reference.ToString(), user.StaffId(), application.Status);
        return Results.Ok(Detail(application));
    }

    private static async Task<IResult> AuditTrail(string applicationId, ClaimsPrincipal user,
        [FromServices] IMarketDbContextFactory factory, [FromServices] TimeProvider clock, CancellationToken ct)
    {
        if (!TryScope(applicationId, user, out var reference, out var failure)) return failure!;
        await using var db = factory.Create(reference.Market);

        var entries = await db.AuditEntries.AsNoTracking()
            .Where(e => e.ApplicationId == reference.Id)
            .OrderBy(e => e.Id)
            .Select(e => new { e.AtUtc, e.ActorKind, e.ActorId, e.Action, e.Detail })
            .ToListAsync(ct);

        db.Audit(Actor.Staff(user.StaffId()), AuditActions.AuditLogRead, reference.Id, Now(clock));
        await db.SaveChangesAsync(ct);
        return Results.Ok(entries);
    }

    /// <summary>Branch staff look up the application the customer brings in (the reference is shown in the app).</summary>
    private static async Task<IResult> BranchDetail(string applicationId, ClaimsPrincipal user,
        [FromServices] IMarketDbContextFactory factory, [FromServices] TimeProvider clock, CancellationToken ct)
    {
        if (!TryScope(applicationId, user, out var reference, out var failure)) return failure!;
        await using var db = factory.Create(reference.Market);
        var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == reference.Id, ct);
        if (application is null) return Results.NotFound();

        db.Audit(Actor.Staff(user.StaffId()), AuditActions.ApplicationRead, application.Id, Now(clock), "branch lookup");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new
        {
            applicationId = reference.ToString(),
            status = application.Status.ToString(),
            application.FirstName,
            application.LastName,
            application.DateOfBirth,
            application.IdentifierType,
            application.IdentifierValue,
        });
    }

    private static async Task<IResult> Activate(string applicationId, BranchActivationRequest request, ClaimsPrincipal user,
        [FromServices] IMarketDbContextFactory factory, [FromServices] TimeProvider clock, CancellationToken ct)
    {
        if (!TryScope(applicationId, user, out var reference, out var failure)) return failure!;
        await using var db = factory.Create(reference.Market);
        var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == reference.Id, ct);
        if (application is null) return Results.NotFound();

        var now = Now(clock);
        application.ActivateAtBranch(user.StaffId(), user.StaffMarket(), request.WetSignatureCaptured, now);
        db.Audit(Actor.Staff(user.StaffId()), AuditActions.BranchActivation, application.Id, now, "wet signature captured");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { applicationId = reference.ToString(), status = application.Status.ToString() });
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Parses the reference and checks the caller works in that market. Cross-market access is refused.</summary>
    private static bool TryScope(string applicationId, ClaimsPrincipal user, out ApplicationRef reference, out IResult? failure)
    {
        failure = null;
        if (!ApplicationRef.TryParse(applicationId, out reference))
        {
            failure = Results.NotFound();
            return false;
        }

        if (!string.Equals(reference.Market, user.StaffMarket(), StringComparison.OrdinalIgnoreCase))
        {
            failure = Results.Forbid();
            return false;
        }

        return true;
    }

    private static object Detail(OnboardingApplication a) => new
    {
        applicationId = a.Reference.ToString(),
        status = a.Status.ToString(),
        a.Market,
        a.FirstName,
        a.LastName,
        a.DateOfBirth,
        a.IdentifierType,
        a.IdentifierValue,
        a.Email,
        a.Phone,
        a.SubmittedAtUtc,
        documentCheck = a.DocumentCheck?.ToString(),
        screening = a.Screening?.ToString(),
        a.ScreeningCaseReference,
        a.ReviewedBy,
        a.ReviewedAtUtc,
        a.ReviewNotes,
        a.RetainUntilUtc,
        a.ErasureRequestedAtUtc,
    };

    private static DateTime Now(TimeProvider clock) => clock.GetUtcNow().UtcDateTime;
}
