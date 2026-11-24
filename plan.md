# OwnDay --- Post-audit implementation plan

Status: In Progress\
Baseline: OwnDay 0.4.0, commit `0939d147fb04e6591fd0192721f912ab7caa4567`\
Evidence: `ownday-diagnostics.zip`, snapshot 2026-11-23 14:54:45 UTC, schema 1.

## 1. Objective

Restore reliable lesson generation, improve diagnostic coverage and pedagogical quality, and validate the resulting Learning Platform through a real learning journey. Do not expand the product scope or redesign the Domain without evidence.

## 2. Codex execution protocol

-   Read `AGENTS.md` and repository-local implementation, architecture-review and verification skills. Inspect current source, tests, migrations and relevant docs. The deployed baseline may differ from HEAD; verify each finding against current code before changing it.
-   Execute stages in order. Within a stage, reproduce each defect with a regression test where feasible, implement the smallest coherent fix, run targeted tests and then the relevant integration suite. If already fixed, record evidence instead of rewriting code.
-   Maintain this file as the persistent work ledger. Mark a checkbox only after its acceptance criteria are met; record test commands, results, changed files, remaining risks and deviations. Use statuses Pending / In Progress / Completed / Blocked / Not Reproducible / Deferred. Do not equate a hypothesis with a confirmed defect.
-   Preserve Clean Architecture, durable ingress, owner sequencing, lease fencing, transactional outbox, budget admission, cancellation and update deduplication. Do not weaken existing tests, silently swallow errors or expose secrets.
-   Do not commit, push, deploy, alter production configuration or invoke paid live providers without explicit authorization. Use fakes and PostgreSQL integration tests; distinguish manual acceptance from automated verification.
-   At each stage boundary report results and blockers. Continue to the next implementation stage when automated gates pass; external gates remain explicitly pending. Stop if a blocker makes further work unsafe. Stage 5 requires operator participation.

## 3. Evidence and issue register

The export contains 132 events, five Learning requests (three successful, two failed), three provider attempts (one successful plan, two cancelled generation attempts), no lessons or knowledge evidence. Effective configuration: LLM timeout 60 s, Learning provider timeout 15 s, lease 30 s, max provider attempts 2, max output 8192 tokens. Both generation attempts ended around 15 s with `provider_outcome_unknown`. This strongly suggests the outer timeout but does not establish the cancellation source. The journal is explicitly best-effort/partial; zero observed losses does not prove completeness.

  ------------------------------------------------------------------------------------------------------------------------------
  ID             Priority       Finding                                       Evidence status                     Stage
  -------------- -------------- --------------------------------------------- ----------------------------------- --------------
  OD-001         P0             Conflicting timeout budgets                   Strong inference                    1

  OD-002         P1             Context-inappropriate failure reply           Observed                            1

  OD-008         P1             Cancellation source missing                   Observed                            1

  OD-007         P1             Inline button labels absent in export         Observed                            2

  OD-009         P1             Decision rationale insufficient               Observed gap; scope to verify       2

  OD-010         P1             User language settings absent from snapshot   Observed                            2

  OD-011         P1             Budget state absent from snapshot             Observed                            2

  OD-012         P2             Ambiguous summary error counters              Observed semantics gap              2

  OD-003         P1             Generic, poorly measurable goal criterion     Observed quality issue              3

  OD-004         P1             Broad topics may be credited too early        Hypothesis                          3

  OD-005         P1             Lesson generation prompt complexity           Hypothesis                          4

  OD-006         P1             RequestedFormat null vs choice instructions   Contract ambiguity to verify        4

  OD-013         P2             Shared output token ceiling                   Optimization candidate              4

  OD-014         P2             Flat route limited to 12 topics               Architectural risk; research only   3
  ------------------------------------------------------------------------------------------------------------------------------

## 4. Stage 1 --- LLM reliability and failure UX

Status: Completed. Issues: OD-001, OD-002, OD-008.

