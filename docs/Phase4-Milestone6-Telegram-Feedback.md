# Telegram feedback and confirmation

Successful result notifications now include an accessible Undo button and `/inbox_undo <id>` fallback.
See [Undo and correction](Phase4-Milestone7-Undo-and-Correction.md) for the added behavior and migration.
References below to the absence of Undo describe the original feedback implementation.

Automatic processing now commits one Telegram notification alongside the Inbox transition, created targets
and application audit. The default remains `inbox-policy-v2`: all structurally valid proposals are applied
as one batch without user confirmation. No model call, new policy setting, Undo or general editor is added.

## Messages and entry points

Telegram capture records the originating private chat and resolved locale in `telegram_inbox_destinations`,
inside the existing webhook transaction. Only entries with this explicit destination receive proactive feedback;
non-Telegram entries and old entries without a destination do not invent a chat from an application user ID.
The current explicit language preference takes precedence over the captured locale. English and Russian
messages use the existing resources.

- Success: one plain-text message names every created type, ID and canonical title in saved proposal order.
  There is no message per proposal, HTML, decorative emoji or table to navigate with a screen reader.
- Composite batches include up to all 20 results in the same message. Long titles are shortened evenly to fit
  Telegram's 4096 UTF-16 character limit; IDs and every result remain visible. Whitespace is normalized and
  truncation avoids splitting surrogate pairs. Complete text remains in the entities and saved analysis.
- Failed/invalid analysis: a generic message confirms preservation and offers `/inbox <id>` for manual processing.
  Provider diagnostics, credentials, raw output and technical error codes never enter these messages.
- Policy rejection: preservation and attention are explained with the same manual entry point. Superseded analyses
  and already processed/discarded Inbox entries do not generate misleading attention notifications.
- `/inbox_result <id>` shows the saved result and a bounded preview of the original text, including after processing.
  Reads are owner-qualified. If later analyses are blocked, an already applied result takes precedence.
  The Inbox ID, analysis ID, proposal IDs and target IDs retain the identity needed for future Undo.
  The command is usable now and makes no promise of cancellation.

Existing `/inbox`, `/discard`, `/cancel` and manual target/text/source/project/confirmation dialogs continue working.
A manual dialog overtaken by automatic processing uses its existing unavailable response. Opening a result does
not create or reset an Inbox processing draft. Rejecting a proposal preserves the active original entry.

## Confirmation

`RequireConfirmation` renders a preview with **Create** / **Keep in inbox** buttons, translated in Russian as
**Создать** / **Оставить во входящих**. Applicable source, waiting start in UTC and project ID are shown too.
Callbacks contain an action and a stable analysis UUID, within Telegram's 64-byte callback-data limit.
They never carry a title, user ID, domain command or permission token.

The active v2 contract currently has no route requiring confirmation. The real compatibility route is the
explicit v1 policy: `ApplyAsync(..., InboxPolicyEngine.LegacyVersion, cancellationToken)` persists its assessment
and queues this preview. This is a trusted Application entry point, not a new user setting or default gate.
The same confirmation use case serves any persisted `RequireConfirmation` assessment; blocked proposals
cannot be turned into permission by pressing a button. The current v1 rules reject composite proposals;
v2 composites continue applying automatically.

The callback is authenticated by the existing webhook filter, accepts only a human sender in their private
chat, then uses the existing Telegram update deduplication and user serialization transaction. Application:

1. Finds the analysis assessment by the authenticated owner, then takes the existing owner-qualified Inbox lock.
2. Reloads the assessment after obtaining the lock, so waiting concurrent requests see the winning resolution.
3. Reloads current analysis, latest version and locked project context, re-evaluates the original policy version
   and compares its state fingerprint with the offered assessment. The preview expires after 24 hours.
4. Marks a changed, superseded, inactive or expired offer stale without creating targets. No stale proposal
   silently applies a different current interpretation or project link.
5. On acceptance invokes the shared batch processing use case; on rejection retains the Inbox entry.
   Saves one resolution, resolution time, confirmed target mapping and feedback outbox row in the same transaction.

The original route, decision, proposal snapshot and original target mapping remain unchanged. Confirmation
resolution is written once under the Inbox lock. Competing accept/reject/manual/discard operations cannot
create a second batch. The first committed outcome wins. A repeated update creates no new response; a new
press of an old button explains the saved outcome without creating another success notification.
Unknown/foreign offers return a generic unavailable message without disclosing their contents.

The text-command equivalents are `/inbox_accept <analysis-uuid-N>` and `/inbox_reject <analysis-uuid-N>`;
the buttons are the normal entry point. `/inbox_result` supplies the readable result entry point for users.
If an entry has no saved Telegram destination, resolving it through a command or callback still queues a reply
to the current chat in the webhook transaction, without registering a destination for proactive notifications.
Callback acknowledgement is best effort after transaction completion, and never deletes the bot's preview.
Old keyboards can remain visible; durable checks, rather than keyboard removal, enforce validity.

