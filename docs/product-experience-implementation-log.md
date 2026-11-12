# Product experience implementation log

Requirements remain in [the approved plan](product-experience-refactoring-plan.md).
The plan and `PostgresLearningRequestTests.cs` were untracked when work started;
their original content is excluded from implementation commits.

## S1.1

Implementation: `dd7c2e2` (parent `0a4aa02`). Synchronous terminal request persistence,
versioned command input, source and owner/sequence uniqueness, nullable outbox links
and unique reply ordinals. No early HTTP acknowledgement or Ready producer.

Validation on isolated PostgreSQL 18 with pooling disabled:

- Restore succeeded. Release build succeeded with zero warnings/errors.
- Initial six supplied acceptance tests failed because the request table was absent.
- Nine focused tests passed after implementation (six supplied, three tracked).
- Full integration regression: 511 passed, one live OpenAI smoke skipped.
- Final unit suite: 719 passed. EF reports no pending model changes.
- A build attempted during the regression run failed due to Windows testhost DLL
  locks; the sequential final Release build succeeded.
- Snapshot trimming initially broke model equality; the corrected snapshot passed
  both model equality tests and the EF pending-change check.

Post-commit review of `0a4aa02..dd7c2e2`:

- **P2 (Important), fixed in follow-up:**
  [tracked acceptance tests](../tests/OwnDay.IntegrationTests/Telegram/PostgresLearningReceiptTests.cs)
  covered concurrent receipt/sequence and commit failure, but migration, blocked-provider
  HTTP timing and cancellation checks lived only in the supplied untracked file.
  Clean checkout would omit those required regressions. Added independent tracked
  tests for these boundaries; no production behavior changes were needed.
- **Architecture assessment:** dependency direction preserved; Domain rules and existing
  command services unchanged. Receipt/effect/outbox remain atomic. No P0/P1 found.
- **Validation gaps:** production topology and a production-size migration rehearsal
  are unverified. Provider latency still holds the legacy transaction as explicitly
  permitted for this step. No deployment, real bot calls or live provider smoke ran.

Rollout: apply additive schema first, then compatible synchronous Host; rollback code
without dropping data. Public background ingress remains disabled.

Follow-up acceptance commit: `f86d3ed`. All six tracked receipt tests passed after the
fix; re-review found no remaining blocking findings. S1.1 is complete.

## S1.2

The no-provider lane processes Pause/Resume/Finish through existing Application use
cases. Public commands stay synchronous; the worker is disabled by default. PostgreSQL
head claim, database-clock leases, ownership fencing, unique operation markers and
effect/reply/completion atomicity are implemented. Ready/backoff and Running heads
block later dependent work. Different owners can execute concurrently.

Validation:

- Release build: zero warnings/errors. EF reports no pending model changes.
- Full regression: 524 integration and 719 unit tests passed; one live smoke skipped.
- Worker tests cover two workers, independent owners, backed-off/Running head, expired
  ownership and duplicate apply, poison/exhausted requests, commit rollback, expiry
  during mutation, HTTP receipt followed by Host restart and HTTP token cancellation.
- An additional migration check preserves a completed synchronous request without
  scheduling it or inventing an operation marker.
- Tests explicitly re-enable the hosted lane removed by `OwnDayHostFactory`; a
  test-only HTTP producer only accepts the supported no-provider operations.

Deployment remains schema-first with the worker/public ingress disabled. No mixed
synchronous/queued writers for one production owner are permitted. Drain accepted
cohort/test requests with a compatible worker before code rollback; never drop data.
