# Bounded skill practice

`Learning:Processing:PracticeEnabled` defaults to false and requires the durable
learning dialogue. The accepted text retains producer capability. Once a practice
exists, a compatible reader preserves its controls even after enrollment is disabled.
No rollout or production flag change is performed by the implementation.

Say “давай одно упражнение”, “давай два упражнения” or “давай три упражнения”
(text alternatives “практика 1”, “практика 2”, “практика 3”). A selected program
and completed/skipped diagnosis are required. If an exercise is already active it
is explicitly included as exercise one; its question/status/help remain unchanged.
Otherwise the existing program Next supplies the first exercise. After a completed
route, a fresh exercise practices the actual last owned program skill instead of
reopening or inventing route topics.

Every additional exercise needs “дальше”/“next” or compatible `/learn_next`.
Generation never runs automatically after an answer. The subsequent exercises use
the existing PracticeAsync on that same owned skill, without extra route credit or
a retention claim. At most three associations can be issued. The common answer
path creates one evidence record and closes the plan on its last completed exercise.
Repeated next/source redelivery cannot exceed the count.

“пауза” pauses the practice and any actual active question. Between exercises it
generates nothing. “Продолжим” resumes the saved question, or explicitly requests
the next exercise if the previous one is already complete. “хватит практики” ends
the bounded plan, pauses an unfinished Lesson and preserves all answers/evidence.
That unfinished question remains separately resumable. Finishing an exercise
without an answer ends the plan rather than inventing negative evidence. “помощь”
shows the actual current exercise or the saved between-exercise pause/count.

Provider failure never consumes an association/count. “дальше” explicitly retries
under existing attempt/call/budget limits. Prepare/provider/apply checkpoints and
the user lock retain their existing ownership; saved generated material can be
replayed after an apply rollback. Practice associations/count, actual Lesson,
dialogue expectation, operation marker and reply intents commit atomically.

`AddBoundedLearningPractice` adds nullable version-1 immutable JSON to the existing
required LearningContext and upgrades known dialogue schema 3 to 4. Legacy rows
retain their selected program and a null practice; unknown versions are rejected
before receipt. At most three IDs are retained in the current pedagogical plan.
Lesson/evidence history remains in its original store. Starting a later practice
or selecting another program after completion replaces/clears only this current
plan; historical practice-level analytics are not implied.

A different program cannot be selected while the plan is active. A future
confirmed suspend/switch must end the plan first; existing active/paused Lesson
guards still apply. Stop/clear does not erase Lesson history. Apply schema and
compatible consumers with acceptance paused/drained before enrollment; destructive
downgrade is refused over any practice record, including an ended record. Rollback
keeps the schema/reader and disables new enrollment. The [design review](../../design/bounded-learning-practice.md)
explains this ownership decision without introducing a LearningSession aggregate.

Expert/live/manual and deployment gates remain open as recorded for E1. This
bounded opt-in capability does not prove a preferred session length or effectiveness.
