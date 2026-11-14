# Bounded practice implementation record

S7 begins after the S6 engineering review (`20205a0`). Separate design review
`8f7c1b2` documents the verified one-exercise gap, explicit 1–3 volume and ownership
on LearningContext. User-directed manual-test deferral permits further engineering;
external release gates remain open. No new LearningSession, broker or framework.

The first PostgreSQL two-exercise/restart acceptance test failed against the prior
code: only one Lesson existed instead of the requested two. The implementation
reuses program Next for the first exercise and existing owned-skill PracticeAsync
for further exercises. It saves bounded associations and reads actual Lesson status
instead of mirroring a second current question. Additional generation always needs
an explicit next request. Common answer/finish/pause transitions synchronize the
plan within the existing atomic queue apply; Infrastructure invokes Application
policy without choosing its transitions.

Early focused checks: three PostgreSQL cases and 766 unit tests passed, including
pause/stop/restart/flag rollback and failure not consuming a slot. Added route-ended
practice and apply rollback/saved-generation replay checks, plus legacy schema
compatibility and final regression verification before commit/review.

The feature guide records schema-4 deployment/drain, immutable version admission,
downgrade refusal, selection guards and preservation of Lesson/evidence history.
Original user changes, including the untracked legacy test class, remain excluded.

Correction to earlier records: the untracked request class contains six cases,
not four. The prior filter excluded that entire class; the count was incorrectly
inferred while other tests were added. All six cases now pass unchanged against
the newer adjacent migration pair, whose predecessor includes the Lesson fields.
No user test file was edited or staged. Final S7 regression runs the entire solution
with no exclusion; actual results follow below. The original fixture fragility
and historical failure remain documented, without claiming a source fix.

The first broad run had one failure in a tracked migration fixture: a safe newer
downgrade had completed before an older downgrade guard rejected the chain, leaving
the newer nullable column absent. The test now restores the current schema before
reading with the current EF model, exactly as required by the compatible-reader
rollback guide. No production guard or data-preservation assertion was relaxed.

Final validation: clean Release solution build, zero warnings/errors; complete
`dotnet test OwnDay.slnx --configuration Release --no-build --disable-build-servers
-m:1 -nodeReuse:false --blame-hang-timeout 60s` passed without filters. Results:
662 integration passed, two live suites skipped, 766 unit passed. This includes all
six original untracked request cases, unchanged. Nine focused request/practice cases
also passed. Provider generation runs outside transactions; saved-result replay
does not pay again after a failed apply. EF model/snapshot equality, populated
legacy upgrade and guarded practice downgrade are covered. `git diff --check` passes.
Implementation commit and post-commit review follow before proceeding to S8.

Implementation `637d362` was reviewed against `8f7c1b2` after commit. Confirmed P2:
completion reset the dialogue source boundary, so a delayed older next could issue
a future exercise. The new PostgreSQL case failed before correction with two
Lessons. The fix retains a monotonic boundary across completion and records/checks
mutating practice controls against it; fresh next remains allowed. Added a Domain
boundary invariant test. [Review](reviews/bounded-practice-review.md).

An overlapping intermediate build/test run held output assemblies and caused copy
retry warnings; it is not counted as final validation. The subsequent sequential
Release build passed with zero warnings/errors, followed by an unfiltered solution
regression. Final results and the separate fix/re-review follow below. User-facing
unavailable copy also avoids an internal handler-version instruction.

Correction validation: complete unfiltered solution passed, 663 integration and
767 unit tests, two opt-in live suites skipped. Final copy-only build passed with
zero warnings/errors; late-next PostgreSQL case and 29 dialogue/presenter unit tests
passed afterward. No exclusion of the original six user request tests.
