# Bounded multi-exercise practice design

Design review for S7, after the S6 engineering review (`20205a0`). External E1
release gates remain open; the user explicitly deferred manual testing until after
engineering. The feature remains opt-in and disabled for new enrollment by default.

## Demonstrated gap and minimum scope

Code confirms that Lesson.Complete ends one exercise and Curriculum.Next can end
the route after that exercise. There is no saved upper bound or next/stop policy
for an explicit request to do two or three exercises of the current skill. Repeated
generation without a saved count would not implement the approved bounded-volume
requirement. The minimum delivery is an explicit 1–3 exercise practice, with a
separate learner request before each next exercise. There is no automatic paid loop.

Reuse existing Lesson, skill PracticeAsync, source deduplication and provider
checkpoints. First include the already active exercise, if present; otherwise use
the selected program's Next. Further exercises practice the same actual owned skill
through the existing use case, outside route advancement. This is deliberate skill
practice, not a claim that a route has more topics or that task completion proves
mastery. Paused Lessons retain their existing semantics.

## Ownership decision

Use a small immutable versioned `LearningPractice` value on LearningContext, whose
existing required owner/program selection remains unchanged. It records requested
count, issued Lesson associations and the practice's running/paused/ended status.
Lesson remains authoritative for its answer/status/help; evidence remains per Lesson.
There is no second current question or copied Lesson status in the value, and no
LearningSession aggregate, new project, generic workflow engine or job representation.
DialogueContext continues to own only conversation expectations/consent. LearningContext
owns this concrete pedagogical volume choice, not Telegram prompts or provider jobs.

At most three immutable Lesson IDs are associated with the current practice. A new
association requires the actual previous Lesson to be completed and the same owned
skill; duplicate associations and foreign Lessons are rejected. Finished-without-answer
ends the practice, without inventing negative evidence. A subsequent practice replaces
the small current plan, while all Lessons/evidence remain preserved in their stores.
Historical practice analytics are out of scope; S10 must not infer such a history.

## Execution and recovery

One Application slice handles explicit start/next/pause/resume/stop and synchronizes
the plan after the common answer/finish/pause use cases. Infrastructure invokes that
policy within the existing apply transaction, never choosing its transitions itself.
Creation, association/count, context, marker and outbox commit together. Preparation
uses the existing provider session, so external generation never runs in a database
transaction. Restart reads the saved associations and actual Lesson. Repeated source
updates do not issue another exercise. Bounded count and explicit next control cost.

Domain rejects selecting another program while the practice is active; an explicitly
confirmed switch must first end that plan in the later switch capability. Stop pauses
an unfinished Lesson and ends the practice, leaving that question independently resumable.
Pause between exercises generates nothing; resume is explicit. Last completion closes
the plan and cannot trigger an extra generation.

## Schema and release

Add nullable practice JSON to the existing required LearningContext row and upgrade
known dialogue readers to schema 4. Legacy rows and selected programs remain valid;
unknown versions are rejected. Refuse destructive downgrade over any practice record.
Deploy compatible consumers with acceptance paused/drained before enabling enrollment;
retain a reader and the data on rollback. Record producer capability in accepted input.
No production flag is enabled here.

Design review: this is the smallest concrete extension of the existing model for
the approved volume requirement. It preserves dependency direction and shared use
cases. No confirmed P0/P1 design finding; tests must prove count limits, same-skill
ownership, restart at each boundary, stop/no negative evidence, provider failure,
atomic publication and legacy upgrade/downgrade protection before local completion.