-   [x] Trace effective timeout and cancellation propagation from worker through provider adapter and HTTP client. Reproduce generation taking longer than 15 s using a controllable fake; distinguish outer timeout, adapter timeout, shutdown, lease loss and network failure.
-   [x] Define one coherent timeout policy (operation-specific where justified). Validate configuration and document precedence. A generation budget of 60--90 s is a candidate, not a mandatory value; choose based on existing limits and evidence. Ensure the adapter and outer deadline do not unintentionally conflict.
-   [x] Verify lease renewal/fencing, queue age/readiness, worker concurrency and bounded shutdown for calls longer than the current 30 s lease. Never apply results under a stale lease or abandon an active provider task while releasing its slot.
-   [x] Preserve provider checkpoints, financial reservations and unknown-outcome semantics. Do not blindly retry an ambiguous remote outcome; do not infer that cancellation means no charge. Distinguish known safe retries from uncertain outcomes.
-   [x] Add structured cancellation diagnostics: source, effective deadline, elapsed duration, operation, attempt, correlation and outcome certainty. Distinguish simultaneous/unknown causes honestly; never invent a cause.
-   [x] Make failure replies operation-specific and localized. For failed first lesson generation, explain that the program remains saved and no lesson was created. Provide a safe contextual retry action when permitted; preserve owner, target and prompt freshness checks.
-   [x] Test slow successful generation, timeout, adapter failure, host shutdown, lease loss, stale target, redelivery, unknown outcome, budget preservation, and recovery without duplicate lessons or evidence.

Acceptance: a fake generation exceeding 15 s but within the effective budget succeeds exactly once; timeout and cancellation paths retain coherent state and accurate diagnostics; no regression of financial or transactional guarantees. Production smoke remains an external gate.

## 5. Stage 2 --- Diagnostic completeness

Status: Completed. Depends on stage 1. Issues: OD-007, OD-009, OD-010, OD-011, OD-012.

-   [x] Reproduce missing inline button labels; export actual display labels, type, semantic action and safe context reference where available. Exclude raw signed callbacks, credentials and signing material. Verify normal mode remains metadata-only.
-   [x] Instrument actual Application decision reasons for goal/strategy, diagnostic selection, level, exercise format and relevant guards. Record only reasons genuinely used; distinguish deterministic rules, user input and LLM proposals. Do not capture hidden reasoning or fabricate rationale.
-   [x] Add `userSettings` snapshot: persisted language, effective language and source, Telegram language only when actually available with provenance. Capture safe language-change events; do not reconstruct unavailable historical settings.
-   [x] Add owner-scoped `llmBudget` snapshot from existing ledger: period, recorded usage, reservations, unknown-cost calls and remaining allowance when computable. Distinguish global limits from owner usage; do not disclose other owners' data. Unknown cost must not become zero cost.
-   [x] Clarify summary counters: severity errors, failed requests, timeouts, cancellations, unknown outcomes and rejected decisions. Define denominator and scope for current state, request cohort and selected timeline; do not double-count correlated events as operations.
-   [x] Review export schema compatibility; use additive fields or an explicit version change as appropriate. Update documentation and all three export modes. Preserve snapshot consistency, best-effort journal disclosure, retention, resource limits, authorization and reset fencing.
-   [x] Add tests for detailed/normal capture, redaction, owner isolation, partial sections, budget unknowns, summary counting, language changes and export schema.

Acceptance: a synthetic failed-generation journey can be investigated from export alone without guessing the cancellation source; labels, settings, actual decision reasons and budget state are available where supported. No sensitive payload leakage or cross-owner disclosure.

## 6. Stage 3 --- Curriculum planning and mastery granularity

Status: Completed. Issues: OD-003, OD-004, OD-014.

-   [x] Reproduce generic success criterion from first-goal consultation (goal: understand the exchange using Moscow Exchange as example; purpose: independent investing; experience: beginner).
-   [x] Improve goal criteria so they describe observable competencies aligned with user intent. Prefer a bounded structured planning proposal validated by Application; preserve confirmation, user intent and deterministic guards. Do not add an extra provider call unless justified.
-   [x] Inspect the relationship between Topic, Skill, Lesson, Exercise, Evidence and route completion. Test whether a single narrow exercise can incorrectly credit a broad topic. Fix only demonstrated defects; preserve prior evidence and route history.
-   [x] Improve topic granularity and prerequisite coherence within the existing route model. Add representative planning fixtures for small and broad goals, including the exchange scenario. Avoid hardcoding a fixed number of topics.
-   [x] Investigate the 12-topic limit and document trade-offs for long curricula. Defer hierarchical modules or a Domain redesign unless separately approved; provide a proposal if evidence warrants it.
-   [x] Validate goal/route contracts, invalid proposals, prerequisite checks, self-report versus demonstrated knowledge and compatibility with existing programs.

