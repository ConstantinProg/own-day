# Intelligent Inbox observability and cost control

OwnDay exposes personal Inbox statistics through `/stats`, `/stats today`, `/stats 30d`, and `/stats all`.
No monitoring server, analytics service, new production package, or mandatory CLI is required.
English and Russian use the existing language preference and Telegram resources. Reports are plain text,
without tables, emoji, markup, original entry text, proposal text, or technical identifiers.

## Periods and metric definitions

All periods use **UTC**, independently of the server OS, Telegram locale, or daylight saving changes.
One clock reading fixes the report end. Every event range is `[start, end)`, with an inclusive start and
exclusive end. Today starts at 00:00 UTC; the default starts six calendar days before today;
30d starts 29 calendar days before today. Both include the elapsed part of today. All time has no effective
lower bound. These are calendar periods, not sliding 168/720-hour windows.

PostgreSQL reports read a separate Repeatable Read snapshot, including when invoked inside the webhook's
transaction. Only the authenticated sender's data is queried. The explicitly labelled service-wide error
and delivery retry counters contain counts only, without other users' identities or content.

| Metric | Unit, attribution, denominator |
| --- | --- |
| Received | Distinct Inbox rows, `CapturedAt` in the period. Telegram retries retain one row. |
| Current results | Processed, Active, Discarded states of that received cohort at the read snapshot; these sum to Received. Undo changes the current result, preserving historical application facts. |
| Capture-to-processing speed | Mean `ProcessedAt - CapturedAt` among currently Processed rows in that received cohort. Includes user waiting time and retries. Empty cohort means unknown, not zero. |
| AutoApply | Distinct Inbox IDs with a committed AutoApply audit whose `EvaluatedAt` is in the period. A composite batch is one entry; Undo does not subtract it. |
| Required review | Distinct Inbox IDs with a non-AutoApply assessment evaluated in the period. Inactive and superseded assessments are excluded. Includes failed/invalid analysis and manual-only guards. An entry can occur in both historical AutoApply and review counters. These counters are not a partition of Received. |
| Created entities | Committed Inbox target creation facts, by the five domain kinds and creation event time. Counts entities, not proposals; a composite can create several of a type. Other `/add` commands and pre-existing linked projects are excluded. Undo does not subtract creations. |
| Manual decisions | Successful manual Inbox processing events plus confirmation resolutions by `ResolvedAt` (accept/reject/stale). A repeated processed command or resolved confirmation adds nothing. Opening a dialog, viewing a result, editing draft text, and canceling a draft are not completed manual decisions. |
| Undo | Committed application `UndoneAt` in the period; each application once. |
| Refused/repeated Undo | Actual calls returning Conflict, Unavailable or AlreadyUndone, by event time. A new user command is another refusal attempt; retransmission of the same Telegram update is not. Unavailable includes missing/foreign IDs and discloses no ownership. |
| AI success/failure | Logical analysis rows completed in the period (`CompletedAt`), including explicit operator versions. Completed is success; Failed and InvalidResponse are unsuccessful. Pending is excluded. A retry retains its analysis ID/version. |
| AI success percentage | `100 * successful / (successful + unsuccessful)`; unknown when the denominator is zero. No quality/confidence score is invented. |
| Mean AI time | Mean `CompletedAt - CreatedAt` of successful logical analyses completed in the period. Includes retry backoff/recovery; it is not HTTP latency. |
| Requests and provider retries | Metered provider admissions by `StartedAt`; each invocation has a new UUID. Sequence > 1 for the same logical analysis is a retry, even if the first request was in another period. |
| Provider errors | Finished metered attempts with a safe failure code, including refusal/incomplete output. A returned response with invalid proposal JSON is a failed logical analysis, not a provider transport error. Pending/unknown attempts are shown through incomplete usage. |
| Background job retries | Successful lease claims with attempt > 1; fact and claim commit atomically. Includes crash recovery and claims that never reach OpenAI, distinct from provider retries. |
| Background errors | Failed job outcomes (including retryable failures, timeout, invalid output and budget refusal) committed with the fenced job completion, plus recorded unexpected analysis/apply-cycle failures for this user. Apply/recovery failures without a user context are labelled service-wide. Provider errors are also shown above as a separate overlapping metric. |
| Delivery retries/errors | Service-wide outbox delivery attempts after an earlier failure and actual send failures. Their facts survive sent-message cleanup. |
| Pending / overdue now | Current gauges, independent of the selected historical period. Pending counts logical pending analyses. Overdue jobs have an expired Running lease, or Ready availability more than ten minutes overdue. Disabled backlog can legitimately be overdue. |

