# Telegram UX consolidation

## Audit and target model (recorded before implementation)

Source of truth: Telegram Commands, Routing, Handling, Localization, Delivery and Cleanup code,
Application Inbox draft/processing/automatic/statistics services, their tests, and M1–M8 documents.
M1–M4 describe historical analysis/policy capabilities; M5–M8 add automatic application, feedback,
Undo and accounting. In particular, confirmation remains a compatibility path, not the default.

### Existing interaction inventory and findings

| Surface | Existing commands / messages / scenarios | Problem and decision |
| --- | --- | --- |
| Entry | `/start`, `/help`, unknown command, `/ping` | Start only reports liveness; help omits AI, Undo and stats. Explain free input first; hide ping from discovery, retain compatibility. |
| Capture | Ordinary private text; captured, expired/unavailable draft notices | Capture tells users to process manually although background processing exists. Preserve source messages; acknowledge durable capture briefly. |
| Tasks | `/add text`, `/tasks`, `/done id`; validation, creation, empty/list, completed/already/not-found | Preserve deterministic creation and completion; add completion action to task rows. |
| Other records | `/project`, `/idea`, `/note`, `/wait` with text; `/projects`, `/ideas`, `/notes`, `/waiting`, `/list` | Preserve explicit creation; one list hub uses the same type names as results and manual processing. |
| Inbox | `/inbox [id]`, `/discard [id]`, `/cancel`; active/empty list, target/text/source/project/confirm prompts, all validation/expiration/race notices | Listing and starting an editor are conflated. Promote `/process id`; old `/inbox id` remains an alias. Add return, an explicit capture escape and a compact processing shortcut. |
| AI result | `/inbox_result id`; applied, failed, attention, original, manual/discarded result | Rename discovery to `/result id`; show readable current result, never policy/version/provider diagnostics. |
| Confirmation | `ia:y:uuid`, `ia:n:uuid`; `/inbox_accept uuid`, `/inbox_reject uuid`; stale/already/unavailable replies | Text equivalent requires a UUID that is not shown. Add `/accept inbox-id review`, `/reject inbox-id review`, bound to the exact offered assessment; keep old UUID commands/buttons. |
| Undo | `/inbox_undo id`, `iu:id`; success, repeated, conflict, unavailable, batch summary | Promote `/undo id`. Keep safety semantics and whole-batch warning; remove duplicate summary on success notifications. |
| Language | `/language`, 1/2 selection, changed/canceled/expired | Invalid free text is swallowed and two dialogs can coexist. Add `/settings`, `/language en\|ru`; ensure only one dialog, unexpected text returns to capture. |
| Statistics | `/stats [today\|7d\|30d\|all]`; all user and worker/token/cost accounting | Keep period calculations; short user report by default, full original accounting under `/stats [period] details`. |
| Transport | private text and two callback families; update deduplication, outbox, retries/cleanup, callback acknowledgement | Preserve transaction and owner checks. Do not delete source messages. Persist context buttons together with replies. |

The exhaustive pre-change EN/RU message-key catalog is in [Telegram UX inventory](Telegram-UX-Inventory.md).
This includes validation and empty/error responses, not only the happy path. Outbox cleanup deletes old
delivered database intents, not chat history; retain it. Delivery remains at least once.

### Target navigation and language

Primary command menu: `/list`, `/inbox`, `/stats`, `/settings`, `/help`, `/cancel`.
`/start` introduces the same model. No LLM is involved in navigation or command interpretation.

1. Send text → durable Inbox acknowledgement → one background result containing all created records.
2. `/list` → tasks, projects, ideas, notes, waiting. `/tasks` rows contain `/done id`.
3. `/inbox` → active originals, with `/result id` and `/process id` next to each entry.
4. `/result id` → current result and original. Automatic results offer `/undo id`; review offers
   `/accept id review` and `/reject id review`. The printed review number identifies the exact offer,
   so an old text command cannot accept a newer interpretation. `/process id` opens the manual editor.
5. `/process id task|project|idea|note|waiting [text]` explicitly creates a single manual result
   with the original or supplied text, no project/source. The existing editor supports these optional fields.
6. `/back` moves one step back inside the editor; at its first step it exits to Inbox.
   `/cancel` exits any selection, preserving originals. Navigation closes active selections.
7. `/capture text` always saves a new original, even while an editor expects text/source input.
8. `/settings` exposes `/language en`, `/language ru` and the existing language picker.
9. `/help` gives concise navigation; `/help commands` supplies the complete deterministic command reference.

