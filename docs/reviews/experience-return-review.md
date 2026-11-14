# Return-flow review

Reviewed `3177668` against `fa1927c` after commit. No confirmed P0/P1/P2/P3
findings. Reviewed deterministic pause admission, consent precedence, read-only
navigation help, saved-help recap and bounded Telegram message sizes. Navigation
does not mutate Lesson/evidence or introduce a classifier/provider call. Session
metadata remains absent for legacy material, preserving its output contract.

The new acceptance factory explicitly installs actual learning and delivery hosted
workers. Tests assert Host shutdown, restart against the same PostgreSQL, duplicate
source acceptance, preserved question/help and zero extra generation/assessment.
The in-flight cancellation case uses the caller/Host token and recovers durable
requests, rather than pretending a manually invoked processor is a hosted lifecycle.

The opt-in smoke keeps physical retry disabled, requires explicit key, price
snapshot and per-case cap, and checks usage/cost/deadlines. It was not run against
a real provider. Its small fixture is a technical smoke, not a retained held-out
evaluation, expert calibration or account-wide billing guarantee.

Production dependencies and Domain authority are unchanged. Existing queue,
provider checkpoints and outbox remain the transaction/delivery mechanisms. No
schema change or new subsystem was necessary. Validation: restore, clean Release
build, 762 unit and 648 repository integration tests passed; two live suites skipped.
The original four-case untracked fixture exclusion remains explicit. Manual and
live quality/retention/topology gates remain open; user deferred manual acceptance
until after engineering implementation, so development may proceed to S7.