Event totals and capture-cohort totals intentionally answer different questions: an entry captured last month can
be processed this week. Proposal count is never used as an entry count or as an entity-creation count.

## OpenAI usage and prices

The production analyzer is wrapped by `CostControlledInboxAnalyzer`. `openai_request_attempts` commits one
reservation before each SDK invocation, independently of the analysis/job transaction. The official SDK's
automatic retries remain disabled. The journal retains analysis ID, owner, request sequence, requested/actual
model, provider request ID, timestamps, safe outcome, nullable input/cached-input/output usage, reserved and
accounted tokens, nullable per-model price snapshot, and nullable estimated USD. No prompts or responses are
copied into this journal. Refusal and incomplete completions preserve API usage too.

The adapter uses the existing Chat Completions SDK usage fields, as documented in the
[official API reference](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create).
Input tokens **include** cached input. Reasoning tokens already belong to the API's output count; they are
not added a second time. For explicitly configured USD-per-million prices:

```text
estimated USD = ((input - cached) * input rate
                 + cached * cached-input rate
                 + output * output rate) / 1,000,000
```

All three token values and all three rates must be known and nonnegative, and cached cannot exceed input.
Otherwise cost stays null. An absent cached count is not assumed to be zero. Prices match the **exact actual
returned model ID**, case-sensitively, without guessing aliases/snapshot prices. Updating configuration does not
reprice old journal rows. Unknown rates do not disable token admission controls.

Reports show known token/cost subtotals with a count of incomplete observations. The estimate is neither an
invoice nor actual OpenAI debits; it excludes unknown usage/pricing, provider credits, taxes and price changes
outside the configured snapshot. Empty request sets have zero observed usage; unfinished requests have unknown
usage. A crash, cancellation, timeout, or lost response can leave permanently unknown usage.

## Token admission budget

Defaults in `appsettings.json`:

```json
"CostControl": {
  "TokenLimit": 1000000,
  "TokensPerRequest": 100000,
  "Models": {}
}
```

These keys bind under `InboxAnalysis:CostControl` (environment prefix `InboxAnalysis__CostControl__`).
`TokenLimit` is a **global lifetime admission budget in this database from deployment of the journal**, across
users, models, application instances, explicit analysis, and background requests. It has no automatic daily/monthly
reset. Set it to null only to explicitly disable admission limits. Positive limits/reservations and nonnegative
prices are validated at startup. All instances must use the same configuration.

Before a call, a short PostgreSQL transaction takes a dedicated negative advisory lock, sums accounted tokens,
checks sufficient headroom for the whole reservation, inserts it, and commits. No lock/transaction spans OpenAI I/O.
Reconciliation uses the same lock and changes a finished attempt once. Known input + output replaces its reservation;
cached tokens remain part of input and are not counted twice. Missing input/output or interrupted requests retain
the **entire reservation**, including after restart. A late response cannot release another attempt's reservation.
The ledger validates analysis ownership before admission.

`TokensPerRequest` is a conservative operator-chosen reservation, **not an API token cap or a tokenizer**.
Keep it above expected input + maximum output, allowing for project context, system prompt, schema and protocol
overhead. The default is 100,000. Actual usage can exceed a configured reservation; in-flight requests can therefore
overshoot the token threshold. Once accounted tokens reach the threshold, no new metered request starts. Admission
also blocks earlier if remaining headroom cannot fit a reservation. This is not an absolute monetary ceiling.

A budget refusal produces the existing `budget_limit` terminal analysis/job outcome, no API call, no automatic
retry, and no deletion of the saved active Inbox item. Existing manual Inbox processing remains available through
Telegram. Raising the lifetime limit/restarting does not automatically replay terminal failed analyses; a deliberate
new analysis version can use the existing operator use case. Untouched Ready jobs can run after capacity is added.
No separate CLI is needed to read statistics or manually process preserved entries.

Do not automatically release old pending reservations: a request may have been charged before the crash.
After checking provider billing, an operator can conservatively raise the lifetime limit through runtime
configuration. Clearing the journal or decreasing unknown reservations would lose evidence; no automatic reset
or such repair command is exposed.

Prices are intentionally absent by default. Supply verified prices at runtime, for example:

```json
"Models": {
  "your-exact-returned-model-id": {
    "InputUsdPerMillion": 2,
    "CachedInputUsdPerMillion": 0.5,
    "OutputUsdPerMillion": 8
  }
}
```

These numbers illustrate configuration and are **not a statement of any model's current prices**.

## Persistence, migration and diagnostics

