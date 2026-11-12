# Learning request processing

The current implementation records completed synchronous Learning commands. It does
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
the new table or links. There is no Ready producer or consumer in this version.

Repository deployment configuration defines one app service and one PostgreSQL 18
service; CI provides PostgreSQL 18 and disables test connection pooling. Actual deployed
replicas, webhook connections and active outbox deliverers have not been inspected.
The existing long transaction during provider calls remains until later changes.
Explicit ordinals identify reply order; delivery currently uses its existing timestamp
ordering. Stable cross-request ordering and delivery topology are subsequent gates.

Input and result content must not be added to operational logs. Requests must not be
purged independently of source deduplication and future operation markers without an
approved retention policy.
