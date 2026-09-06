# OwnDay Domain Model (MVP)

**Version:** 1.2  
**Status:** Experimental MVP  
**Primary interface:** Telegram  
**Domain approach:** Domain-Driven Design  

---

# 1. Purpose

This document defines the domain model for the OwnDay Experimental MVP.

The model is aligned with the current Product Vision, PRD 4.1, and MVP User Flows 2.0.

OwnDay is a personal execution system for self-directed work. Its core concern is the execution gap between a meaningful decision and a real-world outcome.

The MVP execution loop is:

**Choose → Commit → Focus → Execute → Recover when needed → Account → Reflect → Adapt**

The domain model therefore centers on a daily **Commitment**, while deliberately separating:

- current domain state from historical reconstruction;
- observed execution evidence from domain events;
- Commitment lifecycle from Commitment outcome;
- execution risk from Commitment outcome;
- domain decisions from AI interpretation and application orchestration.

The central Aggregate Root of the MVP is **Commitment**.

---

# 2. Domain Modeling Principles

## 2.1 Commitment Is the Accountability Boundary

A Commitment represents an explicit promise by the user to produce a specific meaningful outcome within the current day.

Accountability applies to Commitments, not to Steps, Focus Sessions, or arbitrary activity.

A Commitment cannot silently disappear, silently change, or silently roll into another day.

---

## 2.2 Aggregate Boundary Is Not History Boundary

An Aggregate exists to enforce invariants that require transactional consistency.

It does not need to contain every historical record required to reconstruct execution.

OwnDay therefore distinguishes:

- **operational domain state**, required to make and validate current decisions;
- **immutable historical records**, required for accountability, metrics, Reflection, and later analysis.

A Commitment must remain small enough to load and mutate as an operational Aggregate.

Historical reconstruction may combine Aggregate state, Domain Events, Execution Evidence, Focus Session history, and Reflection records.

---

## 2.3 Commitment Lifecycle and Outcome Are Separate

A Commitment has a simple lifecycle:

**Draft → Active → Closed**

A Closed Commitment has exactly one Outcome:

- Fulfilled;
- Renegotiated;
- Failed;
- Unaccounted.

This avoids representing the same final fact simultaneously as both lifecycle state and outcome state.

---

## 2.4 Execution Risk and Commitment Outcome Are Orthogonal

Execution Risk describes the current condition of execution:

**Normal | AtRisk | Recovery | DecisionRequired**

It does not determine whether the Commitment is ultimately Fulfilled, Renegotiated, Failed, or Unaccounted.

A Commitment may temporarily become AtRisk and later be Fulfilled.

A failed Recovery does not itself Fail the Commitment.

---

## 2.5 Evidence, Interpretation, Policy, and Domain Transition Are Separate

OwnDay distinguishes four stages:

**Observation / User Report**

→ **Execution Evidence**

→ **Interpretation and Policy Decision**

→ **Domain Transition**

A Domain Transition may then produce a **Domain Event**.

AI interpretation is a hypothesis, not authoritative domain state.

Consequential transitions remain deterministic and/or require explicit user authority.

---

## 2.6 Execution Evidence and Domain Events Have Different Roles

Execution Evidence represents relevant information about reality available to OwnDay.

Examples:

- the user reports being stuck;
- the user reports a blocker;
- a planned start passes without confirmation;
- the user reports material new information;
- the user reports drift.

Domain Events represent facts that occurred inside the domain model as the result of accepted domain transitions.

Examples:

- CommitmentConfirmed;
- StepCompleted;
- FocusSessionStarted;
- CommitmentFulfilled.

The same fact should not normally be persisted independently as both Execution Evidence and a Domain Event.

Domain Events may be projected into history or analytics without duplicating them as Execution Evidence.

---

## 2.7 Execution History Unifies Evidence and Domain Facts

**Execution History** is the complete historical record needed to reconstruct execution and accountability.

Conceptually:

**Execution History = Execution Evidence + Domain Events + historical execution records**

where historical execution records include completed Focus Sessions and Recovery Attempt Records when they carry data not already represented by events.

Execution History is a read/reconstruction concept, not an Aggregate.

It provides the common historical vocabulary needed by:

- accountability views;
- Reflection;
- product metrics;
- causal analysis;
- future learning.

This avoids overloading Execution Evidence with accepted internal domain transitions.

---

## 2.8 Silence Is Not Evidence

In the Telegram MVP, OwnDay cannot directly observe what the user is doing on their computer.

Therefore:

> **Absence of activity evidence is not evidence of inactivity.**

Silence cannot by itself prove distraction, failure, fulfillment, or abandonment.

Time-based facts may still become evidence when they are based on an explicit prior agreement, such as a planned Focus Session start or agreed return point.

---

## 2.9 Preserve History Instead of Rewriting It

A confirmed Commitment preserves its original promised outcome.

If materially new information changes the agreement, the original Commitment is Closed with the Outcome **Renegotiated**, and a replacement Commitment may be created with a new identity.

Reflection and Adaptation never rewrite historical facts.

---

## 2.10 Next Step Is a Role of Step

The MVP does not introduce a separate NextStep entity.

Whenever OwnDay tracks an executable Next Step, it is an existing Step selected as the current Step.

A Commitment may have only one Step and no larger decomposition.

This preserves the optional nature of decomposition without creating two competing representations of executable work.

---

## 2.11 Model the MVP, Not the Long-Term Product

The MVP intentionally excludes:

- full Goal management;
- project backlogs;
- nested task hierarchies;
- calendar management;
- behavioral scoring;
- Personal Execution Model;
- machine-learned intervention policy;
- team collaboration;
- productivity dashboards.

The model should leave room for future extension without modeling these concepts prematurely.

---

# 3. Ubiquitous Language

## User

A person using OwnDay to execute work they have chosen for themselves.

The User owns direction, priorities, Commitments, and consequential decisions.

---

## Goal Context

Lightweight context describing the broader result or project toward which current work contributes.

Example:

> Ship a usable OwnDay MVP.

Goal Context provides meaning and prioritization context. It is not a Goal Aggregate or project hierarchy in the MVP.

---

## Commitment

