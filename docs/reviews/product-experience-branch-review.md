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

## Follow-up review through shared financial admission

Coverage extended through `0e90153` on 2026-11-12. The readiness P2 discovered by
subsequent regression is [documented with reproduction](../defects.md) and fixed
in `afe64ee`; its two red capability cases and seven subsequent focused checks
are recorded in the implementation log. The earlier no-findings assessment is
not evidence that the intermittent SQLite readiness case had already been proven.

Post-commit review of `afe64ee..0e90153` found no actionable remaining P0/P1/P2/P3.
Checked source exclusivity and ownership, global budget-lock ordering, UTC periods,
service/user caps, immutable price snapshots, unknown reservations, stale/late cost
reconciliation, physical-call scope lifetimes, local input rejection and guarded
migration rollback. The financial extension reuses the existing ledger and preserves
Inbox-only statistics. Shared admission is USD 10/month in runtime configuration;
it is not an OpenAI billing-account cap or an invoice guarantee.

Validation: full regression of 613 integration and 730 unit tests passed, one live
smoke skipped. After final source/local-admission guards and added cap scenarios,
28 focused integration and 35 unit checks passed with a clean Release build.
EF model/snapshot equality passed. No paid API call or production deployment ran.

## Current-exercise dialogue review

Reviewed `e563978` against parent `bcecc38`, extending the branch review through
the current implementation. No confirmed actionable P0/P1/P2/P3 findings remain.

Checked lazy owner/schema bootstrap, immutable pending values, question/source-order
binding, original-input resolution, bounded enum-only proposals, code consent with
expiry/parameters/target versions, atomic audit/reply publication and rollback.
Confirmed that expected-answer keys are absent from classifier input and that
reconciliation still accounts for stale classifier output. Explicit editor switches
invalidate pending dialogue while retaining accepted input; enrollment survives a
disabled producer flag and first-call in-flight processing. Old payload readers
remain available; rollout still requires compatible producers/consumers and drain.

Domain owns conversation invariants without Telegram/EF dependencies. Application
owns policy and returns typed decisions; the adapter renders copy and invokes the
same learning use cases as commands. No generic executor/framework or new subsystem
was introduced. The added context is separate from pedagogical LearningContext.
The small fixed operation mappings are transport dispatch, not duplicate use-case
rules. Clean Code review found no justified broad refactor within this scope.

Validation: clean restore/Release build, 632 integration and 754 unit tests passed;
one live smoke skipped. Migration from populated legacy state, model equality,
restart, stale consent, delayed messages and apply-commit rollback are covered.
Real classifier quality, Telegram Desktop/NVDA and production topology/backfill
remain unverified release gates. First-goal consultation and diagnostic acceptance
are not claimed by this review.
