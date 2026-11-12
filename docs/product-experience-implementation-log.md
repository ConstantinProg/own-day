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

Implementation: `141d56e` (parent `f86d3ed`). All ten final worker checks passed,
including preservation of the populated synchronous request; the test's initial raw
SQL JSON formatting error was corrected by parameterizing the JSON value.

Post-commit review of `f86d3ed..141d56e` found no actionable P0/P1/P2/P3 defects.
Architecture assessment: Application chooses the supported use case, Infrastructure
owns PostgreSQL claiming, transactional fencing, presentation and delivery intents;
the hosted adapter owns scoped lifetime and shutdown. No provider calls can run in
this lane. Reviewed row-lock skipping, head backoff, reclaim/apply races, final expiry
check, rollback, duplicate apply and cancellation against the PostgreSQL tests.

Validation gaps remain explicit: this is an isolated no-provider lane, not the S1.3
provider protocol or S1.5 public cutover. Production replicas/webhook settings, real
Telegram delivery and live provider behavior remain unverified. S1.2 is complete.

## S1.3 — investigation only

The [provider-path inventory](features/learning/provider-path-inventory.md) maps the
current command entry points, conditional external steps, mutations and apply guards.
No S1.3 production changes have been made. Prepared inputs/results, target stamps,
fresh-scope provider execution and the required blocked-provider matrix remain to be
implemented and verified. This is not a completed milestone or a verified blocker.

S1.3–S1.5 and S2–S11 remain unfinished. Their requirements/dependencies are unchanged.
The legacy long provider transaction is still present, and public early-ack remains
disabled. Later external acceptance also requires a test bot, approved finite live
budget, manual Telegram Desktop/NVDA verification and the plan's content/retention
decisions; none of those checks were performed or claimed as passed.

## S1.3 — prepare/provider/apply implementation

The previous investigation entry is historical. The disabled consumer now supports
every provider path in the inventory using the existing Application use cases,
immutable inputs and detached provider scopes. Preparations roll back provisional
state; final execution rechecks fingerprints and addressed PostgreSQL xmin stamps
in a fresh scope. Checkpoints persist explicit target keys/versions as well as the
stamp. Version 1 jointly identifies the current input/descriptor/prompt/schema
contract; changing that contract requires a compatible versioned reader.

The protocol is concrete and bounded (five provider kinds, at most planner plus
generator). It does not copy the services into a separate engine. Invitation start
selects its target program after successful material, preserving the old selection
and unresolved invitation on provider failure. Source reading is outside the DB
transaction, including the unavailable-source fallback.

Validation on a separate local PostgreSQL 18 (`ownday-product-tests`, port 55440,
test credentials, pooling disabled):

- Restore succeeds with `dotnet restore OwnDay.slnx --disable-parallel -m:1`.
  Default multi-node restore exited 1 without an MSBuild error; single-node restore
  completed normally. The initial test-first build likewise did not produce a
  completed red-test result; no red-test result is claimed.
- Release build succeeds with zero warnings/errors. The model/snapshot equality
  test passes. New schema is additive; legacy migration/restart tests run in the
  complete regression suite.
- Complete regression after the checkpoint/target schema: 549 integration tests
  passed, one opt-in live smoke skipped; 722 unit tests passed.
- The HTTP/hosted matrix blocks every inventory provider branch, including both
  Adapt phases, while a second connection acquires the same user lock. The adapter
  verifies no transaction, tracked entities or open DB connection. Target changes
  and pause/restore reject stale results. Source failure uses the real RFC reader
  and OpenAI adapter with fake HTTP, without real network/provider calls.
- An initial full run exposed missing xmin projections in raw Project/Action
  readers. Fixed all three affected queries; the repeated full regression passed.
  The failing run was stopped after diagnosis and is not counted as passed.

Public ingress is unchanged and the consumer remains disabled by default. Physical
attempt identity, unknown-result recovery and full crash/retry guarantees are S1.4;
public cutover is S1.5. No production deployment or live bot/provider test ran.
The user explicitly deferred test-bot smoke and live-budget agreement during this
session; those remain external checks and are not reported as passed.

