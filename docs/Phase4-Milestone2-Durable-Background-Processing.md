# Phase 4 — Milestone 2: Durable Background Processing

This document describes analysis scheduling and recovery. The existing worker now also applies saved results
using [automatic processing](Phase4-Milestone5-Automatic-Processing.md). Statements below about no target
creation describe the analysis job/use case, which remains separate from application.

Implemented on PostgreSQL with a .NET `BackgroundService`. No broker, scheduler package or new production dependency
is required. AI output remains a proposal: this processing never invokes domain transitions or creates targets.

## Capture and execution

`EfInboxItemStore.AddAsync` tracks an `InboxAnalysisJob` alongside each new Inbox item and saves both in one EF
transaction. A navigation lets EF propagate the generated item ID without an intermediate commit. The Telegram
transaction already encloses capture, update deduplication and reply outbox intent, so all four commit or roll back
together. The webhook neither resolves the analyzer nor waits for AI. The unique job index on `inbox_item_id` is a
second protection against duplicate automatic jobs. Direct Application capture through this store gets the same
guarantee. Direct `DbSet.Add` is a persistence primitive, not the capture use case, and does not schedule analysis.
Jobs are captured even when AI is disabled; enabling analysis later drains untouched jobs for still-active items.
The migration does not enqueue pre-existing Inbox entries.

`InboxAnalysisWorker` runs a configurable number of sequential processing loops, each with its own DI scope and
DbContext per reservation. One PostgreSQL `UPDATE ... RETURNING`, using `FOR UPDATE SKIP LOCKED`, reserves a due
Ready job or reclaims an expired Running job. Reservation increments the attempt counter and replaces a random
lease token. Availability and lease expiry use database time, avoiding clock skew between application instances.
There is no batch prefetch that could expire while awaiting a processing slot. Parallelism is per Host instance.

The worker invokes `InboxAnalysisService.ProcessJobAsync` in Application. It checks owner and active Inbox state,
input bounds and the attempt budget before using AI. Under short job and Inbox row locks, `StartAsync` rechecks
state/lease, allocates an analysis version under the existing Inbox lock, and atomically links it to the job.
This transaction commits before the provider is called. Subsequent attempts load that analysis and its saved
context snapshot. Parsing, independent validation, timeout handling and retry policy belong to Application.
Before invoking the provider, a Pending analysis must match the current analyzer's model, schema version, prompt
version, effective reasoning effort, output token limit and provider timeout. A mismatch after deployment or configuration changes terminates the job and analysis as Failed
(`descriptor_mismatch`) without a provider call or retry, preserving the original metadata. An operator can request
a new manual analysis version with the current descriptor. Already finished results are acknowledged independently
of descriptor changes.
The manual `AnalyzeAsync` operator command still deliberately allocates a new version on each invocation.
Manual and automatic analyses can coexist without overwriting one another.

`FinishAsync` locks the job, checks its current token and expiry, then locks/rechecks the Inbox item. It either
schedules a retry, skips a processed/discarded item, or saves terminal analysis status, proposals and job completion
in one transaction. Ownership in the lease is checked against the stored job. A stale owner cannot publish results.
Result completion alone never modifies Inbox text/status, projects or other target entities. A state change during
the network request discards the returned proposals and terminates the job as Skipped. Project context remains a
historical snapshot; any future application of proposals must revalidate current context and ownership.

## Identity, schema and recovery

Migration `20261025100206_AddInboxAnalysisJobs` adds only `inbox_analysis_jobs`, with:

- Job UUID, owner-qualified Inbox FK, unique Inbox ID, creation time and unique optional analysis FK.
- Status: Ready, Running, Completed, Failed, Skipped.
- Reservation count, due time, lease token/expiry and last safe diagnostic code.
- A partial `(available_at, id)` index for Ready/Running jobs and lifecycle/attempt check constraints.

The existing analysis/proposal tables and their unique version/ordinal constraints remain in use.
The logical job is distinct from its attempts (counter and lease token), and from its analysis version (`analysis_id`).
Transient failures keep that version Pending; retry does not create another user revision or duplicate proposals.
Attempt history is represented by the reservation counter, last code and structured logs; there is no separate
attempt history table. Attempts count reservations, including interrupted reservations before a provider call.

On shutdown the Host cancellation token reaches Application and the SDK. Unfinished work remains reserved until
lease expiry; a new Host resumes it. Database outages and unexpected worker errors also leave the durable lease
to expire. An AI timeout cancels the request and yields a bounded retry/terminal failure. `WaitAsync` bounds a
non-cooperating adapter too; a late response has no persistence continuation. No heartbeat or open PostgreSQL
transaction spans the provider call. Lease configuration must exceed the analysis timeout by at least 30 seconds.

If the last allowed reservation crashes, one recovery reservation terminates its Pending analysis without another
provider call. Thus the reservation counter can reach `MaxAttempts + 1`, while provider executions are capped at
`MaxAttempts`. Normal terminal failures preserve the original Inbox entry for manual processing.

The result and job acknowledgement commit atomically. A failed transaction rolls both back; after an ambiguous
successful commit the completed job is no longer claimable. If a completed analysis is nevertheless linked to an
unfinished job, recovery acknowledges it without calling AI or inserting proposals again.

