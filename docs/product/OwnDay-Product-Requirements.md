# OwnDay Product Requirements Document

**Version:** 4.1
**Status:** Experimental MVP
**Primary platform:** Telegram
**Purpose:** Validate a complete accountability mechanism for self-directed work.

---

# 1. Product Overview

## 1.1 Product Summary

OwnDay is a personal execution system for people pursuing **self-directed goals and projects** where external accountability is weak or absent.

The initial target user is a solo software developer building their own product, such as a SaaS.

The user decides:

* what matters;
* what outcome to pursue;
* what to commit to today;
* when to work on it.

OwnDay helps the user turn those decisions into actual execution.

It does this by combining:

* explicit daily Commitments;
* lightweight decomposition into Steps;
* deliberate Focus Sessions;
* adaptive accountability;
* recovery from execution drift;
* mandatory outcome accounting;
* immediate reflection;
* adaptation based on execution evidence.

The core loop is:

**Choose → Commit → Focus → Execute → Recover when needed → Account → Reflect → Adapt**

---

# 2. Product Objective

OwnDay exists to reduce the gap between:

> **I decided to do this.**

and:

> **This actually happened.**

The objective is not maximum task completion or maximum utilization of time.

The objective is:

> **Meaningful progress toward outcomes chosen by the user.**

Execution reliability matters insofar as it produces meaningful progress.

---

# 3. Target User

## Primary Persona

A solo software developer working on a self-directed software product with substantial autonomy and little or no external accountability.

Typical example:

> A developer building their own SaaS.

The user decides what to build and when to work on it.

There may be:

* no manager;
* no client;
* no team;
* no externally imposed deadline;
* no immediate consequence for postponement.

As a result, important work may be repeatedly deferred despite genuine intention to complete it.

---

# 4. Primary Problem

Self-directed work is easy to postpone because intention alone creates weak behavioral pressure.

The user may know what matters and create reasonable plans but still fail to execute because of:

* difficulty starting;
* avoidance;
* distraction;
* unclear next actions;
* commitments that are too large or vague;
* underestimated complexity;
* unexpected blockers;
* unknown work;
* interruptions;
* unrealistic expectations about available time;
* plans becoming invalid after new information appears.

Traditional task systems primarily represent what should be done.

OwnDay must additionally help answer:

> **What did I commit to?**

> **What is the next executable action?**

> **When am I actually going to work on it?**

> **Did I start?**

> **Is execution still on course?**

> **If execution broke down, can it be recovered?**

> **What actually happened?**

> **What should change next time?**

---

# 5. Core Product Hypothesis

The MVP tests the following hypothesis:

> **A structured accountability loop combining explicit daily Commitments, executable next Steps, deliberate Focus Sessions, adaptive intervention, recovery, mandatory outcome accounting, and immediate reflection can materially improve execution of meaningful self-directed work without creating unacceptable interaction burden.**

The product is successful only if OwnDay changes actual execution, not merely the user's awareness of unfinished work.

---

# 6. Behavioral Mechanism

The MVP is built around six connected mechanisms.

## 6.1 Commitment Creates Accountability

The user explicitly promises a concrete outcome for today.

The Commitment cannot silently disappear or be rewritten.

---

## 6.2 Steps Reduce Activation Energy

A difficult Commitment is decomposed into a small number of concrete Steps.

OwnDay keeps a clear Next Step available.

The user does not need to mentally reconstruct the entire problem before starting.

---

## 6.3 Focus Sessions Create Execution Context

A Focus Session represents an explicit attempt to work on a Commitment.

It gives OwnDay enough temporal context to distinguish:

> "The user has not started yet"

from:

> "The user is not supposed to be working on this now."

---

## 6.4 Adaptive Accountability Detects Drift

OwnDay does not continuously interrupt the user.

The default during execution is silence.

OwnDay intervenes only when:

1. available evidence indicates meaningful execution risk; and
2. there is a concrete intervention that could plausibly improve execution.

