# Open questions

Grouped by who I would ask. Where I had to proceed anyway, the assumption I made is in brackets.

## Compliance

1. **Erasure vs retention (GC §4 vs §5).** Is it correct that AML retention takes precedence, so an
   erasure request on an onboarding record is deferred until ten years after the application? Is there
   any data in the record you want erased immediately on request (e.g. contact details not needed for
   AML purposes, drafts never submitted)?
   *[Assumed: retention wins; request recorded and answered as deferred.]*
2. **Third-party processing and residency (GC §1).** IDNow and World-Check receive the passport image,
   selfie and personal details. Where do they process them, and does sending them there count as data
   "leaving the country"? This may require per-market provider endpoints or contracts.
3. **Forged documents.** If the document check fails, should the application still be screened and/or
   reported, or just rejected? *[Assumed: rejected, not screened.]*
4. **Check-digit algorithms.** Annex B gives formats for MA/MB/ME but not the check-digit algorithms. Are
   MA/MB JMBG? The ticket's example `0403991450016` fails the JMBG check, so either it's a made-up
   example or the algorithm is different. *[Assumed: format-only validation.]*
5. **MF Personal Number format** isn't in Annex B. *[Assumed: 13 digits.]*
6. **The written confirmation** your notice asks for "before development begins". Has it been given? I'd
   want it to cover the decisions in DECISIONS.md, not just the concept.

## Product

7. **"Fully automated" and "under 3 minutes".** Does Marketing know these can't hold for referrals
   (up to 48 h), MD (branch visit) or provider outages? What does the customer see in the app during a
   referral, and who contacts them?
8. **MD launch.** Since branch activation is just configuration, can MD launch on day one with "approved,
   please visit a branch to sign", rather than waiting for phase two?
9. **Account opening and card ordering**, step 5 of the ticket: which systems, and what happens if
   card ordering fails after the account is open?
10. **Re-applications.** Can a rejected applicant apply again straight away? Should we detect an open
    application for the same identifier? *[Assumed: allowed; identifier not unique.]*

## Mobile

11. The ticket's single POST still works, with three new statuses (`IN_PROGRESS`, `PENDING_REVIEW`,
    `PENDING_BRANCH_VISIT`) and a save-and-resume route that uploads images one at a time. Is that
    acceptable? If "no polling" is firm, can we send a **push notification** when an application leaves
    `PENDING_REVIEW`?
12. Is a 20-second hold on the POST OK for your HTTP client timeouts?

## Platform

13. **One region, one SQL instance** (18 Jul) conflicts with GC §1. Can we have a database (and blob
    storage) per market in-country, even if compute stays shared? If compute must be in-country too,
    each market becomes its own deployment of the same services.
14. **Per-service identities.** GC §2 rules out shared credentials for anything that reads customer
    records. Can each service get its own managed identity / database login?
15. **Logs** go to a central sink. I've kept personal data out of them; is that sufficient, or does
    Compliance want logs in-country too?

## Backend (Sam)

16. You asked to be pinged before the data model is fixed. The things I'd want you to check: identifiers
    as `{type, value}` and not a key; one database per market with market-prefixed ids; the access log in
    the same store; the outbox table as the job queue.
