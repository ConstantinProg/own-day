# Study dialogue execution

S8 engineering completed as `ed71a8b`, review recorded in `68b8cb7`.

S9 investigation found that StudySchedule, StudyService, ReviewSchedule and the
dispatcher already implement opt-in, windows/DST, pause, later, invitation guards,
atomic outbox intent and concurrent dispatcher serialization. Natural scheduling
phrases were absent. The new PostgreSQL natural schedule/opt-out/restart case
failed before production edits because no StudySchedule was created.

Minimal implementation maps explicit transport phrases to existing version 3
durable dialogue commands at acceptance, including current question/prompt binding.
Active editors retain priority. No new
provider, scheduling policy, persistence model or dialogue aggregate is added.
Invitation actions require the original ID; bare later does not infer a target.

Focused initial checks: Release build zero warnings/errors; 44 unit and 26
integration Study/Schedule tests passed. Added current/foreign-ID/repeated-action
cases before the final full regression. Final results and post-commit review follow.

Deployment remains one outbox deliverer; claiming/lease migration is a prerequisite
for any future scale-out. Two invitation dispatchers and their concurrent send
rechecks are covered by existing PostgreSQL tests. No topology change is proposed.

Final validation: restore/Release build passed, zero warnings/errors. Full
unfiltered solution before the final dialogue-lane correction: 680 integration
passed / 2 live skipped, 782 unit passed. The final correction preserves the same
version 3 question/prompt binding and bounded-practice guards as slash commands.
After that correction, Release build passed, 134 affected unit and 27 integration
Study/Schedule/Update cases passed; both additional repeated/stale invitation-ID
cases also passed on the final version. The complete suite was not repeated after
that final narrow correction; its changed path is covered by those focused tests.

Implementation commit `2daacd9`; mandatory post-commit review against `68b8cb7`
found no confirmed P0/P1/P2/P3. See [review](reviews/study-dialogue-review.md).
S9 engineering completed with the documented single-deliverer constraint and
external release checks still open. No scale-out or production deployment occurred.
