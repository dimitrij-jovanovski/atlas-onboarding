# Decisions

The ticket, the channel and the compliance notice disagree with each other in several places. My
starting rule: **the compliance notice is binding and the ticket is a product brief.** The notice
says "the following are requirements, not recommendations", is backed by local regulators (Annex B),
and asks for written confirmation before development begins. Where the ticket conflicts with it,
I built what the notice requires and kept as much of the ticket's intent as I could alongside it.

Each section: what the material says, what I did, what I rejected.

---

## 1. Possible sanctions matches go to a human, so "fully automated" is not possible

**Material.** The ticket puts human review out of scope; on 2 Sep Product says "fully automated". On
19 Aug Product suggests auto-rejecting possible matches. GC §3: a possible match *must* be referred to a
compliance officer in the market, may take up to 48 h, and "under no circumstances may this review be
automated or bypassed". Teo also points out that auto-rejecting defeats the purpose.

**Did.** `ReferredForReview` is a real status. The only way out of it is
`OnboardingApplication.RecordReviewDecision`, which needs a named officer, from the application's own
market, with a written rationale. The worker cannot overwrite a referral even if a job is retried.
I built the officer-facing side (queue, detail, documents, decision, access log) because without it a
referral would be a dead end, and the 48 h clock would run against an empty inbox.

**Rejected.** Auto-reject: it breaks GC §3, and refusing someone *because* a screening list flagged them,
without review, also turns every false positive (common names) into a lost customer. Auto-approve after
a timeout: same problem, worse.

The rest of the flow *is* automated. A clean applicant never meets a human, which I think is what
"fully automated" meant to Product.

## 2. The API: keep one call where it can work, add what it cannot express

**Material.** Mobile wants one POST that returns `APPROVED | REJECTED`, no polling, no websocket, and
"don't redesign it without talking to them". But: referrals take up to 48 h, MD needs a branch visit,
provider retries take time, the channel wants save-and-resume ("table stakes"), and the only way to
resume today would be to keep passport scans and selfies unencrypted on the phone for days (Ivana,
6 Aug). Sam points out the images are several MB each in base64.

**Did.**
* `POST /applications` with the ticket's body still works as one call. It creates, stores the
  documents, submits, and **holds the connection up to 20 s** while the worker decides. For a clean
  applicant with healthy providers the response *is* `APPROVED`/`REJECTED`, which is the ticket's
  contract for the common case.
* When a decision can't come in 20 s, the response says so honestly: `IN_PROGRESS`, `PENDING_REVIEW`
  or `PENDING_BRANCH_VISIT`, instead of inventing an answer.
* Save-and-resume is server-side: the same POST without `termsAccepted` creates a `DRAFT`, documents
  are uploaded one at a time as raw bytes (`PUT /applications/{id}/documents/{type}`), then
  `POST …/submit`. The phone only needs to keep the application id and token, not the images.
* `GET /applications/{id}?waitSeconds=30` lets the app check back with one long-poll call when the
  customer reopens it.

The extra statuses and endpoints are additions. A client that only knows the ticket's body still works.
This is what I would take to the mobile team, rather than something I would ship without them.

**Rejected.** Returning `REJECTED` for anything not decided in time (lies to the customer and breaks GC
§3). A fully asynchronous 202-only API (correct but throws away the one-call experience Mobile
asked for, when most customers can have it). Websockets/SSE (Mobile said no).

## 3. Data residency: one codebase, one data store per market

**Material.** GC §1: personal data, documents and biometrics must not leave the customer's country.
Teo: everything runs in West Europe, one SQL instance, one shared Service Bus, "don't overthink it".
Product: one codebase, market differences are configuration.

**Did.** "One codebase" and "one deployment" are different things; only the first was asked for. Every
market gets its own data store (`Database:Markets:{code}` or a `{market}` template).
`IMarketDbContextFactory.Create(market)` is the only way to reach data, and it opens exactly one market.
Application ids carry the market (`MB-…`), so any service can route a request to the right store
without a central lookup table. Such a table would itself be a pile of cross-border data.
Locally all six are databases on the one SQL Server from `/platform`. In production each would be in its
own country, with the same services deployed per market (a "cell" per market).

Logs go to one central Seq, so **logs contain no personal data**: application references, statuses,
counts only. The message broker would carry the same rule. Messages would hold ids, never payloads.

**Rejected.** A `Market` column in one shared database (that *is* the data leaving the country). Teo's
single shared estate as-is. It may well be fine for the services, but not for the data. This needs a
conversation with Platform, not a workaround in code (OPEN-QUESTIONS.md).

## 4. MD: branch activation is configuration, not phase two

**Material.** Annex B and Petra: in MD, remote identification is allowed for the application, but
activation needs an in-person wet signature. The exemption has been pending since 2024 and "no relief
should be assumed". Product: "MD can be phase two… still one codebase".

**Did.** `markets.json` has `"activation": "Branch"` for MD. After a clean result (or an officer's
approval) an MD application goes to `AwaitingBranchActivation`. Branch staff in MD confirm the wet
signature in the back office to activate it. If the exemption arrives, MD becomes `"Remote"` in config
with no code change. MD can launch with everyone else, honestly.

**Rejected.** Leaving MD out (unnecessary). Treating MD as remote and hoping (contradicts the regulator).

## 5. Identifiers: typed, validated per market, never a key

**Material.** The ticket has a required `nationalId` string. Sam: formats differ per market, and many
MF residents (mostly refugees) have no Personal Number and use a passport number. Annex B: formats are
for validation only and "must not be used as a primary key".

