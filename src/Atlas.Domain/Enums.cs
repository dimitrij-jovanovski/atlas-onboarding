namespace Atlas.Domain;

public enum ApplicationStatus
{
    /// <summary>Created; documents may still be uploaded. This is what makes save-and-resume possible.</summary>
    Draft,

    /// <summary>Submitted and waiting for (or undergoing) automated verification.</summary>
    Submitted,

    /// <summary>Screening returned a possible sanctions/PEP match. Only a compliance officer can move it on (GC §3).</summary>
    ReferredForReview,

    /// <summary>Verified, but the market requires an in-branch wet signature before activation (Annex B, MD).</summary>
    AwaitingBranchActivation,

    /// <summary>Account may be opened.</summary>
    Approved,

    Rejected,
}

public enum DocumentType
{
    Passport,
    IdCard,
    Selfie,
}

public enum DocumentCheckOutcome
{
    Verified,
    Failed,
}

public enum ScreeningOutcome
{
    Clear,
    PossibleMatch,
}

public enum ReviewDecision
{
    Approve,
    Reject,
}
