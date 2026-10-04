using Atlas.Domain;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Infrastructure.Persistence;

/// <summary>
/// One market's onboarding store. There is one database per market (see <see cref="MarketDbContextFactory"/>);
/// this context never sees more than one market's data.
/// </summary>
public sealed class AtlasDbContext(DbContextOptions<AtlasDbContext> options) : DbContext(options)
{
    public DbSet<OnboardingApplication> Applications => Set<OnboardingApplication>();
    public DbSet<StoredDocument> Documents => Set<StoredDocument>();
    public DbSet<VerificationJob> VerificationJobs => Set<VerificationJob>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<OnboardingApplication>(e =>
        {
            e.ToTable("Applications");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.Reference);
            e.Ignore(x => x.AcceptsDocuments);

            e.Property(x => x.Market).HasMaxLength(2).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.FirstName).HasMaxLength(200);
            e.Property(x => x.LastName).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Phone).HasMaxLength(32);
            e.Property(x => x.IdentifierType).HasMaxLength(32);
            e.Property(x => x.IdentifierValue).HasMaxLength(32);
            e.Property(x => x.DocumentCheck).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Screening).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ScreeningCaseReference).HasMaxLength(64);
            e.Property(x => x.RejectionReason).HasMaxLength(64);
            e.Property(x => x.ReviewedBy).HasMaxLength(100);
            e.Property(x => x.ReviewNotes).HasMaxLength(4000);
            e.Property(x => x.ActivatedBy).HasMaxLength(100);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.Property(x => x.RequestFingerprint).HasMaxLength(64);

            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.Status);
            // Deliberately NOT unique: the same person may apply more than once (e.g. after a rejection),
            // and Annex B note 2 says the identifier must not act as a key.
            e.HasIndex(x => new { x.IdentifierType, x.IdentifierValue });
        });

        b.Entity<StoredDocument>(e =>
        {
            e.ToTable("Documents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ContentType).HasMaxLength(64);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.HasIndex(x => new { x.ApplicationId, x.Type }).IsUnique();
            e.HasOne<OnboardingApplication>().WithMany().HasForeignKey(x => x.ApplicationId);
        });

        b.Entity<VerificationJob>(e =>
        {
            e.ToTable("VerificationJobs");
            e.HasKey(x => x.ApplicationId);
            e.Property(x => x.LeaseOwner).HasMaxLength(100);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => new { x.CompletedAtUtc, x.NextAttemptAtUtc });
            e.HasOne<OnboardingApplication>().WithOne().HasForeignKey<VerificationJob>(x => x.ApplicationId);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.HasKey(x => x.Id);
            e.Property(x => x.ActorKind).HasMaxLength(16);
            e.Property(x => x.ActorId).HasMaxLength(100);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.HasIndex(x => new { x.ApplicationId, x.AtUtc });
        });
    }
}
