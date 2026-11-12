# Learning request processing

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
