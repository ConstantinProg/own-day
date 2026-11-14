# Confirmed program switching

The current Lesson guard correctly rejects selection/creation while a question is
active or paused. A confirmed goal change only records an intent; it cannot yet
perform the approved switch. Removing the guard or finishing the question would
lose the promised return point. This is the verified scope of S8.

Keep one LearningContext selection. Add a Domain Lesson suspension transition,
retaining its original AwaitingAnswer/Paused state and the program under which it
was suspended. The latter is a resume association, not a rewrite of ProgramId,
SkillId, route, question, help or evidence. It also supports standalone/review
Lessons. An owner-scoped unique suspended question per program prevents replacing
the return point. Completed/finished Lessons cannot be suspended.

Application validates both targets before suspending the current question. It ends
the bounded practice explicitly as part of the disclosed switch, flushes the
suspension inside the existing outer apply transaction, changes selection, then
restores the target question to its original state. The short flush avoids a
transient collision on the existing unique active-Lesson index; it does not commit.
No external provider call is made between flush and apply commit. For a new goal,
planning/validation occurs before any suspension; saved provider results retain
the existing prepare/provider/apply recovery and cost bounds.

Dialogue uses distinct confirmed switch operations and exact goal text. The
existing code, prompt/revision, source order, owner, target version and expiration
checks remain authoritative. Include the target suspended Lesson version in its
confirmation stamp. A new default-off capability is captured at acceptance; saved
confirmations remain consumable after a flag rollback by a compatible reader.
Pause/resume/cancel invalidate pending consent. Existing command grammar and
unconfirmed Application guards remain compatible. Vague goal changes ask for the
actual goal rather than inventing one. A new goal creates a new program; it never
edits the old goal or relabels its historical evidence.

Additive nullable suspension columns, owner FK, filtered uniqueness and consistency
constraint; upgrade known dialogue schema only. Downgrade refuses persisted
suspensions or new pending switch operations. Deploy compatible consumers with
acceptance paused/drained before enabling the capability. No production deployment.

Design review before schema implementation: preserves aggregate authority and layer
direction; no LearningSession, new framework or parallel pedagogical state. Tests
must cover active/paused round trips, restart, stale/foreign/repeated/expired
consent, target revision, invalid/failed creation and atomic apply recovery.
