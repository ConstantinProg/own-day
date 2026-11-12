# Learning provider paths

Investigation for S1.3, after `141d56e`. This is an implementation inventory, not a
claim that prepare/provider/apply is implemented. Public entry points still execute
these services inside the legacy Telegram transaction; the new lane rejects them.

| Entry | Existing use case | External steps | Required prepare/apply guards |
|---|---|---|---|
| `/learn` | `LearningService.StartAsync` | Generate; conditional RFC source retrieval inside adapter | Owner, no active Lesson, effective preferences; create Lesson/Skill only during apply |
| `/answer` | `LearningService.AnswerAsync` | Assess | Exact LessonId, AwaitingAnswer, immutable question/material, skill/program identity; atomic answer/evidence/review |
| `/learn_ask`, `/learn_explain` | `LearningService.ClarifyAsync`/`ExplainAsync` | Explain | Exact LessonId, AwaitingAnswer, question/teaching/transfer snapshot; append one interaction and preserve question |
| `/learn_practice` | `LearningService.PracticeAsync` | Generate; conditional source read | Owned skill, no active Lesson, skill state/preferences; link same skill |
| `/learn_review` | `LearningService.ReviewAsync` | Generate; conditional source read | Owned skill, due review, no active Lesson, preferences; mark review without route progress |
| `/learn_request`, `/learn_program`, `/learn_project` | `CurriculumService.CreateAsync` | Plan | Normalized goal/experience, optional owned project/title, no active Lesson; create and select exactly one Program |
| `/learn_diagnose` (answer) | `CurriculumService.DiagnoseAsync` | Plan | Exact selected ProgramId/route/discipline, diagnostic pending; proposal must not ask a second question |
| `/learn_diagnose skip` | same | None | Preserve skip as a deterministic guarded operation |
| `/learn_next`, `/learn_adapt` | `CurriculumService.StartLessonAsync` | Conditional teaching planner, then Generate/source read | Program/route/next topic, prerequisites/completed topics, skill state, bounded feedback, preferences; validate teaching proposal before choosing target; apply adaptation and Lesson together |
| `/learn_focus` | same | Generate/source read | Owned selected Program, explicit available topic and prerequisites; apply proposed adaptation only with valid material |
| `/learn_project_practice` | `CurriculumService.ProjectPracticeAsync` | Generate/source read | Owned active project/action, action belongs to program's project, exact route/topic/prerequisites, no active Lesson; educational links cannot complete real task |
| `/learn_transfer` | `CurriculumService.TransferPracticeAsync` | Generate/source read | Two owned different-discipline Programs, both diagnostics/routes/prerequisites, confirmed shared skill identity, no active Lesson; source progress unchanged |
| `/learn_invite_start` | `StudyService.StartAsync` | None if resuming; otherwise Review or Select + Next above | Exact current invitation, schedule target/expiry, active Lesson versus skill/program branch; failures leave invitation unresolved; selection and resolution require final guards |

`Next` can invoke the teaching planner even without explicit Adapt when derived
skill state has repeated difficulty. Planner and generator therefore need distinct
step checkpoints, not one result blob or one provider attempt identifier. Source
retrieval is external I/O even if it returns an unavailable-source record. Its
catalogue remains fixed; do not add user/model URLs.

## Current boundaries to preserve

- `LearningService.AnswerAsync` deliberately lets Domain/evidence persistence failures
  escape its provider catch so the enclosing transaction rolls back completely.
- `KnowledgeService.AttachAsync` can create a Skill and link it to a Lesson. It must
  remain in guarded apply, never durable prepare.
- `EfCurriculumStore.SelectAsync` changes LearningContext. Invitation start can select
  a Program before generation; do not retain that change across an unsuccessful
  prepare/provider phase accidentally.
- `program.ProposeAdaptation` is a proposal; `ApplyAdaptation` mutates the route only
  after material validation. Preserve that ordering and prerequisite validation.
- `ILessonTeacher.AssessAsync` and `ExplainAsync` currently accept mutable Domain
  Lesson. The asynchronous path requires a concrete immutable teaching snapshot;
  a tracked Lesson must not cross a scope/provider boundary.
- Preferences and derived skill state influence generation. Sequence alone does not
  detect an external writer changing a target after prepare. Persist target identities
  and stamps and revalidate them on a fresh scope during apply.
- Legacy ILessonTeacher implementations exist in the supplied untracked acceptance
  test. Preserve that file and compatibility when introducing snapshot contracts.

## Planned verification matrix

Each external branch needs a blocked fake with a committed prepared input; a second
scope must acquire the same user's advisory lock and a short database operation while
the provider is blocked. The provider scope must have no open transaction or tracked
Domain state. A competing target change must reject stale apply. Run the same tests
for the conditional two-call branch and for source retrieval failure, plus existing
owner/paused/prerequisite/evidence/review/project/transfer/invitation regressions.

S1.3 is not complete until every row above uses the new protocol and passes these
checks through the real worker. No provider support or public early-ack is enabled
by this inventory.
