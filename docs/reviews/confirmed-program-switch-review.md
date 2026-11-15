# Confirmed program switch review

Reviewed `ed71a8b` against `e46894e` after implementation commit. No confirmed
P0/P1/P2/P3 findings remain in the reviewed change.

Architecture assessment: Domain owns suspension/restoration and rejects foreign
programs and ended questions. Application retains default active-question guards,
validates source/destination and performs the suspension flush inside its caller's
transaction. Provider planning still runs through prepare/provider/apply; the
flush cannot commit independently. Selection, consent consumption, operation marker,
outbox and request completion retain the final atomic apply boundary. One selected
program and the original Lesson evidence targets remain authoritative.

Checked one-shot source/expiry/stamp binding, destination suspended-question
version, sticky accepted capabilities, pause/cancel invalidation, schema upgrade,
filtered return-point uniqueness and downgrade refusal. Commands share the same
Application use cases as dialogue. No new project, provider dependency or generic
executor was introduced. Historical migration fixtures seed historical state rather
than mapping the current entity into an older schema.

Validation: Release build zero warnings/errors; full unfiltered PostgreSQL 18
regression 677 integration passed / 2 live skipped and 770 unit passed. Focused
13-test run additionally confirmed rollback/recovery and paused/active return.
Validation gaps: real model quality, Telegram Desktop/NVDA and actual deployment
topology/retention remain external release gates. No production enablement occurred.
