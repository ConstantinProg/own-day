# First goal consultation

`Learning:Processing:ConsultationEnabled` defaults to false and requires the
Learning-first dialogue flag. Enable it only after the durable ingress rollout
gates. It affects first-goal consultation; existing programs and active/paused
Lessons keep their existing guards. An unfinished draft remains readable and
resumable even after the enrollment flags are disabled.
Acceptance also records consultation capability in the durable text input, so a
goal accepted immediately before flag rollback can still initialize its draft.

A direct goal such as “Хочу читать и писать на английском” does not require Start.
The original desire is retained verbatim (apart from outer whitespace) with its
request ID. Two short questions collect practical use and self-reported experience.
No model rewrites the goal or decides whether the questionnaire is complete.
Short yes/no, unrelated choice words, interrogative responses and a new “Хочу…”
request do not silently replace the saved goal. The prompt is repeated when input
was accepted before the current question or addresses an older prompt.

After both bounded details are supplied, the existing
`CurriculumService.CreateAsync(projectId: null)` plans and selects one program.
The original goal becomes Program.Goal, and practical use/experience are stored in
Program.Experience. No Inbox, Project or Action is created. The draft records the
created ProgramId atomically with the program, selection and reply intents; source
deduplication prevents repeated creation. The response states the saved goal and
practical use, then offers a short optional diagnostic. This milestone stops at
the offer; multi-item diagnostic execution requires the next capability.

A planner failure retains the completed draft and asks for explicit “повторить”.
Resume reopens the saved question instead of silently retrying a paid call.
Start/help repeats current guidance without resetting the draft or welcoming a
returning owner as new. Pause/cancel and explicit Inbox input preserve the draft;
“Продолжим” restores it. An existing selected program is never replaced by this
first-goal use case. Replacement still uses consequential confirmation and Domain
guards from the current-exercise dialogue.

Dialogue schema 2 adds a nullable version-1 consultation value. The additive
migration upgrades known schema-1 contexts without changing pending consent,
revision, Lesson or pedagogical history; unknown schema versions are retained and
rejected. Deploy the compatible reader before enabling consultation. Older schema-1
writers reject existing version-2 owner rows; they are not compatible consumers
for new consultation input and must be stopped before enabling the capability.
Rollback is to the compatible schema-2 consumer with flags disabled, including
before the first draft has been initialized. Downgrade is refused when
any draft exists; retain the schema and use compatible code for rollback.

The initial authored diagnostic/content, live classification quality, external
pilot wording and Telegram Desktop/NVDA acceptance remain separate gates. No
notifications are enabled by consultation or by accepting a diagnostic offer.