## Persistence, delivery and restart

Apply the `AddInboxTelegramFeedback` migration before starting the updated Host:

```powershell
dotnet ef database update --project src/OwnDay.Infrastructure --startup-project src/OwnDay.Infrastructure
```

It adds the destination table, optional serialized reply markup to the existing outbox, and nullable confirmation
outcome/time plus confirmed target mappings to the existing application audit. Existing audits get `[]` as their
confirmed mapping; existing outcomes are not replayed into new notifications. There is no destination backfill
because the previous schema did not retain the capture transport.

The initial notification's outbox primary key equals the analysis UUID. Existing application identity plus the
Inbox lock fences application retries; resolution state under the same lock fences confirmation retries.
Creation, resolution and notification intent either all commit or all roll back. Outbox insert failures and
commit failures leave the source available for recovery. A lost commit acknowledgement recovers the existing
audit and intent. Deleting delivered outbox rows through existing cleanup does not permit reapplication.

The existing analysis worker discovers terminal results without application audits after restart. Committed
outbox rows, including keyboards, are independently retried by the existing delivery worker. No Telegram call
or AI call occurs within an application/domain transaction. Delivery only sends the persisted text/markup;
it never calls target creation. The existing backoff and permanent Telegram rejection rules remain in effect.

Delivery retains the existing **at-least-once** limitation: if Telegram accepts a send but its acknowledgement
or the subsequent database status update is lost, the physical message can be repeated. There is still one
logical notification intent and one domain batch; Telegram sending has no exactly-once idempotency key.
The existing delivery worker also has no cross-Host reservation; deployments should run one delivery worker.

## Example dialogs

IDs below are illustrative; the immediate capture reply is unchanged.

Single entry:

```text
Пользователь: Купить молоко
OwnDay: Сохранено во входящие.
OwnDay:
Входящие #42: создано.
Задача #17: Купить молоко
Результат и исходный текст: /inbox_result 42
```

Composite entry:

```text
Пользователь: Купить молоко, позвонить Ивану и когда-нибудь выучить японский
OwnDay: Сохранено во входящие.
OwnDay:
Входящие #43: создано.
Задача #18: Купить молоко
Задача #19: Позвонить Ивану
Идея #5: Выучить японский
Результат и исходный текст: /inbox_result 43
```

Analysis error:

```text
Пользователь: Разобрать планы поездки
OwnDay: Сохранено во входящие.
OwnDay:
Запись #44 сохранена. Автоматический разбор не удался. Можно разобрать её вручную.
Открыть запись: /inbox_result 44. Разобрать вручную: /inbox 44
Пользователь: /inbox 44
OwnDay: [существующий ручной выбор типа записи]
```

Confirmation, when explicitly using a policy that requires it:

```text
OwnDay:
Входящие #45: подтвердите предложенный результат. Кнопки действуют 24 часа.
Задача: Позвонить Ивану
Открыть запись: /inbox_result 45. Разобрать вручную: /inbox 45
[Создать] [Оставить во входящих]
Пользователь: [Создать]
OwnDay:
Входящие #45: создано.
Задача #20: Позвонить Ивану
Результат и исходный текст: /inbox_result 45
Пользователь: [Создать повторно]
OwnDay: Это предложение уже применено. Новые сущности не созданы.
```

Choosing **Оставить во входящих** instead replies that the proposal was rejected and the original preserved,
with the manual processing command. It does not discard the entry or call AI again.

## Verification

The HTTP/SQLite tests exercise realistic callback serialization, five types, composites including 20 maximum-length
titles, bounded result messages, failures, policy rejection, manual completion after failure, persisted keyboards,
deduplicated updates, repeated/opposite buttons, foreign ownership, expiration, changed input, superseded analysis,
manual/discard winners, retry across fresh delivery scopes and independent confirmation scopes.

PostgreSQL tests use real migrations and isolated schemas for atomic target/audit/outbox writes, outbox insert and
commit rollback, lost commit acknowledgement, fresh-context recovery, four concurrent auto-application workers,
competing accept/reject operations, confirmation rollback/recovery, project locking and manual-processing races.
Existing manual Telegram, language, authentication, analysis and outbox tests remain part of the full suite.
No normal test sends Telegram messages or calls an AI provider.

```powershell
# Set OWNDAY_TEST_POSTGRES_CONNECTION to a local test database.
dotnet restore OwnDay.slnx
dotnet build OwnDay.slnx --configuration Release --no-restore
dotnet test OwnDay.slnx --configuration Release --no-build -m:1
```

Local final verification: restore succeeded; Release build completed with zero warnings/errors;
609 tests passed (210 integration, 399 unit), with the opt-in real OpenAI smoke test skipped.
PostgreSQL migrations ran against isolated test schemas. `dotnet ef migrations has-pending-model-changes`
reported no differences, and `git diff --check` passed. Production schema/deployment was not changed.