---

## 6.5 Recovery Attempts to Restore Execution

A detected deviation is not immediately treated as failure.

OwnDay first attempts to help the user return to the Commitment when it remains valid.

---

## 6.6 Outcome and Reflection Close the Loop

Every Commitment receives an explicit outcome.

Meaningful divergence is reflected on while the relevant context is still available.

Evidence from the current attempt informs the next one.

---

# 7. Core Domain Concepts

## 7.1 Goal Context

Goal Context explains the broader result toward which a Commitment contributes.

Example:

> Ship a usable OwnDay MVP.

Goal Context exists to preserve meaning and support prioritization.

### MVP Constraint

The MVP does not require a full Goal management subsystem.

Goal Context may initially be lightweight.

Monthly and weekly Goal lifecycle management is outside MVP scope.

---

# 8. Commitment

## 8.1 Definition

A Commitment is:

> **An explicit promise by the user to produce a specific meaningful outcome within the current day.**

It represents a result, not merely an activity.

### Weak Commitment

> Work on persistence.

### Better Commitment

> Commitments are persisted in PostgreSQL and survive application restart.

The second statement describes something that should become observably true.

---

## 8.2 Commitment Granularity

A Commitment should be:

* meaningful enough to represent real progress;
* concrete enough to evaluate;
* limited enough that completion within the current day is reasonably plausible.

A Commitment should answer:

> **What should become true today?**

A Step should answer:

> **What should I do next to make that happen?**

If a proposed Commitment obviously requires many days, OwnDay should help narrow it to a meaningful daily outcome.

---

## 8.3 Daily Boundary

All MVP Commitments are daily.

Before the day closes, an active Commitment must eventually become:

* **Fulfilled**
* **Renegotiated**
* **Failed**
* **Unaccounted**

An active Commitment does not silently roll over into the next day.

If additional work is required tomorrow, the user makes a new Commitment or explicitly renegotiates the current one into a new linked Commitment.

---

## 8.4 Rule of Three

The user may have at most:

> **3 active daily Commitments.**

Three is a maximum, not a target.

OwnDay must never encourage the user to fill unused slots merely because they exist.

One meaningful Commitment is a valid day.

---

# 9. Steps

## 9.1 Definition

A Step represents part of the currently understood path toward fulfilling a Commitment.

Example:

**Commitment**

> Commitments persist across application restarts.

**Steps**

1. Define persistence model.
2. Configure EF Core mapping.
3. Create migration.
4. Implement persistence.
5. Verify restart behavior.

---

## 9.2 Purpose

Steps exist to:

* reduce difficulty starting;
* expose the Next Step;
* make large work approachable;
* capture discovered work;
* make intermediate progress visible;
* improve later execution analysis.

---

## 9.3 Accountability Boundary

> **Accountability applies to Commitments, not Steps.**

Steps are implementation hypotheses.

They may change as understanding improves.

Completing a Step is progress.

Failing to complete an individual Step is not itself a Commitment failure.

---

## 9.4 Flat Decomposition

The MVP supports only one level of Steps.

No:

* subtasks;
* nested trees;
* dependencies;
* epics;
* stories;
* task hierarchies.

If a Step itself requires substantial decomposition, it should normally be replaced by several sibling Steps or the Commitment should be reconsidered.

---

## 9.5 Step Evolution

A Step may be:

* **Pending**
* **Active**
* **Done**
* **Removed**

The system also records its origin:

* **Planned**
* **Discovered**

New Steps may be discovered during execution.

This is expected and is not itself evidence of bad planning.

---

# 10. Next Step

Every active Commitment should, whenever possible, have a concrete **Next Step**.

The Next Step is the immediate executable action most likely to move the Commitment forward.

Example:

Instead of:

> Return to persistence.

OwnDay should prefer:

> Next Step: create the EF Core configuration for Commitment.

The Next Step is especially important for:

* starting;
* recovering after distraction;
* resuming after interruption;
* reducing ambiguity.

AI may infer or propose the Next Step from existing Steps and execution context.

The user may correct it.

---

# 11. Focus Session

## 11.1 Definition

A Focus Session is:

> **A deliberate attempt to make focused progress toward one Commitment.**

A Commitment may require multiple Focus Sessions.

A Focus Session may complete multiple Steps.

A Focus Session ending does not imply Commitment failure.

---

## 11.2 Focus Session Data

A Focus Session may contain:

* associated Commitment;
* planned start;
* expected duration;
* intended Next Step;
* actual start;
* actual end;
* end reason.

Expected duration represents allocated capacity, not a deadline.

---

## 11.3 Starting Immediately

A Focus Session does not need to be scheduled in advance.

If the user says:

> Start now.

OwnDay creates and starts the Focus Session immediately.

The UX should optimize for execution rather than scheduling ceremony.

---

## 11.4 Focus Session Is Not Calendar Management

OwnDay does not require the entire day to be time-blocked.

Focus Sessions exist only for meaningful Commitment execution.

Activities such as:

* breakfast;
* routine email;
* commuting;
* ordinary breaks;

do not need to become Focus Sessions.

---

# 12. Observability Model

OwnDay must distinguish observed facts from assumptions.

In a Telegram-only MVP, OwnDay cannot directly observe what the user is doing on their computer.

It knows only:

* what the user reports;
* what the user confirms;
* messages and interaction events;
* scheduled temporal events;
* elapsed time;
* previous execution evidence.

Therefore:

> **Absence of activity evidence is not evidence of inactivity.**

After the user explicitly starts a Focus Session, OwnDay assumes execution continues unless:

* the user reports contrary evidence;
* a relevant predefined checkpoint occurs;
* the Focus Session reaches an expected boundary;
* another meaningful signal requires attention.

OwnDay must not claim to know that the user is distracted, procrastinating, or working unless sufficient evidence exists.

---

# 13. Execution State Model

## 13.1 Commitment State

`Draft`

→ `Active`

→ one of:

* `Fulfilled`
* `Renegotiated`
* `Failed`
* `Unaccounted`

---

## 13.2 Step State

`Pending`

→ `Active`

→ `Done`

or:

`Pending / Active`

→ `Removed`

---

## 13.3 Focus Session State

`Planned`

→ `AwaitingStart`

→ `Active`

→ one of:

* `Completed`
* `Interrupted`
* `Abandoned`

An immediate Focus Session may transition directly:

`Active`

without first being Planned.

---

## 13.4 Execution Risk State

Execution accountability uses a separate conceptual state:

`Normal`

→ `AtRisk`

→ `Recovery`

and then either:

`Recovery → Normal`

or:

`Recovery → DecisionRequired`

`DecisionRequired` leads to an explicit decision such as:

* continue;
* create another Focus Session;
* renegotiate;
* fail.

Execution risk state does not itself change Commitment outcome.

---

# 14. Commitment Creation

The user shall be able to describe a desired result in natural language.

OwnDay may use AI to transform activity-oriented wording into an outcome-oriented proposal.

Example:

**User**

> Work on persistence tomorrow.

**OwnDay**

> I suggest making the outcome explicit:
>
> **Commitments are stored in PostgreSQL and survive application restart.**
>
> Use this as tomorrow's Commitment?

The user must explicitly confirm the Commitment.

Until confirmation it remains a Draft.

---

# 15. AI-Assisted Decomposition

When decomposition is likely to reduce execution friction, OwnDay may propose Steps.

Example:

> I see the following path:
>
> 1. Define persistence model.
> 2. Configure EF Core.
> 3. Create migration.
> 4. Implement persistence.
> 5. Verify restart behavior.
>
> Use this decomposition?

The user may:

* accept;
* edit;
* reject.

OwnDay should prefer generating a reasonable decomposition itself rather than forcing the user to manually construct one.

Decomposition remains optional.