Acceptance: fixtures demonstrate measurable goal criteria and coherent, appropriately scoped topics; mastery is not credited without the required evidence. Any remaining structural limitations are documented rather than hidden.

## 7. Stage 4 --- Prompt and provider contract optimization

Status: Completed. Issues: OD-005, OD-006, OD-013.

-   [x] Inventory prompts, response schemas and effective model parameters by operation. Establish a baseline using offline fixtures and available recorded metrics. Do not treat character count alone as a latency diagnosis.
-   [x] Resolve `RequestedFormat = null` semantics and ownership of exercise format selection. Test automatic and explicit formats; align prompt, schema, parser and deterministic validation.
-   [x] Reduce redundant or contradictory instructions. Consider specialized schemas/templates for open and choice exercises without multiplying sequential provider calls unnecessarily. Preserve pedagogy, safety, localization, source honesty and assessment constraints.
-   [x] Evaluate operation-specific output token ceilings and effective reasoning parameters. Choose limits from measured output sizes and safety margins; test truncation/invalid output handling. Preserve financial admission and checkpoint compatibility.
-   [x] Add or update golden fixtures and compare quality, token usage and latency where evidence exists. Use fake-provider tests for contract correctness; live-provider quality and performance require authorized evaluation.

Acceptance: unambiguous format contracts, passing regression fixtures, no loss of essential pedagogical safeguards. Any claimed latency or cost improvement must be supported by comparable measurements; otherwise label it unverified.

## 8. Stage 5 --- End-to-end acceptance and second audit

Status: Pending; external operator gate. Depends on stages 1--4.

-   [ ] Run full build and test suite, including PostgreSQL integration tests per repository instructions. Record skipped tests and reasons.
-   [ ] Review architecture, migration compatibility, deployment and rollback procedure, pending provider checkpoints and queue drain requirements. Prepare deployment instructions; do not deploy automatically.
-   [ ] Operator: deploy to a test bot, enable Tester Mode, create a new program and successfully generate its first lesson.
-   [ ] Operator: complete 10--20 exercises with correct, partially correct and incorrect answers; request explanations/hints, pause/resume, change topic, inspect reviews and switch language. Include keyboard/NVDA checks and restart recovery where feasible.
-   [ ] Operator: obtain `/export`, `/export recent`, `/export last`; provide archives for follow-up analysis. Keep diagnostic files private and outside version control.
-   [ ] Audit actual evidence accumulation, mastery, adaptive decisions, progress, latency, budget accounting and diagnostic completeness. Compare with baseline; use percentiles only when sample size is adequate.
-   [ ] Record new defects separately, re-open unresolved issues and propose next work. Do not declare real-provider or pedagogical quality verified solely from fake tests.

Acceptance: first lesson generation and the real learning journey are verified, or remaining blockers are explicitly documented. No production rollout claim without operator confirmation.

## 9. Progress ledger

  Stage   Status    Verification evidence   Remaining blockers
  ------- --------- ----------------------- ----------------------------
  1       Completed   PostgreSQL slow call and recovery   Operator smoke pending
  2       Completed   See dated verification entry below
  3       Completed   See dated verification entry below
  4       Completed   See dated verification entry below
  5       Pending   ---                     Operator/test bot required

For each completed stage append: date, issue IDs and disposition, changed files/migrations, test commands and results, deviations, residual risks, manual checks, recommended Conventional Commit message. Keep the original findings traceable. Do not commit the diagnostic archive or its raw contents.

### 2026-11-24 — OD-001, OD-002, OD-008

Confirmed the 15 s outer cap, 30 s lease and generic assessment reply on generation failure. The historical cancellation source remains unknown. Default worker budget is now 75 s including admission and source I/O; the adapter retains 60 s. Attempt admission extends the fenced lease for the call plus the ordinary lease period. Timeout, caller cancellation, transport ambiguity and a stale lease have separate evidence; a lost lease is discovered at a DB fence. Reclaim does not replay a recorded ambiguous attempt. No migration. An explicit new command uses ordinary owner and freshness checks.

