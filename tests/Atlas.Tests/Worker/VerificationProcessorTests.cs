using Atlas.Domain;
using Atlas.Infrastructure.Persistence;
using Atlas.Verification.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Atlas.Tests.Worker;

/// <summary>
/// The awkward parts of the flow: provider outages, retries, and several replicas racing for the same job.
/// Runs against real (SQLite) databases, one per market, with scripted providers.
/// </summary>
public sealed class VerificationProcessorTests : IDisposable
{
    private readonly TestDatabases _databases = new();
    private readonly IMarketDbContextFactory _factory;
    private readonly ManualClock _clock = new(TestData.Now);
    private readonly ScriptedDocumentProvider _documents = new();
    private readonly ScriptedScreeningProvider _screening = new();

    public VerificationProcessorTests() => _factory = _databases.CreateFactory();

    public void Dispose() => _databases.Dispose();

    private VerificationProcessor NewProcessor() => new(
        _factory, Markets.Catalog, _documents, _screening, _clock,
        Options.Create(new WorkerOptions()), NullLogger<VerificationProcessor>.Instance);

    [Fact]
    public async Task Clean_application_is_approved()
    {
        var id = await Seed("MB");
        await NewProcessor().RunOnceAsync(default);

        Assert.Equal(ApplicationStatus.Approved, await StatusOf("MB", id));
        Assert.NotNull((await JobOf("MB", id)).CompletedAtUtc);
    }

    [Fact]
    public async Task Possible_match_is_referred_for_manual_review()
    {
        var id = await Seed("MB");
        _screening.Next = ScreeningOutcome.PossibleMatch;

        await NewProcessor().RunOnceAsync(default);

        Assert.Equal(ApplicationStatus.ReferredForReview, await StatusOf("MB", id));
    }

    [Fact]
    public async Task Branch_market_waits_for_branch_activation()
    {
        var id = await Seed("MD");
        await NewProcessor().RunOnceAsync(default);
        Assert.Equal(ApplicationStatus.AwaitingBranchActivation, await StatusOf("MD", id));
    }

    [Fact]
    public async Task Provider_outage_schedules_a_retry_and_never_rejects()
    {
        var id = await Seed("MB");
        _screening.FailuresRemaining = 2;
        var processor = NewProcessor();

        await processor.RunOnceAsync(default);
        Assert.Equal(ApplicationStatus.Submitted, await StatusOf("MB", id));
        var job = await JobOf("MB", id);
        Assert.Equal(1, job.Attempts);
        Assert.Null(job.CompletedAtUtc);
        Assert.Null(job.LeaseOwner);
        Assert.True(job.NextAttemptAtUtc > _clock.GetUtcNow().UtcDateTime);

        // Not due yet: nothing happens.
        Assert.Equal(0, await processor.RunOnceAsync(default));

        _clock.Advance(TimeSpan.FromSeconds(3));
        await processor.RunOnceAsync(default); // second failure, longer backoff
        Assert.Equal(2, (await JobOf("MB", id)).Attempts);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await processor.RunOnceAsync(default);
        Assert.Equal(ApplicationStatus.Approved, await StatusOf("MB", id));
        Assert.Equal(3, (await JobOf("MB", id)).Attempts);
    }

    [Fact]
    public async Task Competing_replicas_process_each_job_exactly_once()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add(await Seed("MB"));

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => NewProcessor().RunOnceAsync(default))));

        Assert.Equal(5, _documents.Calls);
        Assert.Equal(5, _screening.Calls);
        foreach (var id in ids) Assert.Equal(ApplicationStatus.Approved, await StatusOf("MB", id));
    }

    [Fact]
    public async Task Each_access_to_documents_is_logged_against_the_service_identity()
    {
        var id = await Seed("MB");
        await NewProcessor().RunOnceAsync(default);

        await using var db = _factory.Create("MB");
        var entries = await db.AuditEntries.Where(e => e.ApplicationId == id).ToListAsync();
        Assert.Contains(entries, e => e.Action == "document.read" && e.ActorId == VerificationProcessor.ServiceIdentity);
        Assert.Contains(entries, e => e.Action == "verification.completed");
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Guid> Seed(string market)
    {
        var application = TestData.Submitted(market);
        await using var db = _factory.Create(market);
        db.Applications.Add(application);
        foreach (var type in new[] { DocumentType.Passport, DocumentType.Selfie })
            db.Documents.Add(new StoredDocument
            {
                Id = Guid.NewGuid(), ApplicationId = application.Id, Type = type, ContentType = "image/jpeg",
                Content = TestData.Jpeg, Sha256 = "x", UploadedAtUtc = TestData.Now,
            });
        db.VerificationJobs.Add(VerificationJob.For(application, TestData.Now));
        await db.SaveChangesAsync();
        return application.Id;
    }

    private async Task<ApplicationStatus> StatusOf(string market, Guid id)
    {
        await using var db = _factory.Create(market);
        return await db.Applications.Where(a => a.Id == id).Select(a => a.Status).SingleAsync();
    }

    private async Task<VerificationJob> JobOf(string market, Guid id)
    {
        await using var db = _factory.Create(market);
        return await db.VerificationJobs.AsNoTracking().SingleAsync(j => j.ApplicationId == id);
    }

    private sealed class ScriptedDocumentProvider : IDocumentVerificationProvider
    {
        private int _calls;
        public int Calls => _calls;

        public Task<DocumentVerificationResult> VerifyAsync(DocumentVerificationRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new DocumentVerificationResult(DocumentCheckOutcome.Verified, null));
        }
    }

    private sealed class ScriptedScreeningProvider : IScreeningProvider
    {
        private int _calls;
        public int Calls => _calls;
        public ScreeningOutcome Next { get; set; } = ScreeningOutcome.Clear;
        public int FailuresRemaining { get; set; }

        public Task<ScreeningResult> ScreenAsync(ScreeningRequest request, CancellationToken ct)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new ProviderUnavailableException("worldcheck", "HTTP 503");
            }
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ScreeningResult(Next, "WC-test"));
        }
    }
}