Apply `20261025193822_AddInboxObservability` through the existing deployment migration bundle/EF procedure.
It adds only two PostgreSQL tables and their indexes. `inbox_operation_events` holds small append-only creation,
manual-decision, refusal, retry and error facts, plus the deployment coverage timestamp. Creation facts join the
existing Inbox lock/transaction and outbox commit; a `(kind, targetId)` unique filtered index protects identity.
Successful AutoApply, confirmations and Undo continue using their existing durable audit.

The migration backfills creation facts from current Inbox targets and original/confirmed application mappings,
including mappings already undone. It backfills identifiable current manual completions. It does not fabricate
provider attempts, old Undo refusals, or old background errors. The report explicitly identifies when detailed event
accounting started. Past refused Undo/error counts are incomplete, not evidence of zero incidents.
Backfilled creation timestamps use application `EvaluatedAt`, confirmation `ResolvedAt`, or current manual
`ProcessedAt`; these are the available transaction-level evidence, not reconstructed HTTP request timestamps.

Existing analysis diagnostics can contain the final response's input/output usage (and sometimes cached usage).
Reports expose these separately for analyses without a request journal, attributed by analysis `CreatedAt`.
They do not pretend to reconstruct lost retries, add those observations to request totals, or price them with today's
rates. Analyses without a journal also include pre-call validation/budget rejection and pending analyses; their
existence is not evidence that a paid request occurred. Historical usage is not silently charged against the new budget.

Structured Serilog events correlate job, Inbox, analysis and request IDs, attempt/sequence, safe outcome, token usage
and estimated USD. Every minute the existing recovery loop logs up to 50 overdue jobs and 50 unresolved requests
older than ten minutes, including lease/last code/reservation. Repeated job outcomes correlate on job/analysis IDs;
request-level errors correlate on analysis ID. Logs contain neither user texts, complete AI output, secrets, nor
provider exception bodies. Telegram delivery stores/logs exception type instead of raw exception messages.

Unexpected worker errors are persisted best-effort in an independent scope after rollback. During a database outage
only the safe structured log can be emitted; the durable error counter is necessarily a lower bound. Provider usage
can also remain unknown if persisting it fails. The existing lease recovery continues work and keeps the budget reservation.
No new history retention cleanup is enabled for the ledger/facts: losing them would reset lifetime cost evidence.

## Validation

Tests cover UTC boundaries/year changes, all four Telegram periods in both locales, authentication and owner
isolation, duplicate updates, successful/failed analyses, all five creation kinds, AutoApply replay, Undo and manual
correction across scopes, legacy backfill including deleted targets, unknown prices and missing cached usage,
cached-price calculation, concurrent budget reservations, concurrent duplicate completion, failed/refused/incomplete
requests, cancellation and restart, and budget exhaustion preserving the Inbox. The real OpenAI smoke test remains
explicitly opt-in. Use an isolated PostgreSQL database; no production secrets or live Telegram API are required.

```powershell
dotnet restore OwnDay.slnx
dotnet build OwnDay.slnx --configuration Release --no-restore -m:1
# Set OWNDAY_TEST_POSTGRES_CONNECTION to an isolated test database.
dotnet test OwnDay.slnx --configuration Release --no-build -m:1
```

For a local PostgreSQL with a small connection limit, use `Pooling=false` in the **test** connection string;
concurrency tests still open independent real PostgreSQL connections.

## Example Telegram reports

Illustrative data, not measurements of a real user. Pending and overdue are current gauges and remain the same
across periods. These examples assume complete request accounting and configured prices; actual reports mark unknowns.

`/stats`

```text
Статистика Inbox: последние 7 дней. Часовой пояс: UTC.
Получено: 12. Текущий результат: обработано 9, активно 2, удалено из Inbox 1.
Среднее время от получения до обработки полученных записей: 20 с.
Записей AutoApply: 8. Требовали проверки: 3.
Создано из Inbox: задач 6, проектов 1, ожиданий 1, заметок 3, идей 1.
Undo: 1. Отказов и повторов: 1. Ручных решений: 2.
AI-анализы: успешных 10, неуспешных 1; успешность 90,9%. Среднее время успешного анализа: 4,2 с.
Запросов OpenAI: 12, повторных 1, ошибок 2. Ожидающих анализов сейчас: 1. Просроченных заданий сейчас: 0.
Повторов фоновых заданий: 1, ошибок: 2. Общих фоновых ошибок сервиса: 0, повторов доставки: 1.
Токены: вход 12000, из них кеш 4000; выход 2400.
Оценочная стоимость: 0,0372 USD. Это не сумма фактического списания.
Анализов без учёта запросов: 0; дополнительный расход и стоимость неизвестны.
Подробный учёт событий с 2026-10-01 00:00 UTC; более ранние отказы Undo, фоновые повторы и ошибки неизвестны.
Записи — по дате получения; остальные счётчики — по дате события. Успешность: успешные / завершённые анализы. Календарные дни UTC, включая сегодня.
```

