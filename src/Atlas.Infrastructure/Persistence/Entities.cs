using System.ComponentModel.DataAnnotations;
using Atlas.Domain;

namespace Atlas.Infrastructure.Persistence;

/// <summary>
/// An uploaded identity document or selfie. Kept in its own table so loading an application never
/// pulls several MB of images along with it.
///
/// Production would put the bytes in blob storage in the market's own region and keep only the
/// reference here; see DECISIONS.md for why this build keeps them in the market database.
/// </summary>
public sealed class StoredDocument
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public DocumentType Type { get; set; }
    public string ContentType { get; set; } = default!;
    public byte[] Content { get; set; } = default!;
    public string Sha256 { get; set; } = default!;
    public DateTime UploadedAtUtc { get; set; }
}

/// <summary>
/// Work item for the verification worker. Written in the same transaction as the submit, so a
/// submitted application can never be "lost" between the API and the worker (transactional outbox).
/// </summary>
public sealed class VerificationJob
{
    public Guid ApplicationId { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }

    /// <summary>Which worker instance holds the job. Concurrency token: a worker whose lease was taken over cannot save.</summary>
    [ConcurrencyCheck]
    public string? LeaseOwner { get; set; }

    public DateTime? LeaseUntilUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public static VerificationJob For(OnboardingApplication application, DateTime nowUtc) => new()
    {
        ApplicationId = application.Id,
        NextAttemptAtUtc = nowUtc,
        CreatedAtUtc = nowUtc,
    };
}

/// <summary>
/// Access log entry (GC §2): who touched which customer record, when, and how. Append-only — the
/// application never updates or deletes these rows, and in production the service accounts would
/// have INSERT-only permission on this table. Lives in the market database, so it is retained and
/// located with the record it describes.
/// </summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public Guid? ApplicationId { get; set; }
    public DateTime AtUtc { get; set; }
    public string ActorKind { get; set; } = default!;
    public string ActorId { get; set; } = default!;
    public string Action { get; set; } = default!;
    public string? Detail { get; set; }
    public string? CorrelationId { get; set; }
}
