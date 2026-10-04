using Atlas.Domain;

namespace Atlas.Tests.Domain;

/// <summary>
/// The rules most likely to be broken by a well-meaning change later — especially "just auto-reject
/// the possible matches" (19 Aug) or "MD can be phase two" (22 Jul).
/// </summary>
public class ApplicationLifecycleTests
{
    private static readonly DateTime Now = TestData.Now;

    [Fact]
    public void Clean_result_in_a_remote_market_approves()
    {
        var app = TestData.Submitted("MB");
        app.ApplyVerificationOutcome(DocumentCheckOutcome.Verified, ScreeningOutcome.Clear, "WC-1", ActivationMode.Remote, Now);
        Assert.Equal(ApplicationStatus.Approved, app.Status);
    }

    [Fact]
    public void Possible_match_is_referred_never_rejected_or_approved_automatically()
    {
        var app = TestData.Submitted("MB");
        app.ApplyVerificationOutcome(DocumentCheckOutcome.Verified, ScreeningOutcome.PossibleMatch, "WC-1", ActivationMode.Remote, Now);

        Assert.Equal(ApplicationStatus.ReferredForReview, app.Status);
        Assert.Null(app.DecidedAtUtc);
        // A second automated result (e.g. a retried job) cannot overwrite the referral.
        Assert.Throws<DomainRuleViolation>(() =>
            app.ApplyVerificationOutcome(DocumentCheckOutcome.Verified, ScreeningOutcome.Clear, "WC-2", ActivationMode.Remote, Now));
        Assert.Equal(ApplicationStatus.ReferredForReview, app.Status);
    }

    [Fact]
    public void Referred_application_can_only_be_decided_by_an_officer_in_its_own_market()
    {
        var app = Referred("MB");

        var wrongMarket = Assert.Throws<DomainRuleViolation>(() =>
            app.RecordReviewDecision("officer.ma", "MA", ReviewDecision.Approve, "looks fine", ActivationMode.Remote, Now));
        Assert.Equal("wrong_market", wrongMarket.Code);

        var anonymous = Assert.Throws<DomainRuleViolation>(() =>
            app.RecordReviewDecision("", "MB", ReviewDecision.Approve, "looks fine", ActivationMode.Remote, Now));
        Assert.Equal("officer_required", anonymous.Code);

        var noRationale = Assert.Throws<DomainRuleViolation>(() =>
            app.RecordReviewDecision("officer.mb", "MB", ReviewDecision.Approve, " ", ActivationMode.Remote, Now));
        Assert.Equal("notes_required", noRationale.Code);

        app.RecordReviewDecision("officer.mb", "MB", ReviewDecision.Approve, "False positive: different date of birth", ActivationMode.Remote, Now);
        Assert.Equal(ApplicationStatus.Approved, app.Status);
        Assert.Equal("officer.mb", app.ReviewedBy);
    }

    [Fact]
    public void Officer_rejection_is_recorded()
    {
        var app = Referred("MB");
        app.RecordReviewDecision("officer.mb", "MB", ReviewDecision.Reject, "Confirmed match", ActivationMode.Remote, Now);
        Assert.Equal(ApplicationStatus.Rejected, app.Status);
        Assert.Equal("compliance_review", app.RejectionReason);
    }

    [Fact]
    public void Failed_document_check_rejects_without_screening()
    {
        var app = TestData.Submitted("MB");
        app.ApplyVerificationOutcome(DocumentCheckOutcome.Failed, null, null, ActivationMode.Remote, Now);
        Assert.Equal(ApplicationStatus.Rejected, app.Status);
        Assert.Null(app.Screening);
    }

    [Fact]
    public void Branch_market_never_activates_remotely()
    {
        var app = TestData.Submitted("MD");
        app.ApplyVerificationOutcome(DocumentCheckOutcome.Verified, ScreeningOutcome.Clear, "WC-1", ActivationMode.Branch, Now);
        Assert.Equal(ApplicationStatus.AwaitingBranchActivation, app.Status);

        Assert.Equal("signature_required", Assert.Throws<DomainRuleViolation>(() =>
            app.ActivateAtBranch("branch.md", "MD", wetSignatureCaptured: false, Now)).Code);
        Assert.Equal("wrong_market", Assert.Throws<DomainRuleViolation>(() =>
            app.ActivateAtBranch("branch.mb", "MB", wetSignatureCaptured: true, Now)).Code);

        app.ActivateAtBranch("branch.md", "MD", wetSignatureCaptured: true, Now);
        Assert.Equal(ApplicationStatus.Approved, app.Status);
    }

    [Fact]
    public void Officer_approval_in_a_branch_market_still_requires_the_branch_visit()
    {
        var app = Referred("MD", ActivationMode.Branch);
        app.RecordReviewDecision("officer.md", "MD", ReviewDecision.Approve, "False positive", ActivationMode.Branch, Now);
        Assert.Equal(ApplicationStatus.AwaitingBranchActivation, app.Status);
    }

    [Fact]
    public void Submit_requires_terms_identity_document_and_selfie()
    {
        OnboardingApplication Draft() => OnboardingApplication.Create(Markets.Catalog.Get("MB"), TestData.Applicant(), Now);

        Assert.Equal("terms_not_accepted", Assert.Throws<DomainRuleViolation>(() =>
            Draft().Submit(false, [DocumentType.Passport, DocumentType.Selfie], Now)).Code);
        Assert.Equal("documents_missing", Assert.Throws<DomainRuleViolation>(() =>
            Draft().Submit(true, [DocumentType.Passport], Now)).Code);
        Assert.Equal("documents_missing", Assert.Throws<DomainRuleViolation>(() =>
            Draft().Submit(true, [DocumentType.Selfie], Now)).Code);

        var ok = Draft();
        Assert.True(ok.Submit(true, [DocumentType.IdCard, DocumentType.Selfie], Now));
        Assert.False(ok.Submit(true, [DocumentType.IdCard, DocumentType.Selfie], Now)); // idempotent
        Assert.Equal(ApplicationStatus.Submitted, ok.Status);
    }

    [Fact]
    public void Records_are_retained_for_ten_years_and_erasure_is_deferred()
    {
        var app = TestData.Submitted();
        app.RequestErasure(Now);
        Assert.Equal(Now.AddYears(10), app.RetainUntilUtc);
        Assert.Equal(Now, app.ErasureRequestedAtUtc);
        Assert.Equal("Petrovska", app.LastName); // still there: AML retention takes precedence until RetainUntilUtc
    }

    [Theory]
    [InlineData("MB-3f2a1b4c5d6e47f8a9b0c1d2e3f4a5b6", true)]
    [InlineData("mb-3f2a1b4c5d6e47f8a9b0c1d2e3f4a5b6", true)]
    [InlineData("MB3f2a1b4c5d6e47f8a9b0c1d2e3f4a5b6", false)]
    [InlineData("0403991450016", false)]
    [InlineData("", false)]
    public void Application_reference_carries_the_market(string value, bool valid)
    {
        Assert.Equal(valid, ApplicationRef.TryParse(value, out var reference));
        if (valid) Assert.Equal("MB", reference.Market);
    }

    private static OnboardingApplication Referred(string market, ActivationMode activation = ActivationMode.Remote)
    {
        var app = TestData.Submitted(market);
        app.ApplyVerificationOutcome(DocumentCheckOutcome.Verified, ScreeningOutcome.PossibleMatch, "WC-1", activation, Now);
        return app;
    }
}
