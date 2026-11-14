# English diagnostic implementation record

Baseline: `main`, `a82385d`. Source and Git history confirm local implementation
and review of S1.1–S4. The first unfinished implementation is S5. The next
dependent stage is S6/E1; S7–S9 require E1, S10 requires S5 and comparable checked
observations, and S11 requires pilot data and expert calibration.

Pre-existing changes are preserved and excluded from these commits:
`docs/product-experience-implementation-log.md`,
`docs/reviews/product-experience-branch-review.md`, the untracked plan, and
`tests/OwnDay.IntegrationTests/Telegram/PostgresLearningRequestTests.cs`.

Baseline restore and Release build passed with zero warnings/errors; 756 unit
tests and the PostgreSQL consultation/restart case passed. Two new behavioral
tests failed before the assistance change: three assisted successes earned mastery
and an assisted delayed review earned a longer interval. Both now pass.

Implemented a two-item authored reading/writing diagnostic on the existing
LearningProgram, shared Diagnose/Next use cases, stored prompt/item binding,
one final route revision and an automatic first Lesson. Evidence preserves
versioned content and help; legacy independence stays Unknown. The additive
migration preserves completed diagnostics and history and refuses unsafe downgrade.
See the [capability guide](features/learning/english-diagnostic.md).

Regression identified legacy test fixtures using the current EF model against
an older schema and expectations permitting destructive downgrade or crediting
legacy independence. Tracked fixtures now seed historical SQL columns explicitly,
verify downgrade rejection/data preservation and require new independent evidence.
The pre-existing untracked test file is untouched. Its
`Migrate_PopulatedLegacySchema_PreservesRowsWithoutInventingRequests` similarly
uses the current Lesson model after downgrade and fails with missing `Assistance`.
The committed receipt migration test and the new populated-legacy diagnostic test
verify the actual preservation invariants with historical SQL/unknown assistance.
Final repository regression excludes that entire untracked class (four cases);
this is not a claim that the complete working-tree test suite is green.

Intermediate full working-tree run: 626 integration passed, 16 failed, one live
smoke skipped; 758 unit passed. The failures were investigated rather than counted
as acceptance. Another test attempt lacked the connection environment variable;
it is not counted as verification. Build/test overlap briefly caused assembly-copy
warnings; final builds and tests run sequentially with one MSBuild node and no
reused build servers.

Final validation before implementation commit:

- `dotnet restore OwnDay.slnx`: passed at baseline; no package/project change.
- Release solution build with `--no-restore --disable-build-servers -m:1
  -nodeReuse:false`: passed, zero warnings/errors.
- Unit project, Release/no-build: 761 passed.
- Integration project, Release/no-build, filter
  `FullyQualifiedName!~PostgresLearningRequestTests`: 641 passed, one opt-in live
  smoke skipped. All tracked tests plus the new diagnostic cases were included.
- After the final active-Lesson diagnostic-command guard, the same clean Release
  build and all seven new PostgreSQL cases passed. The command cannot erase the
  current lesson expectation; duplicate/early answers, skip/restart, assistance,
  saved-assessment replay and populated-legacy migration are covered.
- EF model/snapshot equality and `git diff --check` passed.

Implementation is ready for commit; mandatory post-commit review follows.
No paid request, Telegram live send, production enrollment or deployment ran.

Post-commit review of `6e7350b` against `a82385d` found a P1 consent-ordering defect
and a P2 diagnostic control-word defect. Three new scenarios failed against that
commit, while explicit help remained unassessed. The correction routes pending
consent before diagnosis, requires the actual diagnostic expectation in Domain,
and rejects bare choice words before classifier admission. Four focused cases and
all 761 unit tests passed after correction; complete repository regression and the
separate fix commit follow. Details: [code review](reviews/english-diagnostic-review.md).

Fix verification: complete repository regression passed, 645 integration tests and
one live smoke skipped, with the same explicit four-case untracked-class exclusion.
All 761 unit tests passed and Release build had zero warnings/errors. The new
confirmation test proves the selected program changes through the existing guarded
use case with zero classifier/assessment calls and the original diagnostic result
retained. Choice words/help do not become observations.

`fbfb400` was re-reviewed against `6e7350b` after commit. Both findings are resolved;
no confirmed remaining P0/P1/P2/P3. S5 is complete locally, with expert/live/NVDA
and deployment gates explicitly retained. All implementation and review fixes are
committed; the original user changes remain excluded.

## Next stage: S6 blocked at external acceptance

The S6 requirements were inspected after closing S5. They require paid opt-in
Learning live smoke with a test account and a bounded budget, plus manual Telegram
Desktop/NVDA acceptance of both E1 journeys. `OPENAI_API_KEY` and the checked
OpenAI configuration-key environment variables are absent in this session. No
test-account live run or human keyboard/screen-reader acceptance record is
available. NVDA being installed does not establish that acceptance passed.
Fake providers, static copy checks and hosted-worker tests cannot substitute for
those explicit requirements. Production enrollment/deployment is not an alternative.

S6/E1 is not complete. No new S6 implementation is claimed, and its combined
hosted-lifecycle/failure journeys still need their own acceptance run in addition
to the required live/manual checks. To resume, configure a non-production test
account/key through runtime configuration (never commit or paste secrets), perform
the capped live evaluation and record the manual client/OS/NVDA versions/results.
Retention approval, real topology/backfill and expert quality thresholds must be
resolved before the external pilot.

S7–S9 remain unstarted behind E1 and their individual design/guard requirements.
S10 can be scoped independently after S5 but still needs demonstrably comparable
baseline/end observations; no comparability or calibrated achievement metric was
invented in this run. S11 needs actual pilot data and expert calibration. The plan
does not permit claiming these later capabilities from fake-only evidence.

The untracked source plan receives an additive status/link note without changing
its requirements or earlier decisions, and is intentionally not added to Git.