---

# 16. Starting Execution

When the user is ready to work, OwnDay should establish a Focus Session with minimal friction.

Example:

> Next Commitment: persistence.
>
> Next Step: define the persistence model.
>
> Start a Focus Session now?

After confirmation:

> Focus Session started.
>
> I’ll stay quiet unless something needs your attention.

The actual start time is persisted.

---

# 17. Adaptive Accountability

## 17.1 Core Rule

OwnDay shall not use fixed periodic user check-ins.

The core policy is:

> **Intervene only when execution risk is elevated and a useful action is available.**

Conceptually:

**Intervention = Evidence of Risk + Actionable Response**

---

# 18. Intervention Ladder

Adaptive accountability follows four levels.

## Level 0 — Silence

Default during execution.

No meaningful risk signal exists.

OwnDay does nothing.

---

## Level 1 — Check

A weak execution-risk signal exists.

OwnDay asks for the minimum information needed.

Example:

> Your Focus Session was planned to start 15 minutes ago. Did you start?

The objective is information, not pressure.

---

## Level 2 — Recovery

Execution drift is confirmed while the Commitment remains valid.

OwnDay attempts to restore execution.

Example:

> The Commitment still stands.
>
> Your Next Step is to create the EF Core configuration.
>
> Start with that now?

The objective is to reduce activation energy and restore action.

---

## Level 3 — Decision

Recovery failed or new information may invalidate the original execution plan.

OwnDay asks for an explicit decision.

Typical options:

* Continue;
* Renegotiate;
* Fail.

The objective is to prevent silent abandonment.

---

# 19. MVP Intervention Triggers

The MVP uses a small number of transparent triggers rather than an opaque behavioral score.

## 19.1 Start Not Observed

A planned Focus Session reached its start time, but start was not confirmed after a configurable grace period.

Possible response:

**Check.**

---

## 19.2 Explicit Drift

The user reports activity clearly unrelated to the active Commitment.

A short deviation does not automatically trigger Recovery.

Repeated or prolonged reported drift may.

---

## 19.3 Extended Break

The user explicitly reports a break and later indicates that the break has turned into unintended avoidance or fails to return after an explicitly agreed return point.

OwnDay must not infer an extended break merely from silence.

---

## 19.4 Explicit Difficulty

Examples:

> I'm stuck.

> I can't make myself start.

> I'm avoiding this.

OwnDay may immediately enter Recovery.

---

## 19.5 Blocker or New Information

The user reports evidence that may materially change the feasibility or validity of the Commitment.

OwnDay evaluates whether a Decision is required.

---

## 19.6 Focus Session Boundary

Expected session duration expires.

OwnDay may ask whether to:

* continue;
* end the Session;
* evaluate progress.

This is not automatically an AtRisk event.

---

## 19.7 Outcome Due

The daily Commitment reaches its accounting boundary without an outcome.

OwnDay requires explicit accounting.

---

## 19.8 Explicit Help

The user asks:

> What now?

> What should I do?

> Help me get started.

OwnDay responds immediately using current Commitment, Steps, Next Step and execution context.

---

# 20. Temporary Increased Accountability

After confirmed drift, the user and OwnDay may establish a short recovery check.

Example:

**User**

> I got distracted. Returning now.

**OwnDay**

> Start with the current Next Step.
>
> I'll check once in 15 minutes whether you managed to restart.

This creates a temporary accountability contract.

If execution resumes, OwnDay returns to Level 0 — Silence.

The MVP shall not perform permanent 15-minute monitoring.

---

# 21. Recovery

Recovery is a first-class part of execution.

Its purpose is:

> **Restore execution before an AtRisk Commitment becomes a failed Commitment.**

A Recovery intervention should usually:

1. confirm that the Commitment remains valid;
2. identify the current Next Step;
3. reduce the immediate action to something executable;
4. obtain a concrete restart decision.

Example:

> Nothing about the Commitment has changed.
>
> Next Step: create `CommitmentConfiguration`.
>
> Open the project and start there. Ready?

Successful recovery returns execution to `Normal`.

---

# 22. Renegotiation

Renegotiation is appropriate when information available after commitment materially changes the original agreement.

Possible dimensions include:

* Outcome;
* Scope;
* Constraints;
* Execution horizon;
* assumptions underlying feasibility.

Discovering additional Steps does **not** automatically require renegotiation.

Renegotiation becomes relevant when discovered work materially changes the feasibility of achieving the promised outcome today.

Example:

> The Commitment remains technically valid, but the newly discovered architecture work makes today's outcome unrealistic.

The user decides whether to renegotiate.

OwnDay may recommend it but may not silently perform it.

---

# 23. Renegotiation Record

A Renegotiation shall preserve:

**Original Commitment**

→ **Execution Evidence**

→ **Material New Information**

→ **User Explanation**

→ **Decision**

→ **Revised Commitment**, if any.

The original Commitment remains immutable in history.

---

# 24. Focus Session Completion

When a Focus Session ends, OwnDay may summarize relevant evidence.

Example:

> Focus Session ended.
>
> Completed:
>
> * persistence model;
> * EF mapping;
> * migration.
>
> Discovered:
>
> * persistence boundary requires additional work.
>
> Current Next Step:
>
> **Define repository boundary.**
>
> The Commitment remains active.

Ending a Session does not require a Commitment outcome if another realistic execution opportunity remains within the day.

---

# 25. Visible Progress

OwnDay should represent progress through concrete evidence rather than artificial percentages.

Prefer:

> **Progress made**
>
> ✓ Persistence model
> ✓ EF mapping
> ✓ Migration
>
> **Currently known next work**
>
> → Implement persistence
> → Verify restart behavior

Avoid:

> 60% complete.

The set of known Steps may change.

OwnDay must not fabricate precision.

---

# 26. Outcome Accounting

Every daily Commitment requires an outcome.

## Fulfilled

The promised outcome was achieved.

OwnDay shall require explicit confirmation.

AI may propose:

> Based on your update, this looks fulfilled. Confirm?

It may not set Fulfilled autonomously.

---

## Renegotiated

Materially new information justified changing the original agreement.

The original Commitment remains preserved.

---

## Failed

The promised outcome was not achieved and the original Commitment was not validly replaced through renegotiation.

Failure is recorded as evidence, not moral judgment.

---

## Unaccounted

The user did not provide the required outcome.

Silence never means Fulfilled.

---

# 27. Immediate Reflection

Reflection should happen close to meaningful divergence while context remains available.

It should be triggered selectively for:

* Failed Commitments;
* Renegotiated Commitments;
* meaningful Recovery events;
* repeated execution patterns.

OwnDay shall first use existing evidence.

Prefer:

> It looks like the persistence work expanded because an architecture boundary was unknown when you committed. Is that accurate?

over:

> Why didn't you finish?

The user may correct the interpretation.

---

# 28. AI Responsibilities

AI exists primarily to reduce interaction cost and convert unstructured execution information into useful proposals.

AI may:

* clarify Commitment wording;
* propose outcome-oriented formulations;
* propose flat decomposition;
* identify a likely Next Step;
* interpret user-reported activity;
* identify possible blockers;
* summarize Focus Sessions;
* summarize execution evidence;
* formulate reflection hypotheses;
* propose adaptations.

---

# 29. AI Authority Boundary

AI interpretation is not authoritative domain state.

The architecture should conceptually distinguish:

**AI interpretation**

→ what available evidence may mean.

**Policy decision**

→ whether OwnDay should intervene.

**Domain transition**

→ what state changes are permitted.

AI shall not autonomously:

* create confirmed Commitments;
* change confirmed Commitments;
* declare a user distracted without evidence;
* decide whether renegotiation is legitimate;
* mark a Commitment Fulfilled;
* infer failure from silence;
* fabricate execution activity.