**Did.** An identifier is `{type, value}`. Each market lists the types it accepts and their format
(`markets.json`). MF accepts `PERSONAL_NUMBER` or `PASSPORT_NUMBER`. The ticket's flat `nationalId`
still works and maps to the market's default type. Primary keys are GUIDs. The identifier index is
deliberately *not* unique: the same person can apply again after a rejection.

I validate format only, not check digits. Annex B says ME's algorithm "differs from MA/MB" without
saying what either is. MA/MB look like the former-Yugoslav JMBG (DDMMYYY + region + serial + check), and
the ticket's own example `0403991450016` has the date of birth 04-03-1991 and region 45. **But it fails
the JMBG mod-11 check** (expected check digit 4, it has 6). So either the example is made up or the
algorithm isn't JMBG. Guessing wrong would reject real customers, so I left it as a question.
Annex B also doesn't give the MF Personal Number format; 13 digits is my assumption.

## 6. Service boundaries

Four processes, split by **who calls them and what they are trusted with**, not by entity:

| Service | Why it is separate |
|---|---|
| Onboarding API | Public, faces the internet, authenticated by per-application token. Scales with traffic. |
| Back office API | Internal only, staff identity, can read everything in a market. Must never share an exposure with the public API. |
| Verification worker | Talks to external providers, retries, slow. Must not tie up request threads, and must scale/fail independently. |
| Fake providers | Stand-in for third parties. Knows nothing about Atlas code, only the wire format. |

They share one domain library and one data store per market. This is one bounded context ("an onboarding
application"), and the rules that protect it live in one class. Splitting the application into separate
databases per service would have created distributed consistency problems in exactly the place where
compliance needs a single, auditable truth.

**No broker.** The job queue is a `VerificationJobs` table written in the same transaction as the
submit (a transactional outbox), and the worker claims rows with a conditional update. That gives
exactly the guarantee I need, "a submitted application always gets verified", with no dual write and
no extra infrastructure. Per market, it also keeps the queue inside the country. A broker (RabbitMQ /
Service Bus) becomes worth it when other services need to react to events (account opening, cards,
notifications). At that point I'd add a relay from the outbox, with ids only in messages.

**Images in the database.** In production they belong in blob storage in the market's region (Azurite
locally), with only the reference in SQL. I kept them in a separate `Documents` table so they're never
loaded with the application, and made the trade-off to save time. The `StoredDocument` type is the seam.

## 7. Retention (10 years) versus erasure on request

**Material.** GC §4: keep onboarding records, including rejected applications and documents, for ten
years. GC §5: delete personal data on request. Both are "requirements".

**Did.** I read §4 as the legal obligation that limits §5 for these records. That is how AML retention
normally interacts with the right to erasure (e.g. GDPR Art. 17(3)(b)). An erasure request is recorded
and logged, and the customer is told it is deferred until `RetainUntilUtc` (application date + 10 years).
Data with no AML purpose (e.g. a draft that was never submitted) could be erased immediately, but I
haven't built the erasure job. This is the most important interpretation for Compliance to confirm.
It's also where Sam's "things that look simple and aren't" warning most applies to the data model. The
access log is retained "for the same period as the underlying record" (GC §2), so it lives in the same
store and follows the same retention.

## 8. Access control and the access log (GC §2)

* Every read of personal data and every decision writes an `AuditLog` row: who (the named officer, the
  applicant, or the named service), what, when. It's saved in the **same transaction** as the action, so
  an action cannot succeed unlogged. Reads that return only a status are not logged; otherwise every
  poll would add a row.
* Staff are scoped by a market claim. An officer from MA gets 403 on an MB application.
* The review queue lists references and dates, not names. Opening an application is the logged access.
* The customer-facing status never says *why* something is pending or rejected. Telling someone they
  matched a sanctions list is tipping-off.
* The applicant's token is an HMAC of the application id, so an idempotent replay can return it
  without the server storing it.

## 9. Retries, replicas and the tunnel

* **Client retries:** `Idempotency-Key` on create, backed by a unique index (two replicas receiving the
  same retry at the same moment can't both create). Reusing a key with a different body returns 422.
  Submit is naturally idempotent.
* **Provider retries:** a failed provider call reschedules the job with exponential backoff (2, 4, 8 s …
  capped at 5 min). It never rejects the customer. I deliberately did *not* retry inside the HTTP
  request ("build in retries"): that blocks a request thread and blows the 3-minute promise either way.
* **Three replicas:** a job is claimed with `UPDATE … WHERE lease is free`. The final save carries a
  concurrency check on the lease owner, so if a slow worker's lease expired and another took over, the
  slow one's result is discarded. A test runs three workers against the same jobs.

## 10. Smaller calls

* **Document check fails → reject without screening.** Screening an identity we couldn't verify gives
  a meaningless result. Worth checking with Compliance (a forged document can itself be a reportable
  event).
* **Anything from World-Check that isn't an unambiguous CLEAR is treated as a possible match**, i.e.
  fail safe toward a human.
* **Minimum age 18** in every market. It's an assumption, configurable per market.
* **A market must be explicitly enabled** in `markets.json` (Annex B note 3: a new market can't go live
  before the annex is reissued).

## 11. "Under 3 minutes"

Achievable and measurable for clean applicants. In practice it's a few seconds with fast providers, and
the one-call API returns the decision directly. It can't be promised for referrals (up to 48 h, by
regulation), for MD (a branch visit), or during a provider outage. Marketing should know this before
the campaign goes out. That's a product conversation, not a code change.