Final focused verification after preserving invitation selection for non-material
successes and strengthening assertions about actual Domain effects: 42 integration
tests passed (all provider cases and Study regressions). Final Release build passed
with zero warnings/errors; `git diff --check` passed. Post-commit review follows.

Post-commit review of `f881c7b..5295961`:

- **P1 (Important), confirmed:**
  [SaveStepAsync](../src/OwnDay.Infrastructure/Persistence/EfLearningRequestStore.cs)
  renewed and checked ownership before writes, but a delayed checkpoint write could
  commit after the renewed lease expired. The expiry-during-write test returned true
  before the fix. Added a final database-clock/token/attempt check and atomic rollback.
- **P2 (Important), confirmed:**
  [FindActiveAsync](../src/OwnDay.Infrastructure/Persistence/EfLessonStore.cs)
  newly loaded every explanation to build target stamps. A long-running lesson
  could make even Pause/Resume and preparation unbounded. The large-history test
  loaded 25 rows before the fix. Load the latest ten immutable interactions; verify
  that appending another interaction still preserves all 26 persisted rows.
- **Architecture assessment:** dependency direction and Domain authority preserved;
  no provider or source I/O is available inside prepared apply. DTOs cross the
  provider boundary, while fresh scoped use cases own all mutations. Reviewed
  conditional planning, invitation selection, stamp identity and atomic effects.
- **Validation gaps:** public early-ack remains off. Full physical-attempt crash,
  cancellation-ignoring adapters and cost admission are subsequent explicit gates,
  not claimed as completed by this provider split. Live checks are deferred by user.

Both new review regressions were run before production fixes and failed for the
reported reasons. The follow-up Release build passes with zero warnings/errors.
Follow-up validation: 63 focused integration tests (provider matrix, worker, Learning
and Study) and all 722 unit tests passed; diff checks passed. Re-review of the final
fence and bounded history found no remaining P0/P1 or actionable P2. S1.3 is complete.

## S1.4 — physical attempts, failure recovery and delivery ordering

The consumer commits a technical attempt ID before each physical invocation.
Unfinished attempts prevent replacement on reclaim; late outcomes can reconcile
only invocation metadata. Logical deadlines finish with an explicit unknown-outcome
reply while an unresponsive task retains its scope/physical slot. The Host stops
provider admission until the task really completes. Reconciliation uses a separate,
bounded five-second technical cleanup token. No tracked Domain state is retained.

Known transient failures have at most `MaxProviderAttempts` (default 2, range 1–3)
physical calls per step. Auth/configuration, schema/refusal and other permanent
failures do not retry. SDK retries stay disabled. The generator's retries do not
repeat a saved teaching-plan result. This prepares identity for the shared ledger;
it is not an additional Learning cost ledger and makes no billing guarantee.

The additive migration creates invocation metadata/constraints, step constraints
and an active-head index. It backfills outbox order using the old `(created_at,id)`
policy, then supplies a database sequence for new messages. Reply order is allocated
before EF batches INSERTs and used for delivery/backoff. Rollback gaps are valid.
One active deliverer and private-chat/user lock ordering remain required.

Validation on isolated PostgreSQL 18:

- Both initial reclaim/ignored-cancellation tests failed before production changes.
- The clock-regression delivery test failed with reversed messages before the fix.
- Crash tests cover attempt admission commit failure, result commit failure (a new
  paid attempt is allowed), and apply commit failure (saved result reused on restart).
  Existing tests cover receipt/claim, stale ownership, duplicate apply and HTTP token
  independence. Each attempt has a unique durable ID before invocation.
- Known transient retries create distinct IDs; invalid response/permanent HTTP error
  do not retry. Unknown invocation closes admission until physical completion, with
  no late Domain effects. Populated legacy outbox upgrade preserves existing order
  and allocates later sequence values after a new context is created.
- A crash-test expectation of one message was corrected: the existing lesson presenter
  creates two intents (material and question). The test now verifies exactly ordinals
  0 and 1 for one request, rather than changing product behavior to match the test.
- Restore and Release build passed, zero warnings/errors; model/snapshot matches.
- Full regression: 562 integration tests passed, one opt-in live smoke skipped;
  728 unit tests passed. `git diff --check` passed.