An explicit promise by the User to produce a specific meaningful outcome within the current day.

A Commitment answers:

> **What should become true today?**

Example:

> Commitments are stored in PostgreSQL and survive application restart.

A Commitment is not merely an activity such as:

> Work on persistence.

---

## Draft Commitment

A proposed Commitment that has not yet been explicitly confirmed by the User.

A Draft does not yet carry accountability.

---

## Active Commitment

A confirmed Commitment whose Outcome has not yet been accounted for.

---

## Closed Commitment

A Commitment whose final Outcome has been recorded.

A Closed Commitment never returns to Active.

---

## Commitment Outcome

The final accounted result of a Commitment.

Allowed Outcomes:

- **Fulfilled** — the promised outcome was achieved;
- **Renegotiated** — materially new information changed the original agreement and the User deliberately replaced it;
- **Failed** — the Commitment remained valid but was not fulfilled;
- **Unaccounted** — required Outcome Accounting did not produce an authoritative result.

---

## Step

A concrete executable action in the currently understood path toward a Commitment.

Steps are execution hypotheses rather than accountability objects.

A Commitment may have only one Step.

---

## Planned Step

A Step known before or during preparation for execution.

---

## Discovered Step

A Step discovered after execution has begun.

Discovered work may change the execution path without changing the Commitment.

---

## Current Step

The Step currently selected as the immediate executable action most likely to move the Commitment forward.

This is the domain representation of the conversational concept **Next Step**.

A Commitment may have zero or one Current Step.

---

## Next Step

The user-facing term for the Current Step.

It answers:

> **What should I do next?**

Next Step is a role of Step, not a separate Entity.

---

## Focus Session

A deliberate attempt to make focused progress toward one Commitment.

A Focus Session may be planned in advance or started immediately.

Expected duration represents allocated capacity, not a Commitment deadline.

Focus Session history is relevant to execution and accountability, but completed historical Sessions do not need to remain inside the Commitment Aggregate.

---

## Execution Evidence

An immutable, meaningful observation or user-reported fact about external execution reality that can support:

- risk assessment;
- Recovery;
- Renegotiation;
- Reflection;
- Adaptation;
- historical analysis.

Examples:

- reported difficulty;
- reported drift;
- reported blocker;
- reported interruption;
- material new information;
- planned start not confirmed after an agreed grace period;
- agreed return point missed.

Execution Evidence is not a continuous activity log and does not duplicate internal Domain Events.

---

## Domain Event

An immutable fact that a meaningful domain transition occurred.

Examples:

- CommitmentConfirmed;
- StepCompleted;
- FocusSessionStarted;
- CommitmentFulfilled.

Domain Events describe what the domain accepted as having happened, not raw observations about the outside world.

---

## Execution History

The reconstructable historical record of meaningful execution.

Execution History combines:

- Execution Evidence about external reality;
- Domain Events representing accepted domain transitions;
- completed Focus Session records;
- Recovery Attempt Records when they preserve additional causal detail.

Execution History is not a mutable domain object and is not owned by Commitment as a collection.

---

## Execution Risk

The current risk state of execution:

- **Normal**;
- **AtRisk**;
- **Recovery**;
- **DecisionRequired**.

Execution Risk is independent of Commitment Outcome.

---

## Drift

A confirmed deviation from intended execution while the Commitment itself may still remain valid.

Example:

> I have been watching YouTube instead of working.

Drift requires evidence. It is never inferred solely from silence.

---

## Blocker

A condition that prevents or materially obstructs the current execution path.

A Blocker may require only a different Current Step, or it may reveal Material New Information.

---

## Material New Information

Information discovered after Commitment confirmation that materially changes assumptions, feasibility, scope, constraints, or expected outcome.

Material New Information may support Renegotiation.

---

## Recovery

A deliberate attempt to restore execution after confirmed difficulty or drift while the Commitment remains valid.

Recovery aims to return the User to a concrete Current Step.

---

## Renegotiation

An explicit User decision to replace a confirmed Commitment because Material New Information changed the original agreement.

Renegotiation preserves the original Commitment and its causal history.

---

## Outcome Accounting

The required process of obtaining a final Outcome for a daily Commitment.

Every Active Commitment must eventually become Closed with exactly one Outcome.

---

## Reflection

A lightweight, evidence-grounded analysis of meaningful divergence between intention and reality.

A Reflection may concern one Commitment or, in future, a repeated pattern spanning multiple Commitments.

Reflection aims to produce a useful execution lesson, not retrospective judgment.

---

## Adaptation

A proposed change to future execution based on Evidence and Reflection.

Examples:

- make a smaller Commitment;
- investigate uncertainty before implementation;
- define a clearer Current Step;
- change Focus Session duration;
- reduce simultaneous Commitments.

Adaptation remains recommendation-based in the MVP.

---

## What Now

A user-facing query asking OwnDay for one concrete recommended action based on current execution state.

`What Now` is an application use case, not a Domain Entity.

---

# 4. Bounded Contexts

## 4.1 Personal Execution Context

**Type:** Core Domain

The Experimental MVP uses one primary Bounded Context: **Personal Execution**.

This avoids inventing context boundaries before distinct domain models and languages actually exist.

### Purpose

Manage the accountability loop for self-directed execution.

### Responsibilities

- User execution context;
- Commitment lifecycle and Outcome;
- Steps and Current Step;
- Focus Sessions;
- Execution Evidence;
- Execution Risk;
- Recovery;
- Renegotiation;
- Outcome Accounting;
- Reflection;
- Adaptation recommendations.

### Core flow

**Commitment → Steps / Current Step → Focus Sessions → Evidence → Risk → Recovery or Decision → Outcome → Reflection → Adaptation**

### Authority boundary

The Personal Execution Context owns domain state transitions.

Telegram, LLM, scheduler, and persistence support the context but do not own its rules.

---

## 4.2 Future Learning Context

**Status:** Not part of the MVP.

Long-term personalization may eventually justify a separate Bounded Context around:

**Observation → Pattern → Hypothesis → Experiment → Evaluation**

This may contain the Personal Execution Model and evidence-based personalization.

The MVP must not prematurely design this model.

