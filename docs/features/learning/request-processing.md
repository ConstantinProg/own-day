# Learning request processing

## Current command ingress and rollout gate

The producer is controlled by `Learning:Processing:DurableIngressEnabled` (default
`false`). It requires `Enabled=true`; worker startup verifies PostgreSQL and all
installed migrations before serving traffic. The schema is unchanged by this
cutover. All Learning commands, including preference/schedule/selection writers
and read-only commands, share the owner's sequence. Productivity and explicit
Inbox commands keep their existing paths. There are no Learning callback routes in
the current router; invitation start is an existing command and uses this sequence.

Receipt atomically commits update deduplication, immutable versioned input and
destination before HTTP 200. `ProcessedAt` means receipt completion for queued
updates; `learning_requests.status/outcome` describes actual processing. Failure
before commit rolls back receipt, allowing Telegram redelivery. Duplicate source
updates acknowledge their existing receipt even while acceptance is paused.
Worker cancellation is independent of the completed HTTP request token. Queued
commands remain visible in chat; receipt does not wait for command deletion.

Payload version 2 binds Answer/Clarify/Explain to the active Lesson at receipt.
The worker checks this binding before preparation and rechecks target stamps at
apply. Missing/replaced questions produce an explicit clarification with no
assessment; an answer queued before generation cannot bind itself to the new
question. Version 1 readers are retained for already accepted test requests.
Do not roll back to a consumer that cannot read version 2 while any remain pending.

`AcceptancePaused=true` closes new Learning receipts while the compatible worker
drains. Switching back to synchronous mode is rejected for owners with pending
requests. `MaxQueueAgeSeconds` (default 300, range 30–86400) closes new acceptance
when any unfinished head is overdue. Worker recovery continues. The
`learning_queue` readiness check exposes pending count and oldest age in its data;
an overdue backlog is unhealthy. Configure monitoring for this check and the
existing request/attempt outcome logs. Liveness remains independent of readiness.

Deployment remains operator-controlled: apply additive schema, install a compatible
consumer, run test-bot smoke, stop/drain old synchronous ingress, then enable the
producer for the entire bot/user cohort. Verify one ingress connection and one
active deliverer without deployment overlap. No production flag was enabled or
deployment performed by this change. Test-bot smoke, topology verification and
production-size outbox migration rehearsal remain external gates. On rollback,
pause acceptance, drain/recover with a compatible worker and retain all data and
markers; never reintroduce a synchronous writer over pending work.

The sections below describe the earlier incremental deliveries and their original
disabled production state; the settings and protocol above are the current
implementation.

The public implementation records completed synchronous Learning commands. It does
not acknowledge Learning HTTP requests before execution or schedule background work.
`processed_telegram_updates`, Domain changes, `learning_requests` and linked Telegram
outbox intents commit in the existing single transaction. A rejected command or a
handled provider failure also records a completed request and its explanatory reply;
`reply_committed` describes persistence of the reply intent, not pedagogical success
or delivery. Unhandled failures and cancellation roll everything back for redelivery.

Requests retain the normalized command envelope (`learning_command`, version 1),
source update/message identifiers, destination, owner, sequence and timestamps.
Sequences are allocated under the existing PostgreSQL transaction advisory user lock.
The source key and `(user_id, sequence)` have unique indexes. No historical requests
are fabricated. Outbox links and reply ordinals are nullable for legacy and non-Learning
messages, with a foreign key and unique `(learning_request_id, reply_ordinal)`.

## Entry points

The synchronous receipt covers all commands handled by `TelegramCurriculumCommandHandler`,
`TelegramKnowledgeCommandHandler` and `TelegramStudyCommandHandler`, plus `learn`,
`answer`, `learn_ask`, `learn_explain`, `learn_pause`, `learn_resume` and `learn_finish`.
Read-only commands in these groups are also recorded. Productivity commands and ordinary
Inbox text retain their current handling. Existing parsers, presenters and use cases
remain authoritative for argument validation and behavior.

## Deployment

### Upgrading the tutoring dialogue

Drain the Learning queue on the current image before deploying the adaptive dialogue
or changing its provider step sequence. Provider checkpoints include the allowed
intents and step order. A new consumer may otherwise complete a prepared request as
`stale_target` and ask the learner to repeat it.

1. Pause Telegram webhook ingress at the proxy with HTTP 503 so Telegram can retry.
   Keep the current application and Learning worker running during the drain.
2. Check the application database. Both counts must be zero before replacing the
   image. Ready includes requests waiting for a retry. Running includes active and
   expired leases that the current worker must recover.

   ```sql
   SELECT count(*) FILTER (WHERE status = 1) AS ready,
          count(*) FILTER (WHERE status = 2) AS running
   FROM learning_requests;
   ```

3. Deploy the new image while ingress is paused, then restore ingress after the new
   worker is healthy. Follow the same sequence before rolling back across the policy
   change. For a deployment triggered by a push to `main`, drain before pushing.

### Schema and topology

Apply `AddLearningRequests` before starting this code. It only creates a table and
nullable columns/indexes/constraints on the existing outbox; old rows remain valid.
The previous Host can run against the expanded schema. Roll back code without dropping
the new table or links. Public ingress has no Ready producer.

Repository deployment configuration defines one app service and one PostgreSQL 18
service; CI provides PostgreSQL 18 and disables test connection pooling. Actual deployed
replicas, webhook connections and active outbox deliverers have not been inspected.
The existing long transaction during provider calls remains until later changes.
Explicit ordinals identify reply order; delivery currently uses its existing timestamp
ordering. Stable cross-request ordering and delivery topology are subsequent gates.