The user retains authority over consequential decisions.

---

# 30. Execution Evidence

OwnDay shall persist evidence only when it can reasonably support:

* accountability;
* recovery;
* reflection;
* adaptation;
* future execution analysis.

Possible evidence includes:

* Commitment confirmation;
* Step creation;
* Step completion;
* discovered Step;
* Focus Session planned;
* Focus Session started;
* Focus Session ended;
* reported interruption;
* reported blocker;
* reported drift;
* Recovery intervention;
* Recovery result;
* Renegotiation;
* Outcome.

Execution Evidence is not intended to become a continuous activity log.

---

# 31. "What Now?"

The user may ask OwnDay for immediate execution guidance.

OwnDay should consider:

* today's active Commitments;
* current Commitment;
* current Focus Session;
* completed Steps;
* Next Step;
* known blockers;
* recent execution evidence.

The default answer should contain **one concrete recommended action**, not a large backlog.

Example:

> Continue the persistence Commitment.
>
> Next Step: create the EF Core configuration.
>
> Start a Focus Session now?

---

# 32. Adaptation

Evidence from execution may influence:

* Commitment granularity;
* decomposition;
* Next Step;
* Focus Session duration;
* Focus Session timing;
* number of daily Commitments;
* future estimates.

Adaptation should initially be recommendation-based.

The MVP shall not automatically rewrite future plans based on inferred behavioral patterns.

---

# 33. History

The user shall be able to inspect recent Commitments.

For each Commitment, OwnDay should preserve enough information to reconstruct:

**Commitment**

→ **Steps**

→ **Focus Sessions**

→ **meaningful Execution Evidence**

→ **Interventions / Recovery**

→ **Outcome**

→ **Reflection**

History exists primarily to support learning and accountability.

It is not an activity surveillance log.

---

# 34. Metrics

Metrics evaluate OwnDay, not the user's worth or productivity.

## 34.1 Outcome Metrics

### Fulfilled Commitment Rate

Proportion of due Commitments explicitly Fulfilled.

### Silent Abandonment Rate

Proportion becoming Unaccounted.

---

## 34.2 Execution Metrics

### Focus Session Start Rate

Proportion of planned Focus Sessions that actually start.

### Assisted Start

A planned execution did not start, OwnDay intervened, and execution subsequently started.

### Unassisted Start

Execution started without intervention.

---

## 34.3 Recovery Metrics

### Recovery Attempt Rate

How often confirmed execution drift results in Recovery.

### Assisted Recovery

Drift occurred, OwnDay intervened, and execution resumed.

### Recovery Success Rate

`Successful Recoveries / Recovery Attempts`

---

## 34.4 Completion Context

### Assisted Completion

A Commitment became AtRisk, received a Recovery intervention, and was subsequently Fulfilled.

### Unassisted Completion

A Commitment was Fulfilled without requiring Recovery.

These metrics do not establish perfect causality but provide evidence about where OwnDay may be changing execution.

---

## 34.5 Interaction Guardrails

Measure:

* interventions per Commitment;
* user responses required per Commitment;
* ignored interventions;
* interventions explicitly reported as unnecessary;
* voluntary continued usage.

The product should minimize interaction while preserving behavioral impact.

---

# 35. User-Perceived Causal Value

OwnDay should occasionally, but not after every intervention, ask whether a meaningful intervention helped.

Example:

> Did that check-in help you return to the work you intended to do?

Possible answers may be lightweight:

* Yes;
* No;
* Not sure.

This provides evidence about whether OwnDay is perceived to change actual behavior.

---

# 36. MVP Validation

The MVP should be tested on real meaningful self-directed work rather than synthetic tasks.

Primary questions:

### Execution

Does OwnDay cause important work to start that otherwise would have been postponed?

### Recovery

Can OwnDay restore execution after drift?

### Completion

Do recovered Commitments sometimes become real completed outcomes?

### Decomposition