---

## 4.3 Concepts That Are Not Bounded Contexts

The following are application or infrastructure concerns:

- Telegram;
- LLM integration;
- scheduler;
- notifications;
- persistence;
- PostgreSQL;
- outbox;
- metrics collection;
- logging.

They support the domain but do not define independent domain models.

---

# 5. Aggregate Overview

The MVP uses four Aggregate Roots:

1. **Commitment** — primary accountability Aggregate;
2. **FocusSession** — one independently evolving execution attempt;
3. **User** — small Aggregate containing stable execution context;
4. **Reflection** — separate post-execution learning Aggregate.

Execution Evidence, Recovery Attempt Records, and Domain Events are immutable historical records rather than independently mutable Aggregates.

The Aggregate boundaries are intentionally explicit:

- Commitment owns the promise, its Steps, Current Step, Outcome, and authoritative current Execution Risk;
- FocusSession owns its own planning/execution lifecycle and references Commitment by identity;
- Reflection owns one completed learning interaction;
- User owns stable execution context.

Historical Focus Sessions, Execution Evidence, Recovery Attempt Records, and Domain Events do not need to be loaded with Commitment.

The distinction is intentional:

> **Commitment is the operational consistency boundary. History is reconstructed across immutable records.**

---

# 6. Commitment Aggregate

## 6.1 Aggregate Root: Commitment

### Purpose

Represent one explicit daily promise and enforce the invariants required to preserve its accountability semantics.

### Responsibilities

The Commitment Aggregate is responsible for:

- Draft creation;
- explicit confirmation;
- preserving the original Promised Outcome;
- maintaining lifecycle state;
- closing with exactly one Outcome;
- managing Steps;
- selecting the Current Step;
- maintaining authoritative current Execution Risk;
- recording Renegotiation data required to close the original Commitment;
- linking a replacement Commitment after Renegotiation;
- emitting meaningful Domain Events.

### Explicit Non-Responsibilities

The Commitment Aggregate does not need to own:

- the complete Execution Evidence history;
- Focus Session lifecycle state;
- all historical Recovery attempts;
- Reflection records;
- analytics projections;
- complete conversational history.

Those records may reference CommitmentId and remain independently persisted.

### Lifecycle

**Draft → Active → Closed**

### Outcome

A Closed Commitment has exactly one:

**Fulfilled | Renegotiated | Failed | Unaccounted**

### Execution Risk

While Active:

**Normal → AtRisk → Recovery → Normal**

or:

**Recovery → DecisionRequired**

Execution Risk does not replace Commitment lifecycle.

---

## 6.2 Commitment Invariants

### C1. Explicit confirmation

Draft may become Active only through explicit User authority.

AI proposals cannot activate a Commitment.

Persisting Draft Commitments is an implementation choice. A simpler application may keep a proposal outside the domain and create the Commitment as Active only after explicit confirmation, provided the externally observable behavior and authority boundary remain the same.

### C2. Outcome-oriented meaning

An Active Commitment must contain a concrete Promised Outcome suitable for later Outcome Accounting.

### C3. Daily scope

Every Commitment belongs to exactly one User-local calendar date.

### C4. No silent rollover

An Active Commitment cannot silently continue into the next local day.

### C5. Closure requires Outcome

A Commitment may enter Closed only with exactly one Outcome.

### C6. Fulfilled requires explicit confirmation

Neither AI inference, Step completion, Focus Session completion, nor silence may autonomously produce Fulfilled.

### C7. Silence is not Failed

Silence cannot produce Failed.

It may eventually produce Unaccounted only through the defined Outcome Accounting Policy.

### C8. Original Promised Outcome is immutable after confirmation

Once Active, the original agreement cannot be rewritten in place.

### C9. Renegotiation requires explicit User authority

OwnDay may propose Renegotiation but cannot apply it silently.

### C10. Renegotiation preserves causality

Renegotiation must preserve references sufficient to reconstruct:

**Original Commitment → Evidence → Material New Information → Explanation → Decision → Replacement Commitment**

### C11. Step completion does not imply Fulfilled

Completing all currently known Steps does not automatically fulfill the promised Outcome.

### C12. Step state does not determine Commitment Outcome

A Step may be removed, replaced, or remain incomplete without automatically Failing the Commitment.

### C13. Flat decomposition only

Steps cannot contain child Steps.

### C14. At most one Current Step

An Active Commitment may have zero or one Current Step.

Zero is allowed when the next action is unknown, blocked, or awaiting a decision.

### C15. Current Step must belong to the Commitment

CurrentStepId, when present, must identify a non-Removed Step owned by the same Commitment.

### C16. Current Step may exist without broader decomposition

A Commitment may contain a single Step solely to represent the immediate Next Step.

### C17. Focus Session relationship is by identity

A Focus Session references exactly one CommitmentId. Commitment does not own the FocusSession Aggregate.

### C18. Focus Session completion does not close Commitment

Ending a Focus Session does not determine Commitment Outcome. Application orchestration may use FocusSession events as input to later Commitment commands.

### C19. Execution Risk does not determine Outcome

AtRisk, Recovery, and DecisionRequired never directly imply Fulfilled, Failed, Renegotiated, or Unaccounted.

### C20. Closed is terminal

A Closed Commitment cannot return to Active.

### C21. Execution Risk is authoritative operational state

`Commitment.ExecutionRisk` is the authoritative current risk state.

Recovery Attempt Records explain how execution moved through Recovery but do not independently define the current risk state.

### C22. Historical records are not rewritten

Closing or Renegotiating a Commitment cannot mutate previously recorded Evidence, Sessions, Recovery Attempt Records, or Domain Events.

---

# 7. FocusSession Aggregate

## 7.1 Aggregate Root: FocusSession

### Purpose

Represent one independently evolving deliberate execution attempt toward exactly one Commitment.

FocusSession is a separate Aggregate Root because it has:

- its own identity;
- its own lifecycle;
- independent temporal transitions;
- scheduler interaction;
- potentially many instances per Commitment;
- no invariant requiring all Session state to be transactionally loaded with Commitment.

### Responsibilities

