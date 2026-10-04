# Where to look

**Read these first**

1. [`src/Atlas.Domain/OnboardingApplication.cs`](src/Atlas.Domain/OnboardingApplication.cs). Every status
   change and every compliance rule (referral, officer from the same market, branch activation,
   retention) in one class.
2. [`src/Atlas.Verification.Worker/VerificationProcessor.cs`](src/Atlas.Verification.Worker/VerificationProcessor.cs).
   The awkward part: leases, retries during an outage, three replicas, access logging before data leaves.
3. [`src/Atlas.Onboarding.Api/ApplicationEndpoints.cs`](src/Atlas.Onboarding.Api/ApplicationEndpoints.cs).
   How the ticket's one-call POST is kept working alongside save-and-resume and idempotent retries.
4. [`src/Atlas.Infrastructure/markets.json`](src/Atlas.Infrastructure/markets.json). Annex B as configuration.

**The decision I most want to be asked about:** keeping the single POST but making it honest. It holds
the connection briefly and returns `APPROVED`/`REJECTED` when it can, and `PENDING_REVIEW` /
`PENDING_BRANCH_VISIT` when the regulation says a human or a branch has to be involved. That's instead of
building the ticket's "fully automated, one answer" as written (DECISIONS.md §1–2).

**Please ignore:** the staff header authentication (`DevStaffAuthenticationHandler`), which is a local
stand-in for the bank's identity provider, and `Atlas.FakeProviders`, which only exists so every path
can be exercised.
