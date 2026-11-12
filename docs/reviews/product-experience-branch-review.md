# Product experience branch review

Reviewed range: `0a4aa02` (`origin/main`) through `bb46aed`, plus the focused fix
described below. Date: 2026-11-12. Scope includes all branch production changes,
migrations, relevant tests, deployment instructions and Clean Code. Earlier reviews
and fixed P1/P2 findings remain in the [implementation log](../product-experience-implementation-log.md).

## Findings

**P3 — duplicated Learning command dispatch and recognition, fixed.**
In `bb46aed`, [TelegramUpdateHandler](../../src/OwnDay.Infrastructure/Telegram/Handling/TelegramUpdateHandler.cs)
repeated the lesson-command cases and use-case calls already owned by
[TelegramLearningCommandHandler](../../src/OwnDay.Infrastructure/Telegram/Commands/TelegramLearningCommandHandler.cs).
This required maintaining both command inventories when adding a lesson operation
and could make the compatibility route diverge from queued execution. Reused the
existing shared handler for recognition and synchronous dispatch. Constructor
compatibility is retained; no new abstraction, dependency or behavior was added.

No confirmed P0/P1/P2 defects remain in the reviewed code. The absence of complete
Learning financial accounting is the explicit scope of the next approved change,
not a claim that the current technical attempt table is a cost ledger.

## Architecture assessment

Project dependency direction is preserved. Domain still owns pedagogical
transitions; existing Application use cases are reused. PostgreSQL owns durable
requests, technical attempts, target stamps and atomic effects/replies. The
provider path releases preparation transactions, tracked state and user locks.
The Host owns health/configuration/HTTP concerns. LLM outputs remain proposals.

Reviewed source dedup, sequence allocation, head-only claim, user/request lock
order, lease fencing before final writes, checkpoint replay, two-step Adapt,
unknown invocation recovery, bounded physical calls, stale Answer rejection,
legacy row preservation and database-backed outbox ordering. The code makes no
provider or Telegram exactly-once billing/delivery claim. Logging adds technical
identifiers/outcomes without prompts, answer text or credentials. Clean Code
assessment found no need for a framework, new project or broad refactoring.

## Validation and deployment gaps

Fresh complete validation before the focused dispatch cleanup: Release build with
zero warnings/errors, 601 integration tests passed on isolated PostgreSQL 18,
one live smoke skipped, 728 unit tests passed. The initial 32 new receipt tests
failed against the old synchronous implementation. The implementation log records
the focused verification and re-review of the cleanup.

Public ingress is still opt-in. Test-bot smoke, actual single-ingress/single-deliverer
topology, production-size outbox backfill rehearsal and live provider behavior
were not verified. USD 10/month is approved, but not enforced by S1 technical
attempts; source-aware shared financial admission is S2. Dialogue/content/manual
accessibility acceptance belongs to subsequent requirements and is not claimed
by this infrastructure review.
