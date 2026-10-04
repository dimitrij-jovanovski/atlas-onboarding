using Atlas.Infrastructure.Persistence;

namespace Atlas.Infrastructure.Audit;

/// <summary>Who is acting. Every access to a customer record is attributed to one of these (GC §2).</summary>
public sealed record Actor(string Kind, string Id)
{
    /// <summary>The applicant, identified by the application they hold the access token for.</summary>
    public static Actor Applicant(Guid applicationId) => new("applicant", applicationId.ToString("N"));

    /// <summary>A named service identity. In production this is the service's own workload identity, not a shared login.</summary>
    public static Actor Service(string name) => new("service", name);

    public static Actor Staff(string staffId) => new("staff", staffId);
}

public static class AuditActions
{
    public const string ApplicationCreated = "application.created";
    public const string ApplicationRead = "application.read";
    public const string DocumentUploaded = "document.uploaded";
    public const string DocumentRead = "document.read";
    public const string ApplicationSubmitted = "application.submitted";
    public const string VerificationCompleted = "verification.completed";
    public const string VerificationRetryScheduled = "verification.retry_scheduled";
    public const string ReviewDecision = "review.decision";
    public const string BranchActivation = "branch.activation";
    public const string ErasureRequested = "erasure.requested";
    public const string AuditLogRead = "audit.read";
}

public static class AuditLogExtensions
{
    /// <summary>
    /// Adds an audit entry to the context. It is saved by the caller's SaveChanges, i.e. in the same
    /// transaction as the change it describes — an action cannot succeed without being logged.
    /// </summary>
    public static void Audit(this AtlasDbContext db, Actor actor, string action, Guid? applicationId, DateTime nowUtc,
        string? detail = null, string? correlationId = null)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            ApplicationId = applicationId,
            AtUtc = nowUtc,
            ActorKind = actor.Kind,
            ActorId = actor.Id,
            Action = action,
            Detail = detail,
            CorrelationId = correlationId,
        });
    }
}
