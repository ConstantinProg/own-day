# Inbox Undo and correction

The current interface promotes `/undo` and `/process`, preserving the aliases below. See
[Telegram UX consolidation](Phase4-Milestone9-Telegram-UX.md) for navigation and revised messages.

`/inbox_undo <inbox ID>` and the **Undo entire result** / **Отменить весь результат** inline button undo
one complete successful automatic application. Accepted compatibility confirmations are supported too.
The ID is the source Inbox ID, not an individual task/project ID. There is no AI call or analysis scheduling
in Undo. The correction workflow is: undo, open `/inbox <id>`, choose the correct type/text/project through
the existing manual dialog, and confirm.

## Safety and retained history

Undo removes only the entities created by that application. An existing project linked by a created task,
note, idea or waiting item is never deleted. A composite result is one indivisible operation: one conflict
preserves the entire batch. The success notification lists the number and types of removed entities;
the original notification lists all affected type/ID/title values and explains the action before the button.

New applications persist a SHA-256 fingerprint of every target's scalar creation state in `undo_state`.
The state includes identity, owner, text, status, lifecycle timestamps, project link and, where applicable,
waiting source and date. Capture reads the persisted rows inside the original application transaction.
Fields are ordered consistently; timestamps use PostgreSQL microsecond precision. This is a focused
creation-state check for Inbox Undo, not entity versioning or event sourcing. Changing the state and then
restoring exactly the original values is indistinguishable from an unchanged current state.

Undo refuses safely when:

- The source or successful automatic result does not belong to the caller (generic unavailable response).
- Any target is missing, changed, completed, archived, resolved or owned by someone else.
- A created project has acquired any task, idea, note or waiting item referencing it.
- Another Inbox entry references one of the targets, or the current source target set differs from the audit.
- The application predates creation-state capture and has no trustworthy snapshot. Migration deliberately
  does not backfill fingerprints from today's potentially edited state. Such results require manual review.

The refusal explains that nothing was removed and offers `/inbox_result <id>` for review. No automatic
partial cleanup or force-delete command is provided. Unexpected storage failures propagate and roll back;
they do not produce a false success message.

On success, the original Inbox row becomes Active, its current target collection and legacy target fields
are cleared, and `ManualProcessingOnly` is permanently set. The original text, analysis, proposals, policy
decision, original target mappings and creation fingerprints remain. `undone_at` is written once on the
application audit and survives restart. Manual reprocessing establishes a new current target mapping;
the original automatic mapping stays in the application audit. Repeating Undo returns “already undone”
and never removes the manual correction.

The existing application row prevents replay of the same saved analysis. The persisted manual-only flag
also blocks every later analysis version through both policy versions, including an outstanding analysis
that finishes after Undo. New assessments record `KeepInInbox` with `ManualProcessingOnly`. Undo itself
does not enqueue an analysis job, reset an analysis or invalidate the saved history.

The manual workflow creates one entity per processing, as before. A composite entry can be restored and
manually represented by one corrected entity; splitting it into several separately edited entities remains
outside the current manual workflow. There is no general entity editor in this change.

## Transactions, locking and delivery

1. The existing owner-qualified `inbox_items ... FOR UPDATE` lock serializes Undo, application, manual
   processing, discard and analysis version allocation.
2. The successful application is reloaded under that lock. `undone_at` fences repeated and concurrent Undo.
3. Target rows are locked with owner-qualified `FOR UPDATE`, projects first, then by kind and ID.
   Persisted state is reloaded before comparison. Target mutation waits for Undo or commits before its check.
4. Project locks conflict with the FK key-share locks used by concurrent child creation. If child creation
   wins, Undo waits and sees the committed child; if Undo wins, a later FK insertion cannot reference a
   deleted project. All known child types are checked without excluding archived or completed data.
5. Only after all checks succeed are all targets marked for deletion and the Domain Inbox transition made.
   The audit timestamp and success outbox message join the same PostgreSQL transaction.
6. Commit persists the whole result. An insert/delete/audit/commit failure rolls everything back. A lost
   commit acknowledgement is recovered by reloading the persisted audit in a fresh scope.

Telegram commands and callbacks also use the existing webhook authentication, update deduplication,
per-user transaction lock and transactional reply outbox. Callbacks accept only human senders in their
own private chat and carry `iu:<inbox ID>`, never a user identity or permission token. Duplicate update IDs
do not create another reply. A different update repeating Undo can receive an “already undone” reply.
Callback acknowledgement happens after commit through the existing responder.

