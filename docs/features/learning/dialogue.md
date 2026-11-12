# Current-exercise dialogue

`Learning:Processing:DialogueEnabled` enrolls users in Learning-first text input.
It defaults to false and requires the enabled durable worker and ingress. The
existing command interface remains available. Explicit Inbox capture, Inbox editors
and language selection retain priority; the introductory reply names `/capture` as
the escape for notes. Enrollment is sticky through the owner context or committed
dialogue request, including while the first provider call is in flight. Disabling
the producer flag does not turn enrolled users' pending learning text into Inbox.

`DialogueContext` owns conversation expectations, prompt identity, revision and
one bounded pending clarification or confirmation. It does not replace Lesson,
LearningContext or LearningProgram. Legacy users acquire it lazily from the actual
active/paused Lesson without invented programs or publication timestamps. Schema 1
is explicit; an unsupported schema is rejected before accepting another update.

Natural text uses durable `learning_text` v1. Enrolled command input uses
`learning_command` v3, including the addressed Lesson and prompt; old command v1/v2
remain readable. Dialogue effects, resolution audit, pedagogical changes, operation
marker and reply intents commit together. Prepare rolls back before provider I/O;
the existing queue fences apply by owner, row versions and dialogue revision.

The policy recognizes explicit controls and common clarification phrases locally.
Remaining input for an awaiting question may request one bounded intent proposal
with a strict enum-only schema, at most three truncated prior explanations and no
answer key. The proposal still passes deterministic target, state, owner and risk
guards. Invalid output or unavailable budget leads to clarification. Classifier
plus assessment/explanation uses the existing two-step provider ceiling and shared
monthly financial admission. The intent output bound is 512 tokens; configured
Luna intent requests use reasoning `none`.

An ambiguous input is retained in the original durable request. A one-shot choice
is tied to prompt, revision, target stamp, ten-minute expiry and source order.
Choosing “ответ” assesses the original input, never the word used to choose.
Queued choices received before publication and unrelated free text cannot resolve
it. Question binding and source message order reject answers predating a newly
opened/resumed expectation; legacy questions with unknown publication order do not
receive fabricated history. Current supported interaction is private text/commands,
not a new callback protocol.

Finishing an exercise, changing the selected program or creating a replacement
requires a current eight-character confirmation code. Bare “да” is insufficient.
The proposal stores exact operation/parameters, target identities/versions, expiry
and readable consequences. Consent is consumed once. Pause, explicit editor input,
target changes and expiry invalidate pending proposals. Confirmed goal-change intent
is retained by request reference; switching the pedagogical goal is a later feature.

Answer and clarification dispatch through the same Application learning use cases
as commands. Clarification preserves the question and creates no assessment.
Pause/restart/resume preserves the Lesson and does not generate a replacement.
Resume of an already active question produces a recap. Existing Domain prerequisites
still govern Next, program selection and creation.

Deploy schema and a compatible reader before enabling a limited cohort. Roll back
only to a reader/writer preserving v3/text inputs, pending states and schema 1;
the migration refuses destructive downgrade over dialogue state/history. Stop or
pause acceptance when the worker cannot drain. This is not an instruction to enable
production: test-bot, single-deliverer, backfill and budget gates remain applicable.

The versioned [offline fixtures](../../../evaluation/learning/golden-v1.json) and
PostgreSQL Host tests cover routing, restart, stale consent, source ordering, atomic
failure and no Inbox side effects. Fake proposals establish safety behavior, not
real classifier quality. Held-out provider evaluation and Telegram Desktop/NVDA
remain external release gates. First-goal consultation and multi-item diagnostic
are separate capabilities and are not supplied by this exercise dialogue.
