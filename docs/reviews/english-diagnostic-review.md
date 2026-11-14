# English diagnostic code review

Reviewed implementation `6e7350b` against parent `a82385d` after commit, including
all new files, shared use cases, migration, prompts and tests. This is a local
critical code review; no independent human or expert pedagogical review is claimed.

## Important — P1: diagnostic dispatch precedes pending consent

[LearningDialogueService.cs](../../src/OwnDay.Application/Dialogue/LearningDialogueService.cs)
originally routed ordinary input to the English diagnostic before resolving a
pending confirmation. During the writing item, `/learn_select` could publish valid
consent, but “подтверждаю <code>” was then interpreted as diagnostic text. With an
Answer proposal, it was assessed and could complete the diagnostic/start a Lesson
instead of selecting the explicitly confirmed program. `MatchesDiagnostic` also
checked target identity without checking the expected response/phase.

The new PostgreSQL `Post_ProgramConfirmationDuringWriting_ResolvesConsentWithoutAssessingConfirmationText`
failed before correction: selection retained the original program. Move English
dispatch after the existing confirmation/clarification rules, exclude English
programs from the older consultation-only offer path, and require the actual
diagnostic expectation in Domain binding. Consent must use the original guarded
operation and make no intent/assessment call.

## Important — P2: choice words can become a writing observation

[EnglishDiagnosticDialogue.cs](../../src/OwnDay.Application/Dialogue/EnglishDiagnosticDialogue.cs)
passed bare “ответ”/“вопрос” to the classifier. A permitted Answer proposal assessed
that control word as writing, creating a second diagnostic result and first Lesson.
The capability guide explicitly says a control word alone is not an answer.
Two PostgreSQL `Post_WritingControlOrHelp_DoesNotAssessControlWords` cases failed
with two persisted observations instead of one. Reject these words locally and
ask for the complete original answer; help must also remain unassessed.

Both findings are corrected. All four focused scenarios pass, including help,
and complete repository regression passes: 645 integration tests, one live smoke
skipped, 761 unit tests. Release solution build has zero warnings/errors.
The separate fix commit and its post-commit re-review follow before completion.

## Architecture assessment

Production dependencies retain their original direction. Program owns bounded
diagnostic transitions/results, Lesson owns completion/help, evidence owns its
observation snapshot, and DialogueContext owns expectations/consent. Application
owns admission and workflow policy. The adapter dispatches/rendering and existing
short queue transactions preserve atomic effects, marker and outbox. Writing uses
the existing immutable assessor/checkpoints and finite budget, never an LLM call
inside the database transaction. No project, broker or framework was added.

## Validation gaps

Expert calibration, paid held-out/live quality, Telegram Desktop/NVDA, real rollout
topology and retention remain external gates. The pre-existing untracked request
test class is excluded and its old-schema fixture defect is documented separately;
the entire working-tree suite is not claimed green. Producer defaults remain off.