`/stats today`

```text
Статистика Inbox: сегодня. Часовой пояс: UTC.
Получено: 3. Текущий результат: обработано 2, активно 1, удалено из Inbox 0.
Среднее время от получения до обработки полученных записей: 5 с.
Записей AutoApply: 2. Требовали проверки: 0.
Создано из Inbox: задач 1, проектов 0, ожиданий 0, заметок 1, идей 0.
Undo: 0. Отказов и повторов: 0. Ручных решений: 0.
AI-анализы: успешных 2, неуспешных 0; успешность 100%. Среднее время успешного анализа: 3 с.
Запросов OpenAI: 2, повторных 0, ошибок 0. Ожидающих анализов сейчас: 1. Просроченных заданий сейчас: 0.
Повторов фоновых заданий: 0, ошибок: 0. Общих фоновых ошибок сервиса: 0, повторов доставки: 0.
Токены: вход 2000, из них кеш 1000; выход 400.
Оценочная стоимость: 0,0057 USD. Это не сумма фактического списания.
Анализов без учёта запросов: 0; дополнительный расход и стоимость неизвестны.
Подробный учёт событий с 2026-10-01 00:00 UTC; более ранние отказы Undo, фоновые повторы и ошибки неизвестны.
Записи — по дате получения; остальные счётчики — по дате события. Успешность: успешные / завершённые анализы. Календарные дни UTC, включая сегодня.
```

`/stats 30d`

```text
Статистика Inbox: последние 30 дней. Часовой пояс: UTC.
Получено: 50. Текущий результат: обработано 43, активно 5, удалено из Inbox 2.
Среднее время от получения до обработки полученных записей: 42 с.
Записей AutoApply: 40. Требовали проверки: 8.
Создано из Inbox: задач 25, проектов 4, ожиданий 3, заметок 12, идей 5.
Undo: 3. Отказов и повторов: 2. Ручных решений: 6.
AI-анализы: успешных 46, неуспешных 3; успешность 93,9%. Среднее время успешного анализа: 4,5 с.
Запросов OpenAI: 53, повторных 4, ошибок 6. Ожидающих анализов сейчас: 1. Просроченных заданий сейчас: 0.
Повторов фоновых заданий: 4, ошибок: 7. Общих фоновых ошибок сервиса: 1, повторов доставки: 3.
Токены: вход 53000, из них кеш 20000; выход 10000.
Оценочная стоимость: 0,156 USD. Это не сумма фактического списания.
Анализов без учёта запросов: 0; дополнительный расход и стоимость неизвестны.
Подробный учёт событий с 2026-10-01 00:00 UTC; более ранние отказы Undo, фоновые повторы и ошибки неизвестны.
Записи — по дате получения; остальные счётчики — по дате события. Успешность: успешные / завершённые анализы. Календарные дни UTC, включая сегодня.
```

`/stats all`

```text
Статистика Inbox: всё время. Часовой пояс: UTC.
Получено: 120. Текущий результат: обработано 108, активно 8, удалено из Inbox 4.
Среднее время от получения до обработки полученных записей: 60 с.
Записей AutoApply: 100. Требовали проверки: 17.
Создано из Inbox: задач 62, проектов 9, ожиданий 8, заметок 31, идей 12.
Undo: 6. Отказов и повторов: 4. Ручных решений: 14.
AI-анализы: успешных 111, неуспешных 8; успешность 93,3%. Среднее время успешного анализа: 4,8 с.
Запросов OpenAI: 130, повторных 10, ошибок 15. Ожидающих анализов сейчас: 1. Просроченных заданий сейчас: 0.
Повторов фоновых заданий: 11, ошибок: 19. Общих фоновых ошибок сервиса: 2, повторов доставки: 5.
Токены: вход 130000, из них кеш 50000; выход 25000.
Оценочная стоимость: 0,385 USD. Это не сумма фактического списания.
Анализов без учёта запросов: 0; дополнительный расход и стоимость неизвестны.
Подробный учёт событий с 2026-10-01 00:00 UTC; более ранние отказы Undo, фоновые повторы и ошибки неизвестны.
Записи — по дате получения; остальные счётчики — по дате события. Успешность: успешные / завершённые анализы. Календарные дни UTC, включая сегодня.
```

With unknown prices a cost line instead reads, for example:
`Оценочная стоимость: 0,0057 известно; для 2 запросов неизвестно USD. Это не сумма фактического списания.`
Missing token counts use the same explicit known-subtotal/unknown-count wording.