No production deployment, live provider or bot test ran. Rehearse the outbox backfill
on production-size data before deployment. Public ingress remains disabled. Drain
accepted test/cohort work with a compatible reader before rollback; retain schema
and markers. Post-commit review follows before advancing to ingress cutover.

Post-commit review of `34b134d..f11e13d` completed on 2026-11-12. No actionable
P0/P1/P2/P3 findings were confirmed. Reviewed attempt admission/reconciliation,
unknown invocation recovery, checkpoint/apply fencing, bounded retry semantics,
outbox backfill and database ordering against the recovery tests.

Architecture assessment: dependency direction and existing Domain authority remain
intact; detached provider scopes hold no tracked Domain state or application
transaction. Late completion changes technical attempt metadata only.

Fresh validation: single-node restore and Release build passed with zero warnings
and errors; 47 PostgreSQL provider/review/worker tests passed on the isolated
PostgreSQL 18 at port 55440. The previous full-suite results above are historical,
not rerun results. Validation gaps: production-size backfill rehearsal, real bot,
provider and deployment topology remain unverified. S1.4 is complete locally;
the production cutover gate remains closed. Next implementation is S1.5.

## S1.5 — durable command ingress

All Learning commands share the durable owner sequence when explicitly enabled,
including preferences, schedules, selection, review and invitation commands. Receipt
commits versioned input, destination and dedup before HTTP 200. Existing Application
use cases execute through the prepared worker, without network calls in receipt or
apply. Payload v2 binds question-related input to the Lesson visible at receipt;
unbound/replaced targets produce clarification instead of assessing another question.
Version 1 remains readable. No schema or historical user rows are rewritten.

The producer stays disabled by default. Options reject producer-without-worker;
worker startup rejects missing migrations/non-PostgreSQL. Operators can pause new
acceptance while draining. An overdue backlog closes intake and fails readiness;
readiness JSON exposes pending count and oldest age. A disabled producer cannot
run synchronous commands over accepted pending work. Root engineering instructions
now distinguish receipt atomicity from effect/reply/completion atomicity.

Validation:

- Before production changes, all 32 initial ingress cases failed against the old
  synchronous behavior. Subsequent focused run: 51 passed, covering every provider
  branch through the public handler, receipt rollback/restart and stale Answer.
- Final Release build: zero warnings/errors. Full regression on isolated PostgreSQL
  18: 601 integration tests passed, one opt-in live smoke skipped; 728 unit tests
  passed. This includes pause/overdue backlog, disabled-writer bypass prevention,
  options and missing-migration gates, command journey and legacy regressions.
- An intermediate build found the health-check abstraction was not an Infrastructure
  dependency. Moved the operational check to Host; no package was added.
- `git diff --check` passed. Post-commit review follows.

Local implementation/automated acceptance is complete. Production enablement remains
conditional on test-bot smoke, verified single ingress/deliverer topology and an
outbox backfill rehearsal on production-size data. No deployment or paid API call ran.
These external checks are not claimed as passed.

The user approved a shared service budget of USD 10 per calendar month on 2026-11-12
and requested a review of all branch changes, including Clean Code. Use UTC month
boundaries and preserve reservations for unknown usage. This records the budget
decision; source-aware ledger implementation is the next change, not yet completed.

Implementation commit: `bb46aed` (parent `bbde307`). Post-commit review of that
diff and the complete `origin/main..bb46aed` branch found no confirmed P0/P1/P2.
A P3 Clean Code finding (duplicated lesson-command recognition/dispatch in the
Telegram handler) is addressed by reusing the existing shared handler. The
[full branch review](reviews/product-experience-branch-review.md) records rationale,
architecture assessment and validation gaps. Focused fix validation follows.

Cleanup validation: final Release build passed with zero warnings/errors; all 238
Learning/Curriculum/Study/review/personalization integration regressions passed.
Re-review of shared dispatch found no remaining actionable findings. S1.5 local
implementation is complete; the external rollout gates above remain open.

During S2's full regression, a readiness defect from S1.5 was found and recorded
in [defects](defects.md): the queue health check queried an unsupported SQLite
provider, including while disabled. Both new capability tests failed with a missing
queue-table error before the fix. Check provider capability first; retain pending
queue monitoring for disabled PostgreSQL workers. Release build and all seven
focused health/HTTP/backlog tests passed after the fix. Review of the narrow guard
found no further actionable findings; the earlier intermittent readiness 503 is
reported as an observation, not a proven production PostgreSQL failure.

