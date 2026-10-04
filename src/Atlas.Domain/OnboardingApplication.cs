namespace Atlas.Domain;

/// <summary>
/// An onboarding application and the rules for moving it between states.
///
/// Every status change goes through a method on this class, so the compliance rules are enforced in
/// one place regardless of which service (mobile API, verification worker, back office) asks for it.
/// The most important one: an application with a possible sanctions/PEP match can only leave
/// <see cref="ApplicationStatus.ReferredForReview"/> through <see cref="RecordReviewDecision"/>, which
/// requires a named compliance officer from the application's own market (GC-2026-0814 §3).
/// </summary>
public sealed class OnboardingApplication
{
    /// <summary>AML retention period for onboarding records, including rejected ones (GC §4).</summary>
    public static readonly int RetentionYears = 10;

    public Guid Id { get; private set; }
    public string Market { get; private set; } = default!;
    public ApplicationStatus Status { get; private set; }

    public string FirstName { get; private set; } = default!;
    public string LastName { get; private set; } = default!;
    public DateOnly DateOfBirth { get; private set; }
    public string Email { get; private set; } = default!;
    public string Phone { get; private set; } = default!;
    public string IdentifierType { get; private set; } = default!;
    public string IdentifierValue { get; private set; } = default!;

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public DateTime? SubmittedAtUtc { get; private set; }
    public DateTime? TermsAcceptedAtUtc { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public DateTime RetainUntilUtc { get; private set; }

    public DocumentCheckOutcome? DocumentCheck { get; private set; }
    public ScreeningOutcome? Screening { get; private set; }
    public string? ScreeningCaseReference { get; private set; }
    public string? RejectionReason { get; private set; }

    public string? ReviewedBy { get; private set; }
    public DateTime? ReviewedAtUtc { get; private set; }
    public string? ReviewNotes { get; private set; }

    public string? ActivatedBy { get; private set; }
    public DateTime? ActivatedAtUtc { get; private set; }

    public DateTime? ErasureRequestedAtUtc { get; private set; }

    /// <summary>Client-supplied key that makes create safe to retry (the "train goes into a tunnel" case).</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>Hash of the identifying fields of the create request, to detect a reused key with a different body.</summary>
    public string? RequestFingerprint { get; private set; }

    public ApplicationRef Reference => new(Market, Id);

    private OnboardingApplication() { } // EF Core

    public static OnboardingApplication Create(
        MarketDefinition market,
        ApplicantDetails applicant,
        DateTime nowUtc,
        string? idempotencyKey = null,
        string? requestFingerprint = null)
    {
        var errors = ApplicantValidator.Validate(applicant, market, DateOnly.FromDateTime(nowUtc));
        if (errors.Count > 0)
            throw new DomainRuleViolation("invalid_applicant", string.Join(" ", errors.Select(e => e.Message)));

        return new OnboardingApplication
        {
            Id = Guid.NewGuid(),
            Market = market.Code,
            Status = ApplicationStatus.Draft,
            FirstName = applicant.FirstName.Trim(),
            LastName = applicant.LastName.Trim(),
            DateOfBirth = applicant.DateOfBirth,
            Email = applicant.Email.Trim(),
            Phone = applicant.Phone.Trim(),
            IdentifierType = market.FindIdentifier(applicant.IdentifierType)!.Type,
            IdentifierValue = IdentifierFormats.Normalise(applicant.IdentifierValue),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            RetainUntilUtc = nowUtc.AddYears(RetentionYears),
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = requestFingerprint,
        };
    }

    public bool AcceptsDocuments => Status == ApplicationStatus.Draft;

    public void EnsureAcceptsDocuments()
    {
        if (!AcceptsDocuments)
            throw new DomainRuleViolation("not_draft", "Documents can only be changed before the application is submitted.");
    }

    /// <summary>
    /// Submits the application for verification. Idempotent: submitting an already-submitted application
    /// is a no-op so a client retrying after a dropped connection gets the current state, not an error.
    /// </summary>
    /// <returns>true if this call changed the status.</returns>
    public bool Submit(bool termsAccepted, IReadOnlyCollection<DocumentType> uploadedDocuments, DateTime nowUtc)
    {
        if (Status != ApplicationStatus.Draft) return false;

        if (!termsAccepted)
            throw new DomainRuleViolation("terms_not_accepted", "Terms must be accepted to submit.");

        var hasIdentityDocument = uploadedDocuments.Contains(DocumentType.Passport) || uploadedDocuments.Contains(DocumentType.IdCard);
        if (!hasIdentityDocument || !uploadedDocuments.Contains(DocumentType.Selfie))
            throw new DomainRuleViolation("documents_missing", "An identity document (passport or ID card) and a selfie are required.");

        Status = ApplicationStatus.Submitted;
        TermsAcceptedAtUtc = nowUtc;
        SubmittedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    /// <summary>Records the automated checks. Called by the verification worker only.</summary>
    public void ApplyVerificationOutcome(
        DocumentCheckOutcome documentCheck,
        ScreeningOutcome? screening,
        string? screeningCaseReference,
        ActivationMode activation,
        DateTime nowUtc)
    {
        if (Status != ApplicationStatus.Submitted)
            throw new DomainRuleViolation("invalid_state", $"Verification results can only be applied to a submitted application (status is {Status}).");

        DocumentCheck = documentCheck;
        UpdatedAtUtc = nowUtc;

        if (documentCheck == DocumentCheckOutcome.Failed)
        {
            // Screening is not run against an identity we could not verify — see DECISIONS.md.
            Reject("document_verification_failed", nowUtc);
            return;
        }

        if (screening is null)
            throw new DomainRuleViolation("screening_required", "A verified document must be followed by a screening result.");

        Screening = screening;
        ScreeningCaseReference = screeningCaseReference;

        if (screening == ScreeningOutcome.PossibleMatch)
        {
            // GC §3: never auto-reject, never auto-approve. A human in the market decides.
            Status = ApplicationStatus.ReferredForReview;
            return;
        }

        Approve(activation, nowUtc);
    }

    /// <summary>A compliance officer's decision on a referred application (GC §3).</summary>
    public void RecordReviewDecision(
        string officerId,
        string officerMarket,
        ReviewDecision decision,
        string notes,
        ActivationMode activation,
        DateTime nowUtc)
    {
        if (Status != ApplicationStatus.ReferredForReview)
            throw new DomainRuleViolation("invalid_state", $"Only referred applications can be reviewed (status is {Status}).");
        if (string.IsNullOrWhiteSpace(officerId))
            throw new DomainRuleViolation("officer_required", "A review decision must be attributable to a named officer.");
        if (!string.Equals(officerMarket, Market, StringComparison.OrdinalIgnoreCase))
            throw new DomainRuleViolation("wrong_market", "Referred applications must be reviewed by a compliance officer in the application's market.");
        if (string.IsNullOrWhiteSpace(notes))
            throw new DomainRuleViolation("notes_required", "A review decision must record the officer's rationale.");

        ReviewedBy = officerId;
        ReviewedAtUtc = nowUtc;
        ReviewNotes = notes.Trim();
        UpdatedAtUtc = nowUtc;

        if (decision == ReviewDecision.Approve)
            Approve(activation, nowUtc);
        else
            Reject("compliance_review", nowUtc);
    }

    /// <summary>In-branch activation after a wet signature (Annex B, MD).</summary>
    public void ActivateAtBranch(string staffId, string staffMarket, bool wetSignatureCaptured, DateTime nowUtc)
    {
        if (Status != ApplicationStatus.AwaitingBranchActivation)
            throw new DomainRuleViolation("invalid_state", $"Only applications awaiting branch activation can be activated (status is {Status}).");
        if (string.IsNullOrWhiteSpace(staffId))
            throw new DomainRuleViolation("staff_required", "Activation must be attributable to a named member of staff.");
        if (!string.Equals(staffMarket, Market, StringComparison.OrdinalIgnoreCase))
            throw new DomainRuleViolation("wrong_market", "Activation must be performed by staff in the application's market.");
        if (!wetSignatureCaptured)
            throw new DomainRuleViolation("signature_required", "A wet signature must be captured before activation.");

        ActivatedBy = staffId;
        ActivatedAtUtc = nowUtc;
        Status = ApplicationStatus.Approved;
        DecidedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// Records a right-to-erasure request. GC §4 (ten-year AML retention) takes precedence over §5 for the
    /// onboarding record, so the data is kept until <see cref="RetainUntilUtc"/> and erased then.
    /// See DECISIONS.md and OPEN-QUESTIONS.md — this is the interpretation we need Compliance to confirm.
    /// </summary>
    public void RequestErasure(DateTime nowUtc)
    {
        ErasureRequestedAtUtc ??= nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    private void Approve(ActivationMode activation, DateTime nowUtc)
    {
        Status = activation == ActivationMode.Branch
            ? ApplicationStatus.AwaitingBranchActivation
            : ApplicationStatus.Approved;
        if (Status == ApplicationStatus.Approved) DecidedAtUtc = nowUtc;
    }

    private void Reject(string reason, DateTime nowUtc)
    {
        Status = ApplicationStatus.Rejected;
        RejectionReason = reason;
        DecidedAtUtc = nowUtc;
    }
}
