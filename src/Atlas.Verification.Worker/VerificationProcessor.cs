using Atlas.Domain;
using Atlas.Infrastructure.Audit;
using Atlas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Atlas.Verification.Worker;

public sealed class WorkerOptions
{
    public int PollIntervalMs { get; set; } = 500;
    public int BatchSize { get; set; } = 10;
    public int LeaseSeconds { get; set; } = 120;
    public int MaxBackoffSeconds { get; set; } = 300;

    /// <summary>After this many failed attempts each retry is logged as an error, so someone notices.</summary>
    public int AlertAfterAttempts { get; set; } = 5;
}

/// <summary>
/// Runs automated verification for submitted applications.
///
/// Safe with several replicas (the platform runs three of everything): a job is claimed with a
/// conditional UPDATE, and the final save is guarded by a concurrency check on the lease owner, so
/// two workers can never both record an outcome. A provider outage never rejects anyone — the job is
/// rescheduled with exponential backoff and the application stays IN_PROGRESS.
/// </summary>
public sealed class VerificationProcessor(
    IMarketDbContextFactory factory,
    MarketCatalog markets,
    IDocumentVerificationProvider documentProvider,
    IScreeningProvider screeningProvider,
    TimeProvider clock,
    IOptions<WorkerOptions> options,
    ILogger<VerificationProcessor> logger)
{
    public const string ServiceIdentity = "verification-worker";

    /// <summary>Identifies this replica in job leases.</summary>
    public string InstanceId { get; } = Truncate($"{Environment.MachineName}/{Guid.NewGuid():N}", 100);

    private static readonly Actor Self = Actor.Service(ServiceIdentity);

    /// <summary>One sweep across all enabled markets. Returns the number of jobs processed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var processed = 0;
        foreach (var market in markets.Enabled)
        {
            try
            {
                processed += await ProcessMarketAsync(market, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One market's store being unavailable must not stop the other five.
                logger.LogError(ex, "Verification sweep failed for market {Market}", market.Code);
            }
        }
        return processed;
    }

    private async Task<int> ProcessMarketAsync(MarketDefinition market, CancellationToken ct)
    {
        var now = Now();
        List<Guid> candidates;
        await using (var db = factory.Create(market.Code))
        {
            candidates = await db.VerificationJobs.AsNoTracking()
                .Where(j => j.CompletedAtUtc == null && j.NextAttemptAtUtc <= now && (j.LeaseUntilUtc == null || j.LeaseUntilUtc < now))
                .OrderBy(j => j.NextAttemptAtUtc)
                .Select(j => j.ApplicationId)
                .Take(options.Value.BatchSize)
                .ToListAsync(ct);
        }

        var processed = 0;
        foreach (var applicationId in candidates)
        {
            if (!await TryClaimAsync(market.Code, applicationId, ct)) continue; // another replica got it
            await ProcessJobAsync(market, applicationId, ct);
            processed++;
        }
        return processed;
    }

    private async Task<bool> TryClaimAsync(string market, Guid applicationId, CancellationToken ct)
    {
        var now = Now();
        var leaseUntil = now.AddSeconds(options.Value.LeaseSeconds);
        await using var db = factory.Create(market);
        var claimed = await db.VerificationJobs
            .Where(j => j.ApplicationId == applicationId && j.CompletedAtUtc == null && (j.LeaseUntilUtc == null || j.LeaseUntilUtc < now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.LeaseOwner, InstanceId)
                .SetProperty(j => j.LeaseUntilUtc, (DateTime?)leaseUntil), ct);
        return claimed == 1;
    }

    private async Task ProcessJobAsync(MarketDefinition market, Guid applicationId, CancellationToken ct)
    {
        await using var db = factory.Create(market.Code);
        var job = await db.VerificationJobs.SingleAsync(j => j.ApplicationId == applicationId, ct);
        if (job.LeaseOwner != InstanceId) return;

        var application = await db.Applications.SingleAsync(a => a.Id == applicationId, ct);
        var reference = application.Reference.ToString();

        if (application.Status != ApplicationStatus.Submitted)
        {
            // Already decided (e.g. a previous attempt saved the outcome but crashed before completing the job).
            Complete(job);
            await db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            var documents = await db.Documents.AsNoTracking().Where(d => d.ApplicationId == applicationId).ToListAsync(ct);
            var identityDocument = documents.FirstOrDefault(d => d.Type == DocumentType.Passport)
                                   ?? documents.First(d => d.Type == DocumentType.IdCard);
            var selfie = documents.First(d => d.Type == DocumentType.Selfie);

            // Record the access before the data leaves for the provider, in its own save, so it is
            // logged even if the call or the rest of this job fails.
            db.Audit(Self, AuditActions.DocumentRead, applicationId, Now(), "sent to document verification provider");
            await db.SaveChangesAsync(ct);

            var documentResult = await documentProvider.VerifyAsync(new DocumentVerificationRequest(
                reference, market.Code, identityDocument.Type, identityDocument.Content, selfie.Content,
                application.FirstName, application.LastName, application.DateOfBirth), ct);

            ScreeningResult? screening = null;
            if (documentResult.Outcome == DocumentCheckOutcome.Verified)
            {
                screening = await screeningProvider.ScreenAsync(new ScreeningRequest(
                    reference, market.Code, application.FirstName, application.LastName, application.DateOfBirth), ct);
            }

            application.ApplyVerificationOutcome(documentResult.Outcome, screening?.Outcome, screening?.CaseReference, market.Activation, Now());
            job.Attempts++;
            Complete(job);
            db.Audit(Self, AuditActions.VerificationCompleted, applicationId, Now(),
                $"document={documentResult.Outcome}; screening={screening?.Outcome.ToString() ?? "not run"}; status={application.Status}");

            await db.SaveChangesAsync(ct);

            logger.LogInformation("Application {ApplicationRef} verified in {Attempts} attempt(s): {Status}",
                reference, job.Attempts, application.Status);
        }
        catch (ProviderUnavailableException ex)
        {
            await ScheduleRetryAsync(db, job, reference, ex);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Our lease expired and another replica took the job over; it will record the outcome.
            logger.LogWarning("Application {ApplicationRef}: lease lost to another worker, discarding result", reference);
        }
    }

    private async Task ScheduleRetryAsync(AtlasDbContext db, VerificationJob job, string reference, ProviderUnavailableException ex)
    {
        job.Attempts++;
        var delay = Backoff(job.Attempts);
        job.NextAttemptAtUtc = Now().Add(delay);
        job.LeaseOwner = null;
        job.LeaseUntilUtc = null;
        job.LastError = Truncate(ex.Message, 1000);
        db.Audit(Self, AuditActions.VerificationRetryScheduled, job.ApplicationId, Now(), $"attempt {job.Attempts}: {job.LastError}");

        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("Application {ApplicationRef}: lease lost to another worker while scheduling retry", reference);
            return;
        }

        var level = job.Attempts >= options.Value.AlertAfterAttempts ? LogLevel.Error : LogLevel.Warning;
        logger.Log(level, "Application {ApplicationRef}: provider unavailable ({Error}); attempt {Attempts}, retrying in {Delay}",
            reference, ex.Message, job.Attempts, delay);
    }

    private void Complete(VerificationJob job)
    {
        job.CompletedAtUtc = Now();
        job.LeaseOwner = null;
        job.LeaseUntilUtc = null;
        job.LastError = null;
    }

    private TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(attempts, 20)), options.Value.MaxBackoffSeconds));

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}