- preserve CommitmentId;
- preserve planned execution intent;
- store PlannedStart when applicable;
- store ExpectedDuration when applicable;
- reference IntendedStepId when applicable;
- store ActualStart;
- store ActualEnd;
- record EndReason;
- emit FocusSession domain events.

### State

**Planned → Active → Ended**

An immediate Session may be created directly as Active.

Whether a planned Session is currently due is derived from PlannedStart and the clock rather than persisted as `AwaitingStart`.

### Invariants

#### F1. Exactly one Commitment

Every FocusSession references exactly one CommitmentId.

#### F2. Step reference is optional and scoped

IntendedStepId is optional.

When present, application/domain validation must ensure that the referenced Step belongs to the same Commitment.

#### F3. Active requires ActualStart

An Active Session must have ActualStart.

#### F4. End time is ordered

ActualEnd cannot precede ActualStart.

#### F5. Ended is terminal

An Ended Session cannot return to Active.

#### F6. Expected duration is positive

ExpectedDuration must be positive when present.

#### F7. Expected duration is not a Commitment deadline

Reaching ExpectedDuration ends or checks the execution structure; it does not resolve Commitment Outcome.

#### F8. Silence is not inactivity

Silence during an Active Session is not evidence that the User stopped working.

### Cross-Aggregate Coordination

Commands that require both Commitment and FocusSession are coordinated by the Application layer.

Examples:

- starting a Session for an Active Commitment;
- validating IntendedStepId;
- responding to FocusSessionEnded;
- creating Evidence from a missed planned start.

No design should rely on atomically mutating the entire Commitment and FocusSession history as one Aggregate.

---

# 8. User Aggregate

## 7.1 Aggregate Root: User

### Purpose

Represent the stable owner of execution and the minimum persistent context needed by the Personal Execution domain.

### Responsibilities

- OwnDay domain identity;
- operative Time Zone;
- lightweight Goal Context;
- minimal User preferences required by execution policies.

### Explicit Non-Responsibilities

The User Aggregate does not own:

- all Commitments;
- Telegram identity mappings;
- project backlogs;
- full Goal hierarchy;
- Personal Execution Model.

External identity mapping belongs to the application/infrastructure boundary.

---

## 7.2 User Invariants

### U1. Stable domain identity

User has an OwnDay identity independent of Telegram or another interface.

### U2. Exactly one operative Time Zone

Daily Commitment boundaries require one effective Time Zone.

### U3. Goal Context is optional

User may create Commitments without defining Goal Context.

### U4. Goal Context remains lightweight

Goal Context provides meaning without becoming a full Goal or Project Aggregate in the MVP.

---

# 9. Reflection Aggregate

## 8.1 Aggregate Root: Reflection

### Purpose

Represent one evidence-grounded learning record derived from meaningful execution divergence.

Reflection is separate from Commitment because:

- it occurs after or around significant execution events rather than enforcing Commitment consistency;
- it may be created after a Commitment is already Closed;
- future Reflections may span multiple Commitments;
- a completed Commitment should not need to reopen or mutate merely to add learning.

### Responsibilities

- identify the Reflection trigger;
- reference relevant Commitment(s);
- reference supporting Execution Evidence and/or Domain Events;
- preserve the proposed hypothesis;
- preserve User confirmation or correction;
- preserve an optional Adaptation Recommendation;
- emit ReflectionCompleted and AdaptationRecommended events when appropriate.

### Invariants

#### R1. Evidence-grounded

A persisted hypothesis must be supported by referenced Evidence or Domain Events.

#### R2. Hypothesis is not fact

AI-generated interpretation remains a hypothesis until confirmed or corrected.

#### R3. User may correct interpretation

The model must preserve the confirmed or corrected conclusion rather than treating AI interpretation as authoritative.

#### R4. Reflection does not rewrite history

Reflection cannot modify Commitment, Outcome, Step, Focus Session, Evidence, Recovery, or Domain Event history.

#### R5. Reflection is selective

Reflection is not mandatory after every Commitment.

#### R6. Lightweight MVP interaction

The default MVP Reflection should normally be completable through one hypothesis and one confirmation or correction.

#### R7. MVP scope remains simple

An MVP Reflection normally references one PrimaryCommitmentId.

The conceptual model permits future multi-Commitment Reflection, but the MVP persistence model does not require a general N:N relationship unless a concrete repeated-pattern flow needs it.

---

# 10. Entities

## 9.1 Step

### Aggregate

Commitment.

### Purpose

Represent one concrete executable action in the currently understood execution path.

### Responsibilities

- preserve identity;
- hold Description;
- track Status;
- preserve Origin;
- participate in Current Step selection.

### State

**Pending → Active → Done**

or:

**Pending | Active → Removed**

### Invariants

- belongs to exactly one Commitment;
- cannot contain child Steps;
- Done cannot return to Pending;
- Removed cannot become Current Step;
- Origin is immutable;
- Step completion does not change Commitment Outcome automatically.

---

## 9.3 Renegotiation Record

### Aggregate

Commitment.

### Purpose

Preserve the domain data required when an Active Commitment is deliberately replaced because Material New Information changed the agreement.

### Responsibilities

- reference supporting Evidence;
- preserve Material Change;
- preserve User explanation when available;
- preserve decision time;
- reference ReplacementCommitmentId when created.

### Invariants

- may be created only for an Active Commitment;
- requires explicit User authority;
- requires a Material Change rationale;
- discovering additional Steps alone is insufficient;
- closes the original Commitment with Outcome `Renegotiated`;
- Replacement Commitment receives a new identity;
- original Promised Outcome remains immutable.

---

# 11. Historical Domain Records

Historical records support accountability and learning but are not necessarily children of the Commitment Aggregate.

---

## 10.1 Execution Evidence

### Purpose

Represent one immutable observation about execution reality.

### Responsibilities

- preserve what was observed or reported;
- preserve RecordedAt;
- preserve OccurredAt when known or reasonably reported;
- preserve Source;
- reference the relevant Commitment;
- optionally reference a Focus Session;
- support Risk Policy, Recovery, Renegotiation, Reflection, and analysis.

### Typical Evidence Kinds