Use plain text, result first, named actions, no decorative emoji. Small numeric IDs are user-facing
references for text actions; UUIDs, policy identifiers and diagnostic codes stay internal. Every inline
action has a visible command equivalent. Old callbacks remain guarded by the original durable checks;
new confirmation commands must identify the exact assessment, never silently accept a newer offer.

### Dialog policy

Only explicit manual editing consumes free text as edited text/source, with that expectation stated in
the prompt. In choice/confirmation steps, unrelated prose closes the selection and captures a new entry;
invalid numeric choices still explain the expected input. Language selection consumes only 1/2.
Navigation closes selections without extra cancellation notifications. `/cancel` clears both selections
even for legacy overlapping state and returns one answer. `/back` clears downstream choices.
Expired or overtaken drafts never silently lose a newly sent message.
Old confirmation/Undo buttons retain active selections when unavailable or already resolved. They do
not close a later language picker or reset a different manual editor. Read-only navigation buttons
follow the same selection-closing behavior as their printed commands.

Keep the detailed manual editor for project/source selection and final preview. The explicit typed
shortcut avoids a five-step wizard for the common unlinked record. Do not change Domain limits, policy,
analysis scheduling, automatic processing, Undo conflict rules or metric calculations.

## Examples: before and after

| Before | After |
| --- | --- |
| `/start` → “OwnDay is running. Use /help…” | `/start` → “Send a thought, task or note. OwnDay will sort it and show the result. /list … /inbox …” |
| Text → “Saved… Open /inbox to process it.” → verbose AutoApply + repeated target counts | Text → “Saved to inbox.” → “Created. Task: Buy milk. Details: /result 42. Undo all created records if unchanged: /undo 42.” |
| `/inbox 42` → type → text → project → yes | `/process 42 task` → “Created Task …”; optional fields remain available in the editor. |
| Language picker + unrelated thought → same picker, thought lost | Language picker + thought → new Inbox capture, picker closed. |
| `/stats` → worker errors, request retries, cost disclaimer | `/stats` → received, current outcomes, created records, automation; “Details: /stats 7d details”. |

## Compatibility and architecture

No command is physically removed. `/inbox_result` → `/result`, `/inbox_undo` → `/undo`,
`/inbox id` → `/process id` are discovery renames with working aliases. `/inbox_accept` and
`/inbox_reject` remain compatibility operations; `/ping` remains hidden diagnostics. `/discard` and
explicit record creation remain in the full reference, outside the main menu.
The separate language/editor cancellation messages and the unused editor cancellation renderer are
removed; one localized cancellation response covers both. Repeated target counts are removed from
automatic success notifications and retained in result details and the Undo outcome.

Presentation stays in Infrastructure. Router normalizes aliases and validates callback envelopes;
existing handlers own commands and rendering. Application owns draft step transitions and processing.
No universal command framework, schema change or new provider dependency is needed. Webhook replies
and their keyboards commit in the existing transaction alongside update identity and domain changes.
Background notifications still use the existing outbox and existing saved owner/locale destination.
Automatic interpretation still requires the existing configured analyzer and background processing;
this change does not enable a provider or supply credentials. With analysis disabled, captured entries
remain available through `/inbox` and manual processing.

## Command menu registration

There is no existing command registration worker. Use BotFather `/setcommands` for the deployed bot,
replacing the old menu (also remove obsolete per-language/scope overrides). Register these EN entries:

```text
list - Browse tasks and records
inbox - Review saved entries
stats - View statistics
settings - Language and settings
help - How to use OwnDay
cancel - Cancel current selection
```

The corresponding Russian menu text is:

```text
list - Задачи и записи
inbox - Входящие записи
stats - Статистика
settings - Язык и настройки
help - Как пользоваться OwnDay
cancel - Отменить текущий выбор
```

Registration is an operator deployment step, not performed against a live bot by repository tests.
For language-specific menus, call `setMyCommands` with `language_code` `en` and `ru`, plus the same
English commands for the default language (omit `language_code`). Use the six command/description
pairs above in the `commands` array and the private-chat scope:

```json
{
  "scope": { "type": "all_private_chats" },
  "language_code": "ru",
  "commands": [
    { "command": "list", "description": "Задачи и записи" },
    { "command": "inbox", "description": "Входящие записи" },
    { "command": "stats", "description": "Статистика" },
    { "command": "settings", "description": "Язык и настройки" },
    { "command": "help", "description": "Как пользоваться OwnDay" },
    { "command": "cancel", "description": "Отменить текущий выбор" }
  ]
}
```

Submit the JSON from a local operator file, with the bot token supplied only at runtime:

