# Atlas — digital onboarding backend

One working flow, end to end: a customer applies from the app, their documents and identity are
verified, they are screened, and they end up **approved**, **rejected**, **referred to a compliance
officer**, or **waiting for a branch visit** — in all six markets, from one codebase.

Start with [`WHERE-TO-LOOK.md`](WHERE-TO-LOOK.md). The reasoning is in [`DECISIONS.md`](DECISIONS.md),
the questions I would put to Product and Compliance are in [`OPEN-QUESTIONS.md`](OPEN-QUESTIONS.md).

---

## Run it

Requires the .NET 8 SDK. Services run on the host; infrastructure in Docker.

```bash
cd platform && docker compose up -d && cd ..     # SQL Server + Seq, unchanged from what you sent
./scripts/run.sh                                 # Windows: ./scripts/run.ps1
./scripts/demo.sh                                # second terminal (needs curl + jq). Windows: ./scripts/demo.ps1
dotnet test                                      # no Docker needed — tests use SQLite
```

`run` builds the solution and starts four processes; Ctrl+C stops them all. The Onboarding API creates
one database per market (`Atlas_MA` … `Atlas_MF`) on first start. Logs go to the console and to Seq at
http://localhost:5341.

**No Docker?** `./scripts/run.sh --sqlite` (or `run.ps1 -Sqlite`) runs the same thing against one SQLite
file per market in `.data/`.

No images beyond the two already in `/platform` are needed. I did not add RabbitMQ — see DECISIONS.md §6.

| Process | Port | Who calls it |
|---|---|---|
| `Atlas.Onboarding.Api` | 5100 | the mobile app |
| `Atlas.Backoffice.Api` | 5200 | compliance officers and branch staff (`X-Staff-Id: officer.mb`, `branch.md`, … — see its appsettings) |
| `Atlas.Verification.Worker` | — | nobody; polls for submitted applications |
| `Atlas.FakeProviders` | 5300 | the worker, in place of IDNow and World-Check |

The fake providers pick an outcome from the applicant's **last name**: `Match` → possible sanctions
match, `Forged` → document rejected, `Flaky` → each provider fails once with 503 first, anything else → clean.

---

## What I built

```
 mobile ──► Onboarding API ──(same transaction)──► market DB: application + documents + verification job + access log
                                                        ▲
             Verification worker ──claims job──────────┤──► IDNow (fake)  ──► World-Check (fake)
                                                        │
 officers ─► Back office API ──review / activate───────┘
```

* **Onboarding API** — the ticket's `POST /applications` still works as one call: it creates the
  application, stores the documents, submits, and holds the connection for up to 20 s while the worker
  decides. Most customers get `APPROVED` or `REJECTED` in that response, as Mobile asked. Around it:
  `PUT …/documents/{type}` and `POST …/submit` for save-and-resume, `GET …?waitSeconds=` for status,
  `POST …/erasure-request`. `Idempotency-Key` makes retries after a dropped connection safe.
* **Verification worker** — document check, then screening. Clean → approved (or awaiting branch in MD);
  possible match → referred to a human; failed document → rejected; provider down → retried with backoff,
  never rejected. Safe to run as three replicas.
* **Back office API** — per-market review queue, application detail and documents for the officer,
  approve/reject with a mandatory rationale, branch activation with wet-signature confirmation, and the
  access log for an application. Each staff member only ever sees their own market.
* **Per-market data stores** — every market has its own database; nothing in the code reads across markets.
* **Access log** — every read of personal data and every decision, with the named person or service,
  in the same transaction as the action.

### Status the customer sees

| Status | Meaning |
|---|---|
| `DRAFT` | created, documents still being added |
| `IN_PROGRESS` | submitted, automated checks running (or retrying) |
| `PENDING_REVIEW` | a compliance officer has to look at it (up to 48 h) — the reason is not shown to the customer |
| `PENDING_BRANCH_VISIT` | approved, but this market requires a wet signature in a branch |
| `APPROVED` / `REJECTED` | final |

### Tests

`dotnet test` — the rules I think are most likely to be broken later:

* **Lifecycle** (`ApplicationLifecycleTests`) — a possible match can't be auto-approved or auto-rejected,
  and only an officer from the same market can decide it; MD never activates without a wet signature.
* **Annex B identifiers** (`IdentifierValidationTests`) — every market's format; passport only in MF.
* **Worker** (`VerificationProcessorTests`) — retries and backoff during an outage; three competing
  workers process each job exactly once; document access is logged.
* **API** (`OnboardingApiTests`) — the one-call contract, idempotent retry, save-and-resume, tokens,
  and that an application's data lands only in its market's database.

---

## What I left out, and known limitations

**Not built**

* **Account opening and card ordering.** `APPROVED` is where this stops. Next would be an
  `ApplicationApproved` event to core banking and card issuing, from the same outbox.
* **Push notification** when a referral or retry finishes. The customer has to reopen the app
  (`GET /applications/{id}`). Mobile said no polling and no websocket, so push is the remaining option —
  an open question for them.
* **The actual erasure job.** Erasure requests are recorded and answered (deferred by AML retention);
  the scheduled job that erases records once `RetainUntilUtc` passes is not written.
* **Real IDNow / World-Check integrations.** The HTTP clients match my fake's wire format. The interfaces
  are the seam.

**Shortcuts I would not ship**

* **Staff authentication is a header** (`X-Staff-Id`) checked against a list in config. It produces the
  same claims an OIDC token would (person, role, market), so switching to `AddJwtBearer` changes no
  endpoint code — but as it stands anyone who can reach port 5200 can claim to be an officer.
* **Images are stored in the market database** rather than blob storage (Azurite). Fine for a demo,
  wrong at volume — see DECISIONS.md §6.
* **`EnsureCreated` instead of migrations.** Schema changes would need the databases dropped.
* **One `sa` connection string for every service.** Each service should have its own database login
  (GC §2), and the worker's should only be allowed to INSERT into the access log.
* **The local market databases share one SQL Server.** They are separate databases, which is enough to
  prove the code never mixes markets; in production each one is in its own country.
* **The worker processes one job at a time per replica.** Throughput would need parallelism per market.
* **A provider that never recovers keeps an application `IN_PROGRESS` forever**, retried every 5 minutes
  and logged as an error after 5 attempts. There is no ops view for stuck jobs yet.
* **The idempotency fingerprint covers identity fields and document hashes**, so a retry that re-encodes
  a photo differently counts as a different request (returns 422 rather than silently reusing).