The MVP should focus on observations that are not already represented as Domain Events, such as:

- DifficultyReported;
- DriftReported;
- BlockerReported;
- InterruptionReported;
- MaterialInformationReported;
- PlannedStartNotConfirmed;
- AgreedReturnPointMissed;
- UserReportedProgress;
- UserReportedPossibleCompletion.

`UserReportedProgress` is used only when a meaningful progress report cannot be represented as a specific domain transition. If the report directly confirms a transition such as Step completion, the authoritative historical fact is the corresponding Domain Event rather than duplicate Evidence.

### Invariants

- immutable after recording;
- RecordedAt is required;
- OccurredAt may be unknown or approximate when the User reports an earlier event;
- provenance is required;
- belongs to one User and normally one Commitment;
- inferred psychological interpretation is not stored as observed fact;
- only meaningful evidence is persisted;
- internal domain transitions are represented by Domain Events rather than duplicated as Evidence.

---

## 11.2 Recovery Attempt Record

### Purpose

Preserve the immutable historical result of one meaningful attempt to restore execution after confirmed risk.

A Recovery Attempt Record is not an Aggregate Root and is not independently mutated after completion.

Operational Recovery state is represented authoritatively by `Commitment.ExecutionRisk`. The record explains the causal history and supports accountability and MVP metrics.

### Data

May contain:

- RecoveryId;
- CommitmentId;
- TriggerEvidenceIds;
- proposed or selected Recovery action;
- StartedAt;
- Result;
- optional temporary checkpoint reference.

### Result

Examples:

- Recovered;
- DidNotRecover;
- Rejected;
- Superseded.

### Invariants

- immutable after completion;

- requires a specific trigger or reason;
- may not be created solely because of unexplained silence;
- does not change Promised Outcome;
- Commitment.ExecutionRisk remains authoritative for current operational state;
- successful Recovery normally corresponds to a transition back to Normal;
- unsuccessful Recovery may correspond to DecisionRequired;
- unsuccessful Recovery does not automatically Fail the Commitment.

The MVP does not require a persistent `Proposed → Accepted` state machine unless implementation or metrics demonstrate a need for it.

---

# 12. Value Objects

## 11.1 UserId

Typed OwnDay identity.

**Invariants:**

- non-empty;
- immutable;
- independent of transport identity.

---

## 11.2 CommitmentId

Typed Commitment identity.

**Invariants:**

- non-empty;
- immutable.

---

## 11.3 ReflectionId

Typed Reflection identity.

**Invariants:**

- non-empty;
- immutable.

---

## 11.4 StepId

Typed Step identity within a Commitment.

**Invariants:**

- non-empty;
- immutable.

---

## 11.5 FocusSessionId

Typed Focus Session identity.

**Invariants:**

- non-empty;
- immutable.

---

## 11.6 Promised Outcome

### Purpose

Represent the specific result the User explicitly promises to make true.

### Invariants

- non-empty;
- immutable after Commitment confirmation.

Whether wording is sufficiently meaningful and outcome-oriented is a soft domain rule and cannot be proved mechanically by a constructor.

---

## 11.7 Commitment Day

### Purpose

Represent the User-local calendar date to which the Commitment belongs.

### Invariants

- interpreted using the User's operative Time Zone;
- not derived purely from UTC date.

---

## 11.8 Commitment Lifecycle

Allowed values:

- Draft;
- Active;
- Closed.

Lifecycle must not encode final Outcome.

---

## 11.9 Commitment Outcome

Allowed values:

- Fulfilled;
- Renegotiated;
- Failed;
- Unaccounted.

### Outcome Record

A Closed Commitment records:

- OutcomeKind;
- RecordedAt;
- optional Explanation;
- relevant Evidence/Event references where useful.

### Invariants

- exists only for Closed Commitment;
- exactly one Outcome;
- Fulfilled requires explicit User confirmation;
- Unaccounted is not equivalent to Failed.

---

## 11.10 Step Description

A concrete executable action.

**Invariants:**

- non-empty;
- describes execution work rather than the Commitment's promised result.

---

## 11.11 Step Status

Allowed values:

- Pending;
- Active;
- Done;
- Removed.

---

## 11.12 Step Origin

Allowed values:

- Planned;
- Discovered.

---

## 11.13 Execution Risk State

Allowed values:

- Normal;
- AtRisk;
- Recovery;
- DecisionRequired.

---

## 11.14 Focus Session Schedule

### Data

- PlannedStart;
- ExpectedDuration.

### Invariants

- PlannedStart is time-zone-aware;
- ExpectedDuration must be positive when present.

---

## 11.15 Focus Session Period

### Data

- ActualStart;
- ActualEnd.

### Invariants

- ActualEnd is optional until Session ends;
- ActualEnd must not precede ActualStart.

---

## 11.16 Focus Session End Reason

Possible values:

- Completed;
- Interrupted;
- Abandoned.

It must not encode Commitment Outcome.

---

## 11.17 Evidence Source

Examples:

- UserReport;
- UserConfirmation;
- TemporalObservation;
- SystemInteraction.

AI inference is not an Evidence Source for observed facts.

---

## 11.18 Evidence Kind

A deliberately small classification of meaningful observations.

The classification should be driven by actual domain policies rather than by a desire to record all activity.

---

## 11.19 Material Change

Information discovered after Commitment confirmation that may materially alter feasibility, scope, constraints, assumptions, or expected outcome.

**Invariants:**

- non-empty;
- preserved when supporting Renegotiation.

The model does not attempt to prove objective materiality. Consequential authority remains with the User.

---

## 11.20 Reflection Hypothesis

An evidence-based interpretation proposed by OwnDay or AI.

**Invariants:**

- remains explicitly a hypothesis until confirmed or corrected;
- references supporting Evidence or Domain Events when persisted.

---

## 11.21 Reflection Conclusion

The User-confirmed or corrected interpretation of what should be learned.

---

## 11.22 Adaptation Recommendation

A proposed improvement for future execution.

Examples:

- smaller Commitment;
- clearer Outcome;
- earlier investigation;
- better decomposition;
- more concrete Current Step;
- different Focus Session duration;
- different execution timing;
- fewer simultaneous Commitments.