## S2 — shared financial admission and monthly limits

Approved policy: USD 10 per UTC calendar month across Inbox and Learning. The
existing financial ledger now accepts a nullable Inbox source or a unique Learning
physical-attempt source, with an exactly-one-source check and owner validation.
Learning technical admission precedes financial admission; financial commit precedes
every provider invocation. Distinct retries reserve separately; saved results do
not call or charge again. No second Learning cost ledger was added.

The shared budget lock serializes service/user token, call and USD admission with
reconciliation. PostgreSQL supplies period/attempt timestamps. Configured prices
are snapshotted. Unknown usage/effective price retains the monetary reservation;
unknown requested price and unpriced current-month legacy consumption fail closed.
Current runtime settings replace the old lifetime cap with monthly caps, while the
legacy TokenLimit key remains available. Inbox statistics filter to Inbox sources.

Real Learning scopes capture usage before response validation and reconcile with
the bounded independent cleanup token, including stale/late outcomes. The adapter
requires admission before provider/source I/O, so a synchronous compatibility path
cannot bypass the shared budget. Paid Learning therefore requires completing the
documented durable-ingress rollout gates. Input/output bounds and reservation-size
validation prevent silently expanding individual requests. SDK retries remain off.

The additive migration retains historical rows/unknown costs, adds source/period
indexes and refuses destructive rollback over Learning history or unresolved
reservations. The [cost guide](features/learning/cost-control.md) documents exact
settings, price sources and the distinction between an application admission ceiling
and a provider invoice guarantee.

Validation so far:

- Four initial monthly-budget tests failed before implementation (12 concurrent
  requests admitted instead of two, unknown model price accepted, no monthly cap).
- Focused financial/provider run: 97 integration tests and 32 unit checks passed.
- An intermediate reconciliation expectation used two USD 5 reservations, leaving
  insufficient space for another USD 5 call even after a small known charge. The
  fixture was corrected to USD 4 reservations so it actually exercises released
  capacity; the admission arithmetic was retained.
- Source checks assert a durable financial row before every fake invocation.
  Tests cover shared concurrent Inbox/Learning admission, stale results, crash
  replay, ignored cancellation, exact usage/cached-price reconciliation, idempotent
  finish, restart, monthly rollover, legacy upgrade/unknown consumption and refusal
  to drop outstanding reservations. Inbox statistics remain source-specific.
- Restore and Release build passed with zero warnings/errors. Repeated complete
  regression: 613 integration tests passed, one live smoke skipped; 730 unit tests
  passed. The earlier run with one readiness failure is not counted as passed.
- EF model/snapshot equality passed through the repository's public API test.
  `dotnet ef` is not installed here; no CLI pending-model result is claimed.
- A final focused run follows the narrow guard moving admission before RFC source
  retrieval. No production deployment or paid provider invocation ran.

Final focused verification after the admission/source guard and known-zero local
rejections: Release build passed with zero warnings/errors; 28 integration and
35 unit checks passed, including concurrent service/user call and token caps.
`git diff --check` passed. Post-commit review follows before S3.

Implementation: `0e90153` (parent `afe64ee`). Post-commit review of that diff found
no remaining actionable P0/P1/P2/P3 findings. Reviewed shared-lock admission and
reconciliation, source ownership/FKs, monthly boundaries, price snapshots, unknown
reservations, bounded cleanup, scope lifetimes, source I/O admission guard, input
bounds and migration rollback rejection. Architecture and existing Domain authority
are preserved; financial failure cannot permit a provider call or a Domain bypass.
The earlier readiness P2 is fixed in `afe64ee` and remains documented separately.

S2 is complete locally. No billing-account cap, production deployment, live provider
quality or test-bot check is claimed. The next dependent implementation is S3.

## S3 — current-exercise dialogue

Added separate versioned owner DialogueContext with optimistic revision/xmin,
question binding, source-order boundaries and bounded pending values. No change to
LearningContext's required ProgramId or pedagogical authority. Natural text v1 and
command v3 drain through the existing queue, prepare/provider/apply and shared
command/Application use cases. Migration is additive and rejects destructive
downgrade over dialogue state or pending inputs; legacy bootstrap invents no program.

