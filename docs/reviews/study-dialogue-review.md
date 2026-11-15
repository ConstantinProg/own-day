# Study dialogue review

Reviewed `2daacd9` against `68b8cb7` after implementation commit. No confirmed
P0/P1/P2/P3 findings remain in the reviewed change.

Architecture assessment: the new code parses explicit Telegram phrases only;
Application/Domain retain opt-in, validation, windows, invitation currentness and
state transitions. Text phrases enter the same version 3 durable dialogue lane
as commands, retaining question/prompt binding and bounded-practice guards.
No LLM chooses schedule defaults or invitation targets. Active input editors keep
priority. No schema or new dependency is introduced. Existing shared use cases,
worker recovery, source dedup and atomic apply/outbox boundaries remain in use.

Checked schedule read without opt-in, owner/current ID checks, repeated actions,
restart, no answer assessment, equal command guards and cancellation propagation.
Two dispatchers and pre-send opt-out checks reuse the tested learner-lock path.
Scale-out of ordinary outbox delivery remains unsupported until claim/lease work;
this change retains the existing single-deliverer deployment requirement.

Validation: initial requirement test failed before production changes; initial
44 unit / 26 integration focused tests passed. Full solution then passed 782 unit
and 680 integration / 2 live skipped. After the final routing correction, Release
build passed with zero warnings/errors and 134 affected unit / 29 integration
checks passed, including the new bounded-practice guard case. The full solution
was not rerun after that correction; final changed-path coverage is focused.
Manual Telegram/NVDA and real topology/timing remain external release gates.
