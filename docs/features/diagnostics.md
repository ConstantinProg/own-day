# Diagnostic Journal and Export

The journal helps investigate a Telegram request across receipt, Learning, dialogue, the provider, persistence, and delivery. The existing Learning records, provider checkpoints, budget ledger and transactional outbox remain authoritative. Use the snapshot when the journal has a gap.

| Command | File | Scope |
| --- | --- | --- |
| `/export` | ZIP containing `diagnostics.json` | Current state and up to 30 days of retained events |
| `/export recent` | JSON | Current state and the latest 100 events from the preceding 24 hours, returned in ascending UTC time and event ID order |
| `/export last` | JSON | The latest user request by receipt time, its retained correlated events and current state. An unfinished request is included at its current status. |

All modes require globally enabled Tester Mode, an authorized tester and that user's private chat. An export accepts no owner argument. Last mode selects from committed Telegram receipts and Learning requests, excluding diagnostic and Tester Mode commands. An empty request history produces a Telegram message. Only one export can be pending per owner.

The webhook commits Telegram update deduplication and the export job together. The export worker runs independently of the Learning queue. It reads the latest committed data using a read-only PostgreSQL `REPEATABLE READ` transaction. It rechecks tester access when publishing the outbox intent and again at delivery. The outbox sends export documents independently of ordinary replies, with a separate lock that synchronizes document delivery with Tester Mode cleanup. It stores the bounded file in a separate PostgreSQL row and removes its bytes after confirmation, terminal failure, expiry, or tester cleanup. No public URLs or temporary local files are created. Telegram may receive a second copy if its API accepted the first delivery but OwnDay could not save that receipt.

## Capture and retention

Ordinary capture records identifiers, statuses, known decision and failure codes, retry counts, timing and provider metadata. Detailed capture adds explicitly selected input and reply text, button labels and types, visible LLM messages, parsed results, dialogue audit entries and differences in tracked Learning fields. Signed callbacks, lease tokens, Tester Mode codes, SDK objects, complete configuration and hidden model reasoning are excluded. Free text passes through a filter for configured secrets, protected field names and recognizable credential formats. Free text can still contain personal data or a credential that lacks a recognizable format. Treat diagnostic files as private and avoid pasting secrets into the bot.

`/tester_off` ends detailed capture, withdraws pending exports and keeps committed history. Buffered events captured under the revoked authorization may retain only metadata. `/tester_reset` and `/tester_delete_all` erase the journal and clear its links to existing Telegram deduplication keys in the same transaction as the rest of the cleanup. The writer compares the buffered generation with the latest committed generation under a PostgreSQL lock, so it cannot recreate erased rows. A batch retry uses the same event IDs.

The buffer is process local and bounded. A separate writer stores batches and backs off after failures. It drops events when the buffer is full or retries are exhausted, and has a limited shutdown drain. Up to 30 days of history are available. Retention removes expired events and diagnostic receipt links in limited batches. The loss counter covers the exported owner on the current process and may be unavailable when its bounded counter is full. Restart or failure before insertion can leave uncounted gaps.

## JSON schema version 1

| Section | Contents |
| --- | --- |
| `metadata` | Mode, snapshot time, history bounds, owner, version, commit SHA if available and selected request or correlation IDs |
| `summary` | Current Learning request and dialogue counts, request counts for the selected receipt window, statistics for the returned timeline, observed loss and incomplete sections |
| `snapshot` | Current dialogue and expectations, programs and route versions, lessons, exercises, assessments, interactions, sessions, knowledge evidence, preferences, review schedules, Learning requests, provider attempt metadata and outbox statuses |
| `timeline` | Stable event ID, owner, UTC time, category, code, severity, operation and correlation IDs, optional request, attempt and entity references, payload schema version, detail level and structured data |
| `configuration` | Explicitly selected versions, environment, limits and flags for Learning, LLM, Telegram and diagnostics |
| `completeness` | Status per section and a journal availability note. Section values are `complete`, `partial`, `unavailable`, or `notImplemented`. An unavailable section contains `null`. |

Useful event codes include `request_received`, `request_queued`, `request_claimed`, `request_reclaimed`, `decision_proposed`, `decision_rejected`, `state_change_proposed`, `state_change_committed`, `state_change_rolled_back`, `commit_outcome_unknown`, `llm_call_started`, `llm_response_parsed`, `llm_validation_failed`, `provider_retry_scheduled`, `reply_formed`, `outbox_committed`, `outbox_claimed`, `telegram_send_started`, `telegram_api_acknowledged` and `telegram_retry_scheduled`. Telegram acknowledgement denotes API acceptance. Learning completion denotes a committed outcome and may be a rejection or failure. Queue wait is elapsed time between receipt and claim. Stage execution uses a monotonic clock.

Start an investigation with `/export last`. Match the receipt and current request status using its ID. Follow the attempt numbers and provider invocation IDs across retries and correlate the decision with the transaction outcome. Use the outbox message ID to trace delivery through the Telegram API acknowledgement. If the timeline has gaps, consult the current snapshot and its completeness statuses. Use `/export recent` when the incident spans multiple requests.

New provider failures during subject diagnostics are recorded as `diagnostic_provider_unavailable`. Older `diagnostic_reply` outcomes may include provider failures because those requests used the same outcome code for both results.

All export modes include `snapshot.userSettings` and `snapshot.llmBudget`. A persisted interface choice takes precedence. With no choice, the language resolved for the export receipt is the available observation. Tester-only receipt events can include the Telegram language observed on that update. Committed language changes use the ordinary change journal. Detailed keyboard entries carry a display label, type, and a semantic action when the callback is recognized for this owner.

The budget covers the owner's current UTC month. Charged tokens include conservative reservations and carried totals. Recorded usage and known cost estimates cover retained rows only. Missing estimates and pending reservations are counted separately. Remaining owner calls and tokens exclude competing global limits and cannot guarantee admission.

`currentState` counts all current owner requests. `requestCohort` counts requests received in the selected time window (and receipt for last mode). `selectedTimeline.severityErrorEvents` counts events. Timeout, cancellation and unknown-outcome counts identify distinct physical attempts within the selected events; these categories overlap. Rejected decisions count observed rejection events. Missing events can lower every timeline count.

## Deployment and limits

Apply `20261122091000_AddDiagnosticJournalAndExports` to PostgreSQL before running the compatible application. Validate in staging with an authorized tester, an unfinished Learning request, and a controlled Telegram delivery failure. The rollback migration deletes export intents and diagnostic data.

| Setting | Default |
| --- | --- |
| `Diagnostics:QueueCapacity` | 512 events |
| `Diagnostics:BatchSize` | 32 events |
| `Diagnostics:MaxExportBytes` | 8 MiB for the JSON and for the delivered file |
| `Diagnostics:ExportTimeoutSeconds` | 30 seconds |
| `Diagnostics:DocumentLifetimeMinutes` | 60 minutes |

The worker reads rows incrementally and holds a size-limited document in memory for durable publication. A limit breach returns a Telegram error. Large current snapshots can exceed the limit even in a compact mode. A journal event larger than 256 KiB keeps its metadata and marks its detail as omitted. Existing provider checkpoint metadata can help explain older requests. Detailed journal capture starts with this release and covers the Learning provider in the active Telegram workflow. Legacy Inbox analyses retain their existing storage. Exports can miss events still queued in memory. The exporter reports data read failures where the snapshot transaction remains usable.