Deterministic controls precede one enum-only classifier proposal. The proposal has
bounded context without answer keys, local strict validation and financial admission.
Clarification preserves the question and does not assess; an unresolved choice
persists the original request, which alone can later be assessed. Consequential
changes require a fresh code tied to exact parameters/versions, never bare consent.
Audit and reply publication are atomic with effects. Pause/restart/resume retains
the actual Lesson without generation. Explicit editor transitions invalidate pending
dialogue; enrollment remains sticky after the flag is disabled or during first-call
in-flight processing. The [dialogue guide](features/learning/dialogue.md) documents
supported contracts and compatible rollback.

Evidence before final verification:

- The first two natural-input PostgreSQL tests failed against the prior Inbox path.
- Incremental runs reached 12 new PostgreSQL dialogue cases and 29 contract/unit
  checks passing, including English question-as-answer, original-input resolution,
  restart, stale/foreign/versioned consent and source-order rejection.
- Added atomic publication rollback/recovery, populated legacy upgrade and sticky
  enrollment tests. Full run: 631 integration passed, one failed, one live skipped;
  all 753 unit checks passed. The failure was the new legacy SQL fixture interpreting
  JSON braces as composite-format placeholders, before exercising the migration.
- Broad provider regression reproduced that same single fixture failure (94 passed,
  one failed). Replaced the embedded JSON literal with an SQL parameter. Added a
  monotonic source-boundary regression and reran restore/Release/full tests. Results
  follow below; earlier failed runs are not counted as accepted verification.

Real model classification quality and keyboard/NVDA acceptance remain external
gates. No paid invocation, test-bot check, rollout or production deployment occurred.

Final restore and Release build passed with zero warnings/errors. Complete
regression passed: 632 integration tests, one live smoke skipped, 754 unit tests.
The legacy migration fixture uses a parameter explicitly cast to jsonb; an
intermediate run was cancelled before acceptance to include that correction.
EF model/snapshot equality is included. Local acceptance criteria are met;
post-commit review follows before advancing.

Implementation `e563978` reviewed against `bcecc38`: no confirmed remaining
P0/P1/P2/P3 findings; the full branch review includes the new dialogue scope and
Clean Code assessment. S3 is complete locally with the external gates retained.
Next: S4 consultation and practical first goal.

## S4 — first goal and practical context

The initial direct-goal acceptance test failed before implementation: the existing
exercise-only dialogue did not ask about practical use. Added a bounded version-1
consultation draft inside dialogue schema 2, two deterministic context questions,
and typed first-program creation through the existing CurriculumService with no
Project link. Original goal is not rewritten; use and experience remain explicit
self-report. No full route approval or second planning engine was introduced.

The draft survives restart, Start/help, explicit capture, pause/cancel and flag
rollback. Incoming detail must address the published prompt and follow its source
order. Accepted inputs retain consultation capability independently of the live
producer flag, including the first request before a context exists. Failure retains
details and requires explicit retry; resume alone never retries a paid operation.
Existing selected programs and active Lessons cannot be replaced by consultation.

Schema migration preserves known exercise contexts and pending confirmations,
increments only the supported schema version and retains unknown versions. Drafts
block destructive downgrade. [The first-goal guide](features/learning/first-goal.md)
documents this behavior and the separate diagnostic capability/release gates.

Incremental validation: clean Release build; six integration and 28 unit/model
checks passed. Added populated schema-1 confirmation upgrade and pre-worker flag
rollback/premature-detail coverage. Final restore/Release/full regression is running;
results and post-commit review follow before advancing. No live call/deployment ran.

Full regression: 638 integration and 754 unit tests passed, one live smoke skipped;
restore and Release build passed with zero warnings/errors. A final focused build/run
includes the two additional schema-safety cases, the terminal-handler unknown-draft
guard and the final user-facing wording. No additional full run is claimed for
those narrow follow-ups; their focused results follow.

Final focused validation passed: Release build without warnings/errors, ten
integration and 28 unit/model checks passed. Both downgrade rejection and unknown
draft version guards preserve data; populated schema upgrade preserves consent.
`git diff --check` passed. Local acceptance is ready for commit and review.