For entries with a saved Telegram destination, success is queued once by the application use case, using
the current language preference. Without a destination, no chat is inferred; a Telegram command's reply
is queued by the webhook in its transaction. Direct non-Telegram calls without a destination produce no
Telegram notification. Refusal and repeated-command replies also use the webhook outbox.

Messages use plain text, explicit labels, counts and actionable commands. No color, emoji, spatial layout,
HTML or visual-only state is required. Buttons have descriptive Russian/English labels, and the command
fallback exposes the same action to NVDA and VoiceOver. All supported 20-result messages remain bounded
by the existing title budget and Telegram's 4096-character limit. Actual assistive-technology testing was
not performed; message contents and markup are verified automatically.

## Migration

Apply `20261025180000_AddInboxUndo` before starting the updated Host. It adds:

- `inbox_items.manual_processing_only`, non-null boolean, default false.
- `inbox_automatic_applications.undo_state`, non-null jsonb, default empty array for historical records.
- `inbox_automatic_applications.undone_at`, nullable UTC timestamp.

No existing Inbox, target or analysis rows are deleted by the migration. Use the normal deployment EF bundle
or the repository's EF migration command with runtime connection configuration. Rolling back the schema
after users perform Undo loses the manual-only protection and the new Undo audit; retain this migration
once the feature has been used.

## Telegram dialogs

The following are abbreviated examples; actual success messages also retain the original result details.

Single result and correction:

```text
OwnDay: Входящие #42: создано.
        Задача #101: Купить молоко
        Отмена удалит все сущности этой обработки, если они не изменились. /inbox_undo 42
        Затрагиваемых сущностей: 1. Задача: 1
        [Отменить весь результат]
User:   /inbox_undo 42
OwnDay: Обработка Inbox #42 отменена. Исходная запись возвращена во входящие.
        Обработайте вручную: /inbox 42. Автоматическая обработка этой записи отключена.
        Затрагиваемых сущностей: 1. Задача: 1
User:   /inbox 42
OwnDay: [Existing manual type, text, project and confirmation dialog]
User:   [Select Note, enter corrected text, confirm]
OwnDay: [Manual processing success]
```

Composite:

```text
OwnDay: Входящие #43: создано.
        Задача #102: Купить молоко
        Задача #103: Позвонить Ивану
        Идея #31: Изучить японский
        Отмена удалит все сущности этой обработки, если они не изменились. /inbox_undo 43
        Затрагиваемых сущностей: 3. Задача: 2, Идея: 1
        [Отменить весь результат]
User:   [Отменить весь результат]
OwnDay: Обработка Inbox #43 отменена. Исходная запись возвращена во входящие.
        Обработайте вручную: /inbox 43. Автоматическая обработка этой записи отключена.
        Затрагиваемых сущностей: 3. Задача: 2, Идея: 1
```

Repeat, including after manual correction:

```text
User:   /inbox_undo 43
OwnDay: Обработка Inbox #43 уже отменена. Другие сущности не изменены.
        Для ручной обработки: /inbox 43.
```

Conflict after completing a created task or adding data to a created project:

```text
User:   /inbox_undo 44
OwnDay: Безопасно отменить Inbox #44 нельзя: созданная сущность изменена, завершена,
        удалена, связана с другими данными или её исходное состояние нельзя проверить.
        Ничего не удалено. Посмотрите /inbox_result 44.
```

## Validation

PostgreSQL integration tests use real migrations and isolated schemas. They cover all five kinds,
composites, concurrent Undo, concurrent completion/manual correction/project linking, foreign owners,
changed/removed/transferred targets, existing project preservation, lifecycle changes, historical records,
outbox failure, commit failure, lost acknowledgement, cancellation, restart and old/new-analysis replay.
Telegram HTTP tests cover commands, inline callbacks, duplicate updates, localized feedback, conflict,
foreign callbacks and the complete correction dialog. Domain tests check exact-batch reopening and manual
reuse; router tests check callback shape and private human sender requirements. The existing model snapshot
test verifies the migration matches the runtime EF model.

```powershell
# OWNDAY_TEST_POSTGRES_CONNECTION must identify an isolated test PostgreSQL database.
dotnet restore OwnDay.slnx
dotnet build OwnDay.slnx --configuration Release --no-restore -m:1
dotnet test OwnDay.slnx --configuration Release --no-build -m:1
```

Ordinary tests do not contact Telegram or OpenAI. The separately opted-in real OpenAI smoke test can remain skipped.