Maintenance runs at startup and once per minute, including when analysis is paused. It marks unlinked manual
Pending analyses older than ten minutes Failed (`interrupted`); this exceeds the maximum provider timeout of five
minutes and recovers history from older releases too. When AI/background processing is disabled or the key is
missing, maintenance stops expired Running jobs and retries that already own an analysis (`disabled`). Untouched
Ready jobs stay queued. Active unexpired leases are respected. Operators should configure AI consistently across
instances: a paused instance can stop expired attempts while another instance is enabled.

There is no exactly-once guarantee for OpenAI execution. A crash after a provider response but before transaction
commit, or lease expiry during a process pause/database outage, can cause another billable provider call. Fencing
and atomic persistence guarantee one terminal result/proposal set for the automatic job. This does not deduplicate
separate intentional manual revisions.

## Configuration and operations

Apply migrations before starting the updated Host, using the existing EF tooling and runtime connection settings.
See [the analysis foundation](Phase4-Milestone1-AI-Analysis.md) for model, token limits and the operator command,
and [the model configuration guide](Inbox-AI-Model-Configuration.md) for the GPT-6 Luna migration and VPS steps.
API keys belong in runtime secrets/environment variables. A missing key pauses the worker and does not fail Host
startup. The disabled state is logged once; no analyzer or SDK client is resolved in paused processing loops.
Configuration is read on startup; restart the Host to apply changes.

| Key under `InboxAnalysis:Background` | Default | Allowed |
| --- | --- | --- |
| `Enabled` | `true` | Boolean; provider must also be enabled with a key |
| `Parallelism` | `1` | 1–8 per Host |
| `PollSeconds` | `5` | 1–300 |
| `LeaseSeconds` | `120` | timeout + 30 through 3600 |
| `TimeoutSeconds` | `60` | 1–300 |
| `MaxAttempts` | `3` | 1–10 |
| `InitialBackoffSeconds` | `30` | 1–3600 |
| `MaxBackoffSeconds` | `900` | initial backoff through 86400 |

Environment variable example: `InboxAnalysis__Background__Parallelism=1`. Provider settings remain under
`InboxAnalysis:OpenAI`, with `Enabled=false` by default. Conservative limits are intended for a small VPS.
SDK retries remain disabled; the durable job exclusively controls retries and cost bounds.

| Reason/code | Action |
| --- | --- |
| Network/5xx/408/409 `provider_failure`, `rate_limit`, `timeout` | Retry up to the reservation budget |
| `refused`, `incomplete_response`, `invalid_response` | Stop; no repeated generation for malformed/refused output |
| `input_limit`, `context_limit` | Stop; do not truncate input or enlarge the budget automatically |
| HTTP 401/403 `configuration`, 429 quota/billing `budget_limit` | Stop; operator intervention required |
| Other provider HTTP errors `permanent_failure` | Stop |
| `descriptor_mismatch` | Stop; request a new manual version after model/schema/prompt/reasoning/token/provider-timeout changes |
| Host cancellation | Propagate cancellation; recover after lease expiry |
| Unexpected runtime/database error | Log safe error type; recover via lease, consuming the finite reservation budget |

Backoff is `min(max, initial * 2^(attempt-1))`, multiplied by independent uniform jitter in `[0.5, 1]`.
Failed/Skipped jobs are terminal. Fixing configuration does not resubmit them; an operator can request a new
manual analysis version. This milestone adds no universal retry dashboard, per-user quotas or daily spend ledger.
Each new Inbox item has its own finite budget; large input volumes still require operational spending controls.

Structured logs contain job ID, reservation number and fixed reason codes, without Inbox text, provider bodies,
credentials or exception messages. The `OwnDay.InboxAnalysis` Meter exposes `inbox_analysis.claims`,
`inbox_analysis.outcomes` (bounded `code` tags), `inbox_analysis.active`, `inbox_analysis.duration` in seconds, and
`inbox_analysis.worker_errors`. Standard .NET metrics collectors can subscribe; no exporter dependency is added.
The durable queue can also be inspected directly:

```sql
SELECT status, count(*), min(available_at) AS oldest_due
FROM inbox_analysis_jobs GROUP BY status;
```

## Validation and next work

Tests use fake analyzers/HTTP transports only. PostgreSQL tests require `OWNDAY_TEST_POSTGRES_CONNECTION`, create
isolated schemas and apply real migrations. They cover atomic insertion/rollback, webhook deduplication and prompt
response, actual Host restart, parallel claims, bounded hosted concurrency, transient/permanent errors and budget
exhaustion, expired lease fencing, timeout/late completion, database disconnect after response, atomic rollback of
result and acknowledgement, already-saved result recovery, item state changes, disabled/missing configuration,
manual orphan recovery and concurrent intentional operator revisions. Existing manual Inbox tests remain in the suite.

For Milestone 3, policy/confirmation must consume stable analysis/proposal IDs, verify current Inbox/project state
and context, then invoke existing domain use cases with transactional deduplication and outbound intent. No Policy
Engine, target creation, Telegram confirmation, undo, personalization or additional provider is implemented here.
