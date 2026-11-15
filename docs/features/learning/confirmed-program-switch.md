# Confirmed goal and program changes

`Learning:Processing:ProgramSwitchEnabled` defaults to false and requires dialogue
and durable ingress. It adds `выбрать программу <id>` and
`сменить цель на <exact goal>`, and allows existing `/learn_select`, `/learn_request`
and `/learn_project` to propose a switch while a question is active or paused.
Ambiguous goal intent asks for the actual goal. The selected owner/program versions,
current question and suspended destination question version bind the proposal.

The response discloses that the current question/help will be retained, bounded
practice will stop and historical answers remain. Only the exact ten-minute code
can consume that proposal. Bare yes, foreign/stale/expired/repeated consent does
not change selection. Pause/resume/cancel invalidate pending consent. Accepted
capability and saved switch operations remain processable after flag rollback.

One selected program remains authoritative. Switching suspends its unfinished
Lesson without assessing or finishing it; switching back restores the original
AwaitingAnswer/Paused status, question and help. This is a return association and
does not rewrite the Lesson's original ProgramId/SkillId/route. Existing route
prerequisite/progress rules still govern new Lessons. A confirmed new goal creates
a separate program with experience explicitly unknown; old goal/evidence stays
unchanged. Failed planning leaves the current question/selection untouched.

The Domain owns suspension/restoration. Application validates the source and target,
then flushes suspension in the existing outer apply transaction before restoring
another question, avoiding transient active-index collisions. Request completion,
selection, consent consumption, replies and final state commit together. Real
provider I/O remains outside the transaction; replay uses saved results.

`AddProgramSuspension` adds nullable resume fields, owner FK, filtered uniqueness
and consistency constraint. Known dialogue schema 4 upgrades to 5; unknown versions
are not rewritten. Populated legacy rows retain their state. Downgrade refuses any
suspended return point or pending switch operation. Roll back flags to a compatible
reader; do not drop stored questions. Pause/drain acceptance and migrate/install
compatible consumers before enabling the default-off capability. No scale-out or
production deployment is implied. Manual Telegram/NVDA and live quality gates are
deferred, not reported passed by local fakes.
