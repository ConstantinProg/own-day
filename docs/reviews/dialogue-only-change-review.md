# Dialogue-only Telegram change

The transport always accepts ordinary text into the existing durable learning
lane. Legacy Inbox/productivity commands and callbacks return dialogue guidance;
they cannot mutate Inbox or create productivity records. Legacy editors are
closed in the receipt transaction. Existing records are retained. Inbox
background analysis is disabled; previously committed shared outbox intents are
still delivered. Production Compose enables the compatible worker and ingress.

Dependency direction, authenticated webhook handling, owner serialization,
receipt deduplication and the worker apply/outbox boundary are preserved. No new
domain policy or persistence model was introduced.

## Validation and outstanding work

- Restore and Release build passed; zero warnings/errors. `git diff --check`
  passed.
- Sixteen new SQLite HTTP cases passed: default enrollment, duplicate receipt,
  editor retirement, old commands/buttons, paused acceptance and failed receipt
  rollback/redelivery.
- On an isolated PostgreSQL 18, 32 existing selected command/clarification/pause
  cases passed. The restart test initially failed because it explicitly disabled
  dialogue. Its setup now keeps the mandatory mode enabled; the restart test
  and all sixteen new HTTP cases passed together (17/17) on the final build.
- The initial full unfiltered run was **not green**: unit 754 passed / 28 failed;
  integration 100 passed / 591 failed / 2 live skipped. PostgreSQL connection was
  not configured for that run. Remaining suites also assert removed Inbox UI,
  synchronous command execution, or disabled-mode compatibility. These suites
  still need migration; the new focused cases do not establish full regression
  acceptance. Do not treat this change as release-verified.
- No live model, Telegram/NVDA, production deployment or production topology
  verification was performed. No commit was created.
