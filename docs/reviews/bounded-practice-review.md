# Bounded practice review

Reviewed `637d362` against parent `8f7c1b2` after commit, including the separate
design, all new files, common-command policy, migration and transaction boundaries.

## Important — P2: late next can consume a future practice slot

[DialogueContext.Synchronize](../../src/OwnDay.Domain/Dialogue/DialogueContext.cs)
reset the source boundary to zero when an answer removed the current active Lesson.
[LearningDialogueService](../../src/OwnDay.Application/Dialogue/LearningDialogueService.cs)
routed practice controls without checking that their source followed the completed
answer. A delayed older “дальше”, delivered after a newer answer, could therefore
issue and pay for a new practice Lesson without a fresh request for that state.

The PostgreSQL `Post_LateNextOlderThanCompletedAnswer_DoesNotGenerateUntilFreshRequest`
failed against the implementation commit: two Lessons existed instead of one after
the late message. Retain a monotonic source boundary across completed questions and
admit mutating practice controls only after that boundary. Read-only practice info
remains available. The test also verifies that a fresh next then issues exactly one
exercise. No schema or pedagogical aggregate change is required.

The correction passed the complete unfiltered solution: 663 integration tests,
two opt-in live suites skipped, 767 unit tests. A final copy-only adjustment was
followed by a clean Release build (zero warnings/errors), the late-next PostgreSQL
case and 29 dialogue/presenter unit tests. The separate fix commit is reviewed below.

## Architecture assessment

Domain owns the bounded associations, count, owner/skill/preceding-completion guards
and practice lifecycle. It does not copy a second current question or provider jobs.
Application owns progression and synchronization after the common use cases.
Infrastructure only dispatches those concrete operations inside the existing queue
transaction and renders results. Saved provider snapshots are consumed outside
transactions, including replay after apply failure. FK selection, unique active
Lesson, existing evidence and review rules remain authoritative.

Reviewed migration from populated legacy rows, null practice compatibility, schema-4
reader admission, downgrade refusal, explicit next/cost bounds, early capability
capture/flag rollback and isolation from unrelated lesson starts. No new project,
framework or speculative aggregate. Original user changes remain outside commits.

## Validation gaps

The full unfiltered pre-review solution run passed: 662 integration, two live suites
skipped, 766 unit. All six untracked request cases are now included unchanged; the
earlier four-case count was corrected in the execution record. Manual/live/expert
quality and production rollout remain open gates, not fake-only acceptance claims.