Input and result content must not be added to operational logs. Requests must not be
purged independently of source deduplication and future operation markers without an
approved retention policy.

## Disabled background lane

`AddLearningRequestLeases` extends completed records with compatible lease/recovery
fields and adds unique `(request_id, step)` applied-operation markers. Existing completed
records are unchanged. A request is Completed (0), Ready (1) or Running (2); constraints
enforce terminal timestamps/outcome versus active lease state. Completion is distinct
from Telegram delivery.

`Learning:Processing` options are validated on startup: Enabled defaults to false,
Parallelism to 2 (1–8), PollMilliseconds to 1000 (20–60000), LeaseSeconds to 30 (1–300)
and MaxAttempts to 3 (1–5). These are per-Host execution limits. The hosted worker only
supports typed version-1 Pause/Resume/Finish commands through existing Application use
cases. It cannot call a provider. Unsupported payloads and exhausted attempts finish
with an explanatory reply and release the head. Attempts exhausted by crashes get one
recovery reservation that performs no domain mutation.

PostgreSQL claim updates one unfinished head with a random token, database-clock expiry
and incremented attempt count. A Running or backed-off head blocks later requests,
even when a worker skips its row lock. Different owners can execute concurrently.
Claim only locks its request row and never subsequently waits for a user/target lock.
Apply consistently locks user advisory fence, request row, then Domain targets. Under
that transaction it validates owner, head, token, attempt and database-clock expiry,
executes the no-provider use case, then saves effect, marker, ordinal replies and
completion. It rechecks expiry after writes; a stale lease rolls everything back.
Crashes/DB failures leave the lease for bounded reclaim, including after Host restart.
Stopping the Host cancels its operations, independent of the already completed HTTP
request token. Public commands continue through the legacy synchronous handler.

Only isolated tests use a no-provider HTTP producer and explicitly enable the hosted
lane. Do not enable mixed synchronous/queued writers for one production user. Deploy
the additive schema before the disabled worker; do not roll schema back or delete
accepted work. If a test/cohort has accepted work, drain it with a compatible consumer
before reverting code. No public early-ack or provider operation is enabled here.

## Provider preparation lane

The background consumer also supports the existing provider commands via the
[provider inventory](provider-path-inventory.md). `AddLearningProviderSteps` creates
concrete versioned inputs, fingerprints, original target stamps and saved outcomes,
uniquely keyed by request and ordinal. It does not backfill historical results.
PostgreSQL xmin is mapped in Infrastructure for the addressed Learning/project/task
rows; system columns already exist, so user data is not rewritten. Raw locked
Project/Action readers explicitly project xmin for compatibility with this mapping.

Preparation runs the same Application use case until the next external boundary,
then rolls back and disposes its scope. A fenced transaction persists that step
before any network call. Provider/source I/O uses an independent scope and a linked
finite timeout (`ProviderTimeoutSeconds`, default 15, range 1–120). The worker awaits
the actual adapter task; it does not release its slot by abandoning a running task.
New scopes revalidate inputs and targets while consuming saved results. A changed
branch or row stamp yields `stale_target`, with no use-case effects; the user gets
an explicit instruction to inspect the current lesson or repeat the command.
Only the final complete invocation commits effects/replies/completion.

Lease renewal uses the current token, attempt and database-clock expiry. Public
ingress remains synchronous and the hosted lane remains disabled by default.
Unknown physical-attempt recovery/retries are the next gate before public cutover;
this lane alone does not claim complete crash or cost accounting guarantees.

## Physical attempts and recovery

`AddLearningProviderAttempts` adds technical invocation identities linked to the
concrete request/step. Admission commits before calling an adapter. Reclaim cannot
start a replacement while an older invocation remains unresolved. A request with
unknown outcome ends with an explicit, durable explanation and no pedagogical
effect; the original input remains available. A late result can update its own
attempt metadata, but cannot write a stale checkpoint or Domain state.

`MaxProviderAttempts` defaults to two and is restricted to 1–3 per durable step.
Only known transport/rate-limit failures retry, with a short bounded delay. Invalid
response, unavailable configuration and permanent HTTP errors terminate without
automatically growing the prompt/output budget. SDK retries remain disabled.
Each real retry has a distinct ID; crash after response and before durable result
can still incur another physical call. This is not a financial ledger or a claim
of exactly-once provider billing; shared cost admission belongs to S2.

Logical deadline cancellation stops waiting even if an adapter ignores its token.
The physical task retains its scope and concurrency slot until it actually ends.
While an unresponsive task remains, that Host refuses new provider admission;
no-provider operations still work. Durable unfinished attempts protect reclaim
after restart. Invocation reconciliation has an explicit independent five-second
cleanup token and can update only technical metadata. Failed reconciliation leaves
an honest unknown record, rather than inventing success or zero consumption.

The same migration assigns existing outbox rows a delivery order preserving the
previous `(created_at, id)` ordering, and introduces a PostgreSQL sequence. New
reply ordinals receive sequence values before EF batches INSERTs. Delivery and
backoff use that persisted order, independent of Host clocks and random GUIDs.
Sequence gaps after rollback are valid. Private-chat/user transaction locks and
one active deliverer remain the deployment contract; scale-out delivery claiming
is not supplied by these changes.

Deploy the additive schema before a compatible consumer. Drain any isolated
accepted cohort with a compatible reader before rollback; retain checkpoint,
attempt, dedup and effect markers. Public ingress is still disabled. Rehearse the
outbox backfill on production-size data before deploying; no production migration,
live bot call or live provider evaluation is claimed by local tests.