**Invariant:** recommendation never mutates future plans automatically.

---

# 13. Domain Policies

Domain Policies express business rules that do not naturally belong to one Aggregate or require information outside one consistency boundary.

---

## 12.1 Active Commitment Limit Policy

### Rule

A User may have at most three Active Commitments for the same User-local date.

Three is a maximum, not a target.

### Consistency Requirement

This is a cross-Aggregate invariant.

A read-then-write count without concurrency protection is insufficient.

The application/persistence boundary must enforce it transactionally, for example through:

- serialization by `UserId + CommitmentDay`;
- a suitable database constraint or quota record;
- or another transaction strategy providing equivalent protection.

The domain policy defines the rule; infrastructure provides concurrency enforcement.

---

## 12.2 Risk Policy

### Purpose

Determine whether available Execution Evidence is sufficient to change Execution Risk.

### Responsibilities

Examples include determining whether:

- a planned start has become a meaningful risk signal;
- explicit difficulty justifies AtRisk or Recovery;
- explicit drift justifies Recovery;
- a Blocker requires a Decision.

### Invariants

- silence alone is not risk evidence;
- psychological motives are not inferred as facts;
- Session boundary alone does not imply AtRisk;
- risk transitions do not determine Commitment Outcome.

---

## 12.3 Intervention Decision

**Layer:** Application policy/orchestration, not pure domain state.

### Purpose

Determine whether OwnDay should interrupt the User after Risk Policy has evaluated the evidence.

Core product rule:

> **Intervene only when execution risk is elevated and a useful actionable response is available.**

The existence of an actionable response may depend on:

- available domain state;
- LLM-generated proposal;
- conversation context;
- scheduling capability;
- interface capability.

Therefore the Domain owns Risk semantics, while the Application layer owns the final decision to send an intervention.

---

## 12.4 Outcome Accounting Policy

### Purpose

Determine when an Active daily Commitment requires explicit accounting and when unresolved accounting becomes Unaccounted.

### Invariants

- no silent rollover;
- silence never means Fulfilled;
- Unaccounted remains distinct from Failed;
- historical knowledge at the accounting boundary is preserved.

Temporal scheduling belongs to the Application layer; the resulting business transition is domain behavior.

---

## 12.5 Renegotiation Policy

### Purpose

Support reasoning about whether new information is relevant to reconsidering the original agreement.

### Responsibilities

- distinguish changed execution path from Material Change;
- preserve supporting Evidence;
- allow OwnDay to propose a revised Commitment;
- require explicit User authority to close the original Commitment as Renegotiated.

The policy may assist the decision but does not override User authority.

---

# 14. Domain Events

Domain Events express accepted business facts caused by domain transitions.

They are distinct from Execution Evidence.

---

## 13.1 CommitmentDrafted

A proposed daily outcome was created.

---

## 13.2 CommitmentConfirmed

The User explicitly confirmed the Draft.

**Invariant:** requires explicit User authority.

---

## 13.3 StepAdded

A Step was added to the execution path.

---

## 13.4 StepDiscovered

A new Step was added as work discovered during execution.

This preserves analytically meaningful Origin without requiring the observation itself to be duplicated as Execution Evidence.

---

## 13.5 StepStarted

Execution began on a Step.

---

## 13.6 StepCompleted

A Step was completed.

This does not Fulfill the Commitment.

---

## 13.7 CurrentStepChanged

The Step serving as the current Next Step changed.

---

## 13.8 FocusSessionPlanned

A future execution attempt was planned.

---

## 13.9 FocusSessionStarted

A deliberate execution attempt actually began.

---

## 13.10 FocusSessionEnded

A Focus Session ended with a recorded EndReason.

This does not determine Commitment Outcome.

---

## 13.11 ExecutionRiskChanged

Execution Risk changed because the domain accepted sufficient Evidence.

The event should contain previous and new Risk State rather than claim an unobservable psychological cause.

---

## 13.12 RecoveryStarted

A deliberate attempt to restore execution began.

---

## 13.13 ExecutionRecovered

Execution resumed after Recovery.

This event supports the MVP Assisted Recovery metric.

---

## 13.14 RecoveryDidNotRestoreExecution

Recovery did not restore execution.

This may lead to DecisionRequired but does not Fail the Commitment.

---

## 13.15 CommitmentDecisionRequired

Further progress requires an explicit User decision such as:

- continue;
- create another Focus Session;
- Renegotiate;
- Fail.

---

## 13.16 CommitmentRenegotiated

The User explicitly replaced the original agreement because Material New Information changed it.

May contain:

- original CommitmentId;
- ReplacementCommitmentId;
- Material Change reference;
- supporting Evidence references.

---

## 13.17 CommitmentFulfilled

The User explicitly confirmed that the Promised Outcome was achieved.

---

## 13.18 CommitmentFailed

The User confirmed that the Promised Outcome will not be achieved and the Commitment was not validly Renegotiated.

---

## 13.19 CommitmentBecameUnaccounted

Outcome Accounting expired without an authoritative Outcome.

This remains distinct from CommitmentFailed.

---

## 13.20 ReflectionCompleted

A Reflection hypothesis was confirmed or corrected and the learning record completed.

---

## 13.21 AdaptationRecommended

An evidence-based recommendation was produced.

This does not imply automatic application.

---

# 15. Application Events and Triggers

Not every useful event is a Domain Event.

The following are primarily application/scheduling concerns that may create Execution Evidence or invoke a domain command:

- planned Focus Session start reached;
- grace period expired;
- temporary Recovery checkpoint reached;
- Focus Session expected duration reached;
- agreed return point reached;
- daily Outcome Accounting boundary reached;
- outcome-response window expired.

For example:

**Scheduler trigger**

→ planned start grace period expires

→ create `PlannedStartNotConfirmed` Evidence if still applicable

→ Risk Policy evaluates Evidence

→ Application decides whether an intervention is useful.

This keeps wall-clock orchestration outside the Aggregate while preserving domain semantics.

---

# 16. Relationships

Logical relationships are:

- `User 1 → N Commitment`
- `User 1 → 0..1 GoalContext`
- `Commitment 1 → N Step`
- `Commitment 1 → 0..N FocusSession` by CommitmentId
- `Commitment 1 → 0..N ExecutionEvidence`
- `Commitment 1 → 0..N RecoveryAttemptRecord`
- `Commitment 1 → 0..1 RenegotiationRecord`
- `Commitment 1 → 0..1 Outcome`
- `Commitment 0..1 → 1 ReplacementCommitment` after Renegotiation
- `Reflection N → 1 primary Commitment` in the MVP
- `Reflection → relevant ExecutionEvidence / DomainEvent references`

Cross-Aggregate relationships use identifiers rather than direct object references.

The relationship diagram is logical; it does not imply that every related record belongs to the same Aggregate consistency boundary.

---

# 17. Conceptual Domain Flow

The domain can be understood as:

**Goal Context**

provides meaning for

**Commitment**

which contains one or more optional

**Steps**

with zero or one

**Current Step / Next Step**

executed through

**Focus Sessions**

while OwnDay receives

**Execution Evidence**

which is evaluated by

**Risk Policy**

and may change

**Execution Risk**

which may lead to

**Recovery**

or reveal

**Material New Information**

which may support

**Renegotiation**

and every Commitment eventually reaches

**Outcome Accounting**

and becomes

**Closed**

with exactly one Outcome:

**Fulfilled | Renegotiated | Failed | Unaccounted**

Together, Evidence, Domain Events, completed Sessions, and Recovery Attempt Records form reconstructable

**Execution History**.

Significant divergence may then produce a separate

**Reflection**

and optionally an

**Adaptation Recommendation**.

---

# 18. State Machines

## 17.1 Commitment Lifecycle

**Draft**

→ `Confirm` → **Active**

**Active**

→ `ConfirmFulfilled` → **Closed / Fulfilled**

→ `Renegotiate` → **Closed / Renegotiated**

→ `ConfirmFailed` → **Closed / Failed**

→ `AccountingExpires` → **Closed / Unaccounted**

Closed is terminal.

A Renegotiated Commitment may link to a new Draft or Active replacement Commitment with a new CommitmentId.

---

## 17.2 Step

**Pending**

→ `Start` → **Active**

→ `Complete` → **Done**

or:

**Pending | Active**

→ `Remove` → **Removed**

---

## 17.3 Focus Session

Planned path:

**Planned**

→ `Start` → **Active**

→ `End` → **Ended**

Immediate path:

`StartNow` → **Active**

→ `End` → **Ended**

Whether a Planned Session is currently awaiting start is derived from time rather than persisted as a lifecycle state.

---

## 17.4 Execution Risk

**Normal**

→ `RiskDetected` → **AtRisk**

→ `StartRecovery` → **Recovery**

→ `ExecutionResumed` → **Normal**

or:

**Recovery**

→ `RecoveryDidNotRestoreExecution` → **DecisionRequired**

From **DecisionRequired**, an explicit User decision may:

- continue and return to an executable state;
- create another Focus Session;
- Renegotiate the Commitment;
- Fail the Commitment.

Execution Risk transitions never assign Outcome automatically.

---

# 19. Command and Evidence Examples

The following examples clarify the boundary between Evidence and Domain Events.

## 18.1 Explicit difficulty

User says:

> I can't make myself start.

Application records:

`DifficultyReported` Execution Evidence.

Risk Policy may determine:

`Normal → AtRisk` or directly justify Recovery.

Domain transition emits:

`ExecutionRiskChanged`.

If Recovery begins:

`RecoveryStarted`.

---

## 18.2 Step completion

User says:

> Migration done.

Application interprets the message in context and invokes:

`CompleteStep(stepId)`.

Commitment validates the Step transition and emits:

`StepCompleted`.

There is normally no separate `StepCompleted` Execution Evidence record because the Domain Event already represents the accepted fact.

---

## 18.3 Possible Commitment completion

User says:

> Done. Persistence works after restart.

Application may record or transiently interpret a `UserReportedPossibleCompletion` observation.

OwnDay asks for explicit confirmation.

Only after confirmation does the domain execute:

`ConfirmFulfilled()`.

The resulting authoritative fact is:

`CommitmentFulfilled`.

---

## 18.4 Planned start not confirmed

A scheduler reaches the agreed grace boundary.

The application verifies that no start was confirmed and records:

`PlannedStartNotConfirmed`.

Risk Policy evaluates it.

The Application may then decide to ask:

> Did you start?

No distraction or failure is inferred.

---

# 20. Application-Layer Boundaries

## Telegram

Responsible for:

- receiving messages;
- buttons and callback queries;
- presenting prompts;
- mapping Telegram identity to UserId.

`TelegramUserId → UserId` mapping is not part of the User Aggregate.

---

## LLM

Responsible for proposals and hypotheses such as:

- outcome-oriented Commitment wording;
- Step decomposition;
- likely Current Step;
- blocker interpretation;
- Reflection hypothesis;
- Adaptation Recommendation.

LLM output is not authoritative domain state.

---

## Scheduler

Responsible for temporal triggers such as:

- planned Session start;
- temporary Recovery checkpoint;
- expected Session boundary;
- agreed return point;
- Outcome Accounting boundary.

Scheduler invokes application use cases. It does not decide domain meaning.

---

## Persistence

Responsible for:

- Aggregate persistence;
- historical Session records;
- Execution Evidence;
- Domain Events / outbox;
- Recovery Attempt Records;
- Reflection records.

Persistence structure must not dictate Aggregate boundaries.

---

## Metrics

Metrics such as:

- Fulfilled Commitment Rate;
- Silent Abandonment Rate;
- Focus Session Start Rate;
- Assisted Start;
- Assisted Recovery;
- Recovery Success Rate;
- Assisted Completion;

are projections derived from domain history.

They evaluate the product mechanism, not the User's worth.

---

## What Now

`What Now` is an application query.

It may combine:

- Active Commitments;
- Current Step;
- active Focus Session;
- Blockers;
- recent Evidence;
- Goal Context.

The default response is one recommended action.

The query does not require a dedicated Domain Entity.

---

# 21. Consistency Boundaries