```powershell
$menuBody = Get-Content -LiteralPath ./commands.ru.json -Raw -Encoding utf8
Invoke-RestMethod -Method Post -ContentType 'application/json; charset=utf-8' `
    -Uri ('https://api.telegram.org/bot' + $env:TELEGRAM_BOT_TOKEN + '/setMyCommands') `
    -Body ([System.Text.Encoding]::UTF8.GetBytes($menuBody))
```

Verify default/EN/RU menus with `getMyCommands` for the same scopes. Clear obsolete narrower chat
overrides using `deleteMyCommands`, otherwise Telegram can continue showing a more specific old menu.
Telegram uses its client language for these menus; OwnDay's explicit `/language` setting controls replies.

### Russian dialog example

Before:

```text
Пользователь: Купить молоко
OwnDay: Сохранено во входящие. Откройте /inbox для обработки.
OwnDay: Входящие #42: создано.
        Задача #17: Купить молоко
        Результат и исходный текст: /inbox_result 42
        Отмена удалит все сущности этой обработки… /inbox_undo 42
        Затрагиваемых сущностей: 1. Задача: 1
```

After:

```text
Пользователь: Купить молоко
OwnDay: Сохранено во входящие.
OwnDay: Создано.
        Задача: Купить молоко
        Подробнее: /result 42
        Удалить все созданные записи, если они не изменились: /undo 42
        [Подробнее] [Отменить весь результат]
Пользователь: /undo 42
OwnDay: Результат отменён. Исходная запись возвращена во входящие. Разобрать вручную: /process 42
        Затронуто записей: 1. Задача: 1
Пользователь: /process 42 заметка Список покупок: молоко
OwnDay: Запись #42 разобрана. Создано: Заметка #18.
```

## Manual accessibility acceptance script

Run with both NVDA/Telegram Desktop and VoiceOver/Telegram iOS, in both English and Russian.
Record client/OS/screen-reader versions and actual results; automated text checks do not prove AT behavior.

1. Start a new chat, read `/start`, navigate the command menu by keyboard/rotor. All six actions have names.
2. Send a multi-record thought. Read capture and result in order; confirm source remains in history,
   each record is spoken, and no information requires emoji, color, position or markup.
3. Navigate result/Undo buttons and perform the same actions by the printed `/result` and `/undo` commands.
   Verify one whole batch is undone; repeat Undo and press an old button without creating/deleting records.
4. Open `/list`, each of the five lists and `/inbox`; follow the printed command for a record and task completion.
5. Open `/process id`; use choices, edit text, optional source/project, `/back` and `/cancel` at every step.
   Confirm no mutation until final confirmation, downstream choices reset, and original remains after cancellation.
6. Open language selection; send a thought, then `/language ru`, `/language en`. Check no thought is swallowed.
   Navigate away from a manual editor, then send a thought: it must become a new Inbox entry.
7. During a text/source prompt use `/capture another thought`; check the original and new text are preserved.
8. Read `/stats`, all four periods and `/stats all details`; check reading order and concise default.
9. Retry commands, use an expired/foreign confirmation and an already resolved button; check understandable
   responses, no private data, and no unexpected action. Test HTTP update redelivery automatically.

Actual NVDA/VoiceOver runs require those clients/devices and are not claimed by automated validation.

## Final verification

Architecture assessment: no outstanding critical or important findings in the final diff. Domain,
policy, statistics calculations, schema and project dependencies are unchanged. New draft navigation
stays in Application; Telegram rendering, aliases, callback envelopes and selection routing stay in
Infrastructure. Owner checks, webhook authentication, update deduplication and transactional outbox
are retained. No navigation invokes the analyzer or an external provider.

Executed locally:

```text
dotnet restore OwnDay.slnx
dotnet build OwnDay.slnx --configuration Release --no-restore -m:1
dotnet test OwnDay.slnx --configuration Release --no-build -m:1
git diff --check
```

Restore succeeded; Release build has zero warnings/errors. Final full suite: **745 passed**
(425 unit, 320 integration), **1 skipped** (the opt-in real OpenAI smoke test). Integration tests ran
against SQLite and the available local test PostgreSQL using isolated schemas and real migrations.
No production bot, provider or production database was contacted. Whitespace checks passed.

Coverage includes EN/RU navigation and text alternatives, source-message preservation, deterministic
creation of all five record types, shortcut rollback/retry, draft return/cancellation, unrelated prose,
language selection on first contact, overlapping legacy dialogs, overtaken/expired drafts, exact-offer
text confirmation, stale/foreign/repeated buttons, duplicate updates, concise notifications, result
aliases, Undo, statistics periods/accounting and restart/concurrency/transaction regressions.

Validation gaps: actual NVDA/VoiceOver interaction and live menu registration are operator/manual
steps described above. The existing at-least-once Telegram delivery limitation remains unchanged.
