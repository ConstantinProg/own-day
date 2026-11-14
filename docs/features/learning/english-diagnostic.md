# English diagnostic and first exercise

`Learning:Processing:EnglishDiagnosticEnabled` defaults to false and requires
consultation, dialogue and durable ingress. Enrollment is saved in the accepted
text payload. A stored English diagnostic remains readable with the producer flag
off. Existing completed diagnostics are never reopened.

The bounded `work-email-v1` content supports the initial reading/writing journey.
The deterministic enrollment phrases are listed in `EnglishLearningContent`:
Russian requests to read/write in English or read English work emails, and
“I want to read and write in English”. Other goals retain the existing curriculum
and optional single diagnostic. There is no arbitrary web content or CEFR claim.
The original goal and self-reported experience remain unchanged.

Consent (“начнём диагностику”, “да”, “start”) publishes the first of exactly two
items. Reading selects one of A/B/C; the local authored key is B because the
email explicitly asks Alex to send the meeting notes today. A schedules a meeting
and C calls tomorrow, neither of which the email requests. Writing asks for a
short reply to a different email, accepting or declining the request with timing.
Its versioned rubric assesses meaning, clarity and appropriateness; minor grammar
errors are allowed. The immutable rubric goes to the existing assessor; writing
has no normalized equality check. This technical content/key/rubric review is not
expert pedagogical calibration or evidence of learning effectiveness.

Each result records content identity/version, answer, feedback, assessment source
and assistance inside the owned program value. Reading and writing use different
rules; neither diagnostic result creates skill evidence or a review penalty.
“пропустим”/“skip” works at the offer or after the reading item and preserves any
recorded observation. Completion creates exactly one final route revision.
Completion and skip start one authored Lesson via the existing Next use case.
If the diagnostic indicates difficulty or used help, the first Lesson includes
an explicit scaffold and is already marked assisted. Diagnostic uncertainty is
not a certified level.

Commands and dialogue share `CurriculumService.DiagnoseAsync` and `NextAsync`.
`/learn_diagnose start`, `/learn_diagnose B`, `/learn_diagnose <writing answer>` and
`/learn_diagnose skip` remain available. Natural writing may use one bounded intent
proposal followed by one assessment, both through existing durable checkpoints
and budget admission. “ответ: <text>” is the text alternative for an ambiguous
writing reply. Ambiguity asks the learner to repeat the actual answer; the word
“ответ” alone is not assessed. No classifier receives the reading key.

Program/item/version and published prompt/source order must match before a natural
answer or diagnostic command can affect an item. Early queued text never moves
silently onto the next item. “пауза” retains observations; “Продолжим” republishes
the actual saved item. An assessment failure retains the prior item and requires
an explicit retry. Resume never retries a paid assessment. Applied request markers,
program results, final route, Lesson, dialogue and reply intents share one apply
transaction; saved assessments can be replayed after a failed commit without a
second provider call.

New Lessons start with `Independent` assistance. Any successful explanation or
diagnostic scaffold marks them `Assisted`; that snapshot is copied to the single
evidence record. Assisted and legacy Unknown successes earn neither independent
positive mastery credit nor a longer review interval. They keep their original
answer and AI interpretation. Existing conflicting evidence remains conflicting.
The status view displays assistance explicitly.

`AddEnglishDiagnostic` adds nullable versioned JSON and assistance/content fields.
It upgrades known dialogue schema 2 to 3, preserving consultation and consent;
unknown versions remain rejected. Legacy Lesson/evidence assistance is Unknown,
never inferred independent. Existing due dates and history remain; the derived
successful-review streak resets to zero because old successes did not distinguish
help. Future independent reviews earn new credit from zero.

Deploy with producers paused and outstanding provider work drained, then apply
the additive migration and install compatible schema-3 readers/workers before
enrollment. The schema change can invalidate an old preparation stamp; do not
enable incompatible writers over pending work. Rollback keeps this schema and
uses compatible code with enrollment disabled. Destructive downgrade is rejected
when new diagnostic, content or assistance values exist. A failed downgrade over
multiple migrations may have already removed earlier safe migrations: restore
the current schema before restarting the current binary.

Production topology/backfill, retention approval, paid live quality checks,
expert pilot review and Telegram Desktop/NVDA acceptance remain release gates.
This capability does not enable invitations or deploy anything.