## 20.1 Commitment Aggregate

The Commitment Aggregate is the transactional consistency boundary for:

- UserId;
- CommitmentDay;
- Promised Outcome;
- Lifecycle;
- Outcome;
- Steps;
- CurrentStepId;
- current Execution Risk;
- Renegotiation data needed to close the Commitment.

It is **not** the consistency boundary for FocusSession and is **not** the container for all historical execution data.

---

## 21.2 FocusSession Aggregate

The FocusSession Aggregate is the consistency boundary for:

- FocusSessionId;
- CommitmentId;
- IntendedStepId reference;
- planning data;
- ActualStart and ActualEnd;
- lifecycle state;
- EndReason.

It evolves independently from Commitment and is coordinated through application use cases and Domain Events.

---

## 21.3 User Aggregate

The User Aggregate is the consistency boundary for:

- UserId;
- operative Time Zone;
- lightweight Goal Context;
- minimal execution preferences.

It does not own Commitments.

---

## 21.4 Reflection Aggregate

Reflection is independently consistent around:

- its trigger;
- referenced history;
- hypothesis;
- User conclusion;
- Adaptation Recommendation.

It references Commitments and Evidence by identity.

---

## 21.5 Cross-Aggregate Rule of Three

The maximum of three Active daily Commitments is a cross-Aggregate invariant.

It must be enforced transactionally at the application/persistence boundary rather than by loading all Commitments into User.

---

# 22. Explicitly Excluded Domain Concepts

The following should not appear as first-class MVP domain objects unless requirements change:

- DailyPlan;
- PlanTask;
- MinimumCommitment;
- ReliabilityScore;
- ProductivityScore;
- DayMode;
- project backlog;
- Epic;
- Story;
- nested Task;
- Step dependency graph;
- calendar event;
- streak;
- XP;
- badge;
- Personal Execution Model;
- behavioral profile;
- machine-learned risk score.

Their inclusion would conflict with the current MVP or introduce explicitly excluded scope.

---

# 23. Core Domain Invariants Summary

1. A Draft carries no accountability until explicitly confirmed.
2. A confirmed Promised Outcome cannot be silently rewritten.
3. Every MVP Commitment belongs to one User-local day.
4. An Active Commitment cannot silently roll into another day.
5. Commitment lifecycle is `Draft → Active → Closed`.
6. Every Closed Commitment has exactly one Outcome.
7. Outcome is `Fulfilled | Renegotiated | Failed | Unaccounted`.
8. Fulfilled always requires explicit User confirmation.
9. Silence never means Fulfilled.
10. Silence alone never means Failed.
11. Unaccounted is distinct from Failed.
12. A User may have at most three Active Commitments for one local day.
13. The Rule of Three requires transactional cross-Aggregate enforcement.
14. Three is a maximum, not a quota.
15. Accountability belongs to Commitments, not Steps.
16. Steps are flat execution hypotheses.
17. Next Step is the user-facing role of the Current Step, not a separate Entity.
18. A Commitment may contain one Step without broader decomposition.
19. At most one non-Removed Step is the Current Step.
20. Discovering another Step does not by itself justify Renegotiation.
21. A Focus Session executes one Commitment.
22. Focus Session duration is execution structure, not a Commitment deadline.
23. Ending a Focus Session does not determine Commitment Outcome.
24. `AwaitingStart` is derived from time, not required as persisted Session state.
25. Execution Evidence represents observations about reality.
26. Domain Events represent accepted domain transitions.
27. Execution History combines Evidence, Domain Events, and relevant immutable historical records.
28. The same internal fact should not normally be duplicated as both Evidence and Domain Event.
29. Silence is not evidence of inactivity.
30. AI interpretation is a hypothesis, not authoritative state.
31. Execution Risk and Commitment Outcome are separate.
32. Risk Policy belongs to the domain; the final send/no-send Intervention Decision belongs to application orchestration.
33. Recovery requires a meaningful trigger and does not modify Promised Outcome.
34. Failed Recovery does not automatically Fail the Commitment.
35. Renegotiation requires Material New Information and explicit User authority.
36. Renegotiation preserves the original Commitment and causal evidence chain.
37. Closed Commitments do not reopen to attach Reflection.
38. Reflection is a separate Aggregate and cannot rewrite history.
39. Adaptation is recommendation-based in the MVP.
40. Aggregate boundaries are defined by consistency requirements, not by historical navigation needs.
41. The system preserves enough immutable history to reconstruct intention, execution, divergence, Outcome, and learning without becoming a surveillance log.

---


# 24. Domain Model Summary

The OwnDay MVP domain is intentionally narrow.

It is not a task-management model and not a calendar model.

Its primary operational Aggregate is:

**Commitment**

which preserves one explicit daily promise and owns the state required to enforce its accountability semantics.

Execution is represented through:

**Commitment Steps → Current Step**

and independently evolving execution attempts are represented by the:

**FocusSession Aggregate**

while observations about reality are preserved as:

**Execution Evidence**

and accepted domain changes are expressed as:

**Domain Events**.

Together with completed Focus Sessions and Recovery Attempt Records, these form reconstructable:

**Execution History**.

Evidence may affect:

**Execution Risk**

which can lead to:

**Recovery** or an explicit **Decision**.

Materially changed reality may lead to:

**Renegotiation**.

Every Commitment eventually becomes:

**Closed**

with exactly one:

**Fulfilled | Renegotiated | Failed | Unaccounted** Outcome.

Learning is deliberately separated from Commitment consistency through the:

**Reflection Aggregate**

which may produce an:

**Adaptation Recommendation**.

The central design decisions are:

> **Commitment is the primary operational Aggregate Root of the OwnDay MVP Core Domain.**

> **Aggregate boundary is not history boundary.**

> **Execution Evidence describes observations; Domain Events describe accepted domain transitions.**

> **Lifecycle, Outcome, and Execution Risk are separate concepts.**

> **Next Step is a role of Step rather than a competing domain object.**

This structure supports the MVP product hypothesis while keeping the model small enough to implement cleanly in a modular ASP.NET Core application without prematurely introducing distributed bounded contexts or a large historical Aggregate.