Does having a clear Next Step reduce initiation friction?

### Focus

Do Focus Sessions create useful execution structure without turning OwnDay into a calendar?

### Accountability

Does mandatory outcome accounting reduce silent abandonment?

### Reflection

Does immediate reflection produce useful changes to subsequent execution?

### Friction

Is the benefit large enough that users voluntarily continue using the system?

---

# 37. MVP Functional Scope

The Experimental MVP includes:

* Telegram interface;
* minimal onboarding;
* lightweight Goal Context;
* maximum three daily Commitments;
* outcome-oriented Commitment creation;
* explicit Commitment confirmation;
* flat Steps;
* AI-assisted decomposition;
* discovered Steps;
* Next Step;
* visible evidence-based progress;
* Focus Sessions;
* planned and immediate Focus Session starts;
* expected Focus Session duration;
* adaptive accountability;
* Intervention Ladder;
* temporary increased accountability;
* Recovery;
* blockers and new-information handling;
* explicit Renegotiation;
* outcome accounting;
* Fulfilled / Renegotiated / Failed / Unaccounted;
* immediate Reflection;
* What Now;
* execution history;
* structured Execution Evidence;
* basic experimental metrics.

---

# 38. Non-Goals

The Experimental MVP excludes:

* general-purpose task management;
* project backlogs;
* nested task decomposition;
* Step dependencies;
* epics and stories;
* kanban boards;
* team collaboration;
* managers assigning work;
* comprehensive life planning;
* monthly planning workflow;
* weekly planning workflow;
* full Goal lifecycle management;
* detailed calendar management;
* scheduling every activity in the day;
* calendar integrations;
* fixed 15-minute activity reporting;
* continuous activity monitoring;
* desktop surveillance;
* browser monitoring;
* automatic behavioral judgments;
* complex risk scoring;
* machine-learned intervention policy;
* automatic Commitment creation;
* automatic Renegotiation;
* automatic rescheduling;
* productivity scores;
* reliability scores;
* completion percentages based on Steps;
* streaks;
* XP;
* badges;
* gamification;
* complex Personal Execution Model;
* analytics dashboards;
* project-management integrations;
* social accountability;
* web application;
* native mobile application.

---

# 39. Product Boundaries

## 39.1 OwnDay Is Not a Task Manager

Steps serve Commitments.

The product does not optimize for:

> How many tasks did you complete?

It cares whether work changed the meaningful outcome.

---

## 39.2 OwnDay Is Not a Calendar

Focus Sessions establish execution context.

The user does not need to schedule their entire day.

---

## 39.3 OwnDay Is Not Surveillance

OwnDay operates on deliberately provided execution evidence.

Silence during an active Focus Session is not interpreted as inactivity.

---

## 39.4 OwnDay Is Not an Autonomous Manager

OwnDay creates accountability around decisions made by the user.

It does not become the source of those decisions.

---

# 40. Key Risks

## R1 — Accountability Is Too Weak

Users may acknowledge interventions without changing behavior.

The key measure is resumed execution, not conversational compliance.

---

## R2 — Accountability Is Too Intrusive

Frequent intervention may itself damage focus.

Mitigation:

> **Silence is the default.**

---

## R3 — Decomposition Becomes Planning Procrastination

Users may spend time organizing Steps rather than executing.

Mitigation:

* flat decomposition only;
* AI proposes Steps;
* decomposition remains optional;
* always expose a Next Step;
* move quickly toward a Focus Session.

---

## R4 — Commitments Become Too Small

Users may optimize for easy completion.

Mitigation:

Commitments must represent meaningful outcomes rather than trivial actions.

---

## R5 — Commitments Become Too Large

Large Commitments weaken daily accountability.

Mitigation:

OwnDay helps narrow outcomes to something plausibly achievable within the current day.

---

## R6 — Renegotiation Becomes Escape

Users may use renegotiation to avoid uncomfortable work.

Mitigation:

Preserve the full chain:

