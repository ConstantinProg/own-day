# Return and E1 verification

User steering after `fa1927c`: continue the full engineering plan; manual testing
will happen after implementation. Manual Telegram Desktop/NVDA acceptance remains
pending and is no longer a development gate for subsequent stages. It is still
required before an external pilot, together with real-model quality/budget,
retention and deployment topology checks. No unperformed check becomes a pass.

S6 starts from `fa1927c` on `main`. Original modified/untracked files are retained
and excluded as recorded in the previous implementation log. The repository-local
implementation, verification and architecture-review skills remain in use.

The two full complete/skip journeys failed before production changes: the planned
“Мне пора, продолжим позже” reached the Answer classifier and completed the Lesson.
Add this exact deterministic pause, navigation help without assessment, and recap
of the saved material/question/latest explanation. New metadata remains absent in
legacy Session results, preserving the existing compatibility contract.

The new PostgreSQL tests use real HTTP/auth/serialization/DI and enabled
LearningRequestWorker and TelegramOutboxWorker, recording fake delivery only at
the external boundary. They dispose the real Host, assert its stopped lifecycle,
restart against the same PostgreSQL, redeliver the old update and resume the same
Lesson/question/help without generation or assessment. A separate hosted stop
during blocked explanation verifies acceptance of the next message and recovery
of both durable requests without assessing the question.

An opt-in paid Learning smoke covers both journeys with an explicit price snapshot,
per-case cap of at most USD 0.50 and four physical invocations, no automatic retry.
Usage, prompt version, timestamps and charged reservations remain in the isolated
test PostgreSQL ledger. Telegram delivery is recorded locally; manual real-client
acceptance is separate. No live call is authorized by merely running ordinary CI.

Validation, commit and post-commit review follow before local completion.

Verification: restore succeeded; Release solution build has zero warnings/errors;
762 unit tests passed. Complete repository integration regression passed with
648 tests and two opt-in live tests skipped. The same explicit
`FullyQualifiedName!~PostgresLearningRequestTests` exclusion preserves the original
untracked four-case fixture; no complete-working-tree green claim is made.
The hosted E1 cases and cancellation/recovery test passed after the pause/help
correction. `git diff --check` passed. Manual and real-provider acceptance remain
pending; only the engineering portion is accepted for subsequent development.