Release build passed with zero warnings. Seven focused PostgreSQL cases passed, including a 16 s generation with a competing claim, a non-cooperative adapter, uncertain/retryable failures and lease loss. The original sandboxed red run failed to produce a test result; baseline limits were verified in source. Review found the recovery admission path could retry recorded cancellations and the adapter could retry an ambiguous transport failure; both corrected. Production smoke requires the operator.

### 2026-11-24 — OD-007, OD-009–012

Post-commit review of b83b9ab checked fenced admission, reconciliation and failure paths. OD-007 reproduced before the fix with TelegramNavigation's actual serialized markup. Button field casing differs from raw Bot API JSON. Detailed capture now accepts both shapes and exports verified action metadata. Settings resolution, committed language changes, consultation inputs and the effective generation policy are observable; historical Telegram language and hidden model reasons are unavailable. Schema 1 additions use the existing read-only snapshot, redaction and owner fence. Monthly quota uses both retained attempts and carryover. Selected timeline attempt counts deduplicate by physical ID.

Seven diagnostic unit tests passed in Release. All 15 PostgreSQL tester/export tests passed in Debug, including a synthetic worker deadline, both owners and all three modes. Review caught a missing financial source FK in the fixture; the corrected fixture uses a real provider attempt. No migration. Full real-provider diagnostic acceptance is pending.

### 2026-11-24 — OD-003, OD-004, OD-014

Post-commit review of 9f1bc9e checked owner predicates, carryover, redaction and the export receipt's locale provenance. The complete provider integration class against the reliability change passed 244 cases with one opt-in live test skipped.

Confirmed a fixed generic criterion template. The existing planner response now accepts a bounded optional observable criterion and asks for task-sized topics and necessary prerequisites. The Application accepts it only in consultation; old proposals and invalid wording keep the consultation fallback. Exchange and geometry fixtures preserve the original goal and check concrete outcomes. Confirmed in PostgreSQL that one correct open recognition answer credited a broad topic before the fix. Newly assessed question-scoped recognition now requires deeper evidence to close that topic. Older committed credits and observations remain intact. Demonstrated mastery already requires independent evidence; no separate mastery defect reproduced. OD-014 remains a structural limit: keep the flat 12-topic introductory route; hierarchical curricula deferred.

Three integration and 32 rubric, program and model cases passed in Debug. Release PostgreSQL regression also verified the migration refuses lossy downgrade. Review covered tracked and stored completion queries, follow-up selection, checkpoint stamps and old-row defaults.

### 2026-11-24 — OD-005, OD-006, OD-013

Post-commit review of 11a6f20 checked recognition history and model compatibility. Confirmed the null user preference could accompany a choice schema without an explicit effective format. The provider input now distinguishes automatic and explicit requests from the selected response contract, including open fallbacks. Conditional teaching, transfer, project and shared-skill instructions are omitted for absent contexts. Existing checkpoint inputs and financial admission are unaffected. The expanded fake-HTTP fixtures failed on the old contract, then all 59 adapter tests passed in Release. The inventory documents operations, schemas, output caps and reasoning defaults. Two interrupted generations without measured successful output cannot support tuning generation token caps or attributing latency to prompt length. OD-005 latency explanation unconfirmed; OD-013 lower non-intent caps deferred.

### 2026-11-24 — cross-cutting review corrections

Review of 3c28315 and of the aggregate change found two Major issues. The original semaphore shutdown check could race with slot acquisition or final release, and the prepared Application selection reasons were lost before provider execution. The admission gate now serializes shutdown with acquisition and release. Preparation reports the actual format branch and starting-level source separately from adapter schema selection; these fields do not affect persisted fingerprints or provider input. Additional corrections distinguish the hosted stopping token from another caller and an observed deadline from a transport cancellation. The budget now includes pending known USD reservations and the number lacking a price.

A synthetic private Telegram first-lesson request traversed durable ingress, real PostgreSQL admission, a deadline-aware fake provider, guarded completion, journal flush and last-receipt export. The resulting export identified the worker deadline, format reason and unknown cost. It contained one saved program, no lesson and the contextual retry instruction. The final focused Release run passed 4 PostgreSQL and 65 unit cases. Release solution build passed with zero warnings and zero errors.