**Original Commitment → Evidence → New Information → Explanation → Decision**

OwnDay does not judge legitimacy automatically.

---

## R7 — AI Misinterprets Behavior

LLM inference may confuse investigation, interruption, difficulty and avoidance.

Mitigation:

AI interpretations remain hypotheses.

Consequential state transitions require deterministic policy and/or explicit user decisions.

---

## R8 — False Observability

A Telegram bot may behave as though it knows what the user is doing when it does not.

Mitigation:

Explicitly model observed evidence versus assumptions.

---

## R9 — OwnDay Becomes a Task Manager

Step functionality may expand into generic project management.

Mitigation:

Steps exist only within Commitments and have no independent accountability lifecycle.

---

## R10 — OwnDay Becomes a Calendar

Focus Session functionality may expand into complete time management.

Mitigation:

Only meaningful execution periods are modeled.

---

# 41. Core UX Rules

1. The user owns direction.
2. Commitments describe outcomes, not vague activities.
3. MVP Commitments are daily.
4. Maximum three active daily Commitments.
5. Three is a maximum, not a quota.
6. A Draft is not a Commitment.
7. Confirmed Commitments cannot silently change.
8. Steps are hypotheses about execution.
9. Accountability belongs to Commitments, not Steps.
10. Keep decomposition flat.
11. Maintain a concrete Next Step whenever possible.
12. Focus Sessions represent execution attempts, not deadlines.
13. Starting execution should require minimal ceremony.
14. Silence is the default during an active Focus Session.
15. Silence is not evidence of inactivity.
16. Intervention requires evidence and an actionable purpose.
17. Prefer Recovery before declaring failure.
18. Recovery should return the user to a concrete Next Step.
19. Increased accountability should be temporary.
20. New information may justify Renegotiation.
21. Discomfort alone does not automatically justify Renegotiation.
22. Ending a Focus Session does not imply Commitment failure.
23. Silence never means Fulfilled.
24. Fulfilled requires explicit confirmation.
25. A Commitment cannot silently roll into tomorrow.
26. Show concrete progress rather than invented percentages.
27. Ask only when existing evidence is insufficient.
28. AI interpretations are hypotheses, not facts.
29. Reflection should occur while relevant context remains available.
30. Every intervention should improve execution, establish accountability, or collect information necessary for a consequential decision.

---

# 42. MVP Success Criteria

The Experimental MVP succeeds when there is credible evidence that OwnDay affects actual execution.

Specifically:

## Initiation

Users begin meaningful self-directed work that they would otherwise have postponed.

## Recovery

OwnDay interventions sometimes restore execution after meaningful drift.

## Completion

Recovered execution sometimes contributes to Fulfilled Commitments.

## Reduced Silent Abandonment

Important Commitments are less likely to disappear without an explicit outcome.

## Lower Activation Energy

Steps and Next Step make difficult work easier to begin.

## Useful Focus Structure

Focus Sessions provide execution context without requiring comprehensive calendar management.

## Useful Reflection

Failures, Renegotiations and Recovery events produce evidence that changes subsequent decisions.

## Acceptable Friction

Users voluntarily continue using OwnDay and judge its accountability benefit greater than its interaction burden.

## Causal Value

Users can identify concrete moments where OwnDay changed what they actually did.

---

# 43. MVP Success Definition

OwnDay Experimental MVP succeeds if its accountability mechanism demonstrates that:

> **A user working without external supervision can make a meaningful daily Commitment, understand the next executable action, deliberately enter focused execution, receive intervention when execution genuinely breaks down, recover when possible, account for the real outcome, and use the resulting evidence to make the next attempt better.**

The desired experience is not:

> OwnDay keeps asking whether I am working.

It is:

> **OwnDay mostly leaves me alone when I am executing, but when I start drifting away from something I deliberately decided matters, it helps me notice, return to a concrete action, and prevents the commitment from quietly disappearing.**

That is the core behavior the MVP must validate.
