# OwnDay MVP — User Flows

**Version:** 2.0
**Status:** Experimental MVP
**Primary Interface:** Telegram

---

# 1. Purpose

This document defines how the user interacts with OwnDay during the Experimental MVP.

The interaction model is designed around one constraint:

> OwnDay should influence execution while requiring as little interaction as possible.

The product should not feel like a task-management interface implemented as a chatbot.

It should feel like a lightweight execution companion that understands the current work context, stays quiet while execution is going well, and intervenes when a useful decision or action is needed.

The underlying domain may contain:

**Commitment → Steps → Focus Session → Execution Evidence → Risk → Recovery → Outcome → Reflection → Adaptation**

The user should not need to understand or explicitly operate this state machine.

---

# 2. Core Interaction Principles

## 2.1 Execution Before Conversation

Conversation exists to support execution.

OwnDay should not create interaction merely because a state transition occurred.

Before sending a message, OwnDay should effectively ask:

> Is user interaction necessary or likely to improve execution right now?

If not, OwnDay remains silent.

---

## 2.2 Silence Is the Default

During normal execution OwnDay should not check in periodically.

Silence means:

> No intervention is currently justified.

It does not mean OwnDay assumes the user is working.

OwnDay must not infer activity or inactivity without evidence.

---

## 2.3 Rich State, Thin Interface

OwnDay may maintain detailed internal execution state.

The user should normally interact using natural concepts such as:

* today's result;
* next step;
* start;
* continue;
* stuck;
* change the plan;
* done.

Internal terms such as `Recovery`, `AtRisk`, `DecisionRequired`, and `Execution Evidence` should not normally appear in conversation.

`Commitment` may appear when useful, but the conversational default should be phrases such as:

> today's result

or:

> what you decided to finish today.

---

## 2.4 Infer Before Asking

OwnDay should use existing context and execution evidence before asking the user for information.

Prefer:

> It looks like the work expanded because the persistence boundary was unclear when we made the plan. Is that right?

over:

> Why didn't you finish?

AI interpretations remain hypotheses.

The user must be able to confirm or correct them.

---

## 2.5 One Decision Per Turn

A message should normally require one simple decision.

Avoid:

> Did you start? If not, why not? Is the result still realistic? Do you want to change it?

Prefer:

> Did you start?

Then use the answer to determine whether another interaction is necessary.

---

## 2.6 One Confirmation May Perform Several Obvious Actions

The user should not have to separately confirm internal objects when their intention is already clear.

For example, one confirmation may:

* confirm a Commitment;
* accept the proposed Next Step;
* start an immediate Focus Session.

The system must still persist the corresponding domain transitions separately.

---

## 2.7 Natural Language First

Users may answer naturally:

> yes

> no

> later

> in 20 minutes

> I'm stuck

> already started

> almost done

> this no longer makes sense

> forget it

> what now?

OwnDay should interpret the answer in context rather than require command vocabulary.

Buttons may be provided for convenience but should not be required when natural language is unambiguous.

---

## 2.8 User Message Does Not Require Assistant Message

A user update may only create Execution Evidence.

For example:

**User**

> Migration done.

OwnDay may record the completed Step without generating a conversational response unless a decision or useful next action follows.

---

## 2.9 Every Intervention Must Earn the Interruption

An unsolicited OwnDay message should do at least one of the following:

* help execution start;
* help execution resume;
* resolve uncertainty about what to do;
* handle materially changed reality;
* obtain required outcome accounting;
* produce useful reflection.

Otherwise OwnDay should remain silent.

---

# 3. Conversation Model

The primary interaction pattern is:

**Observe Context**

→ **Decide Whether Interaction Is Useful**

→ **Infer What Can Be Inferred**

→ **Ask for the Minimum Missing Information**

→ **Produce One Useful Decision or Next Action**

→ **Return to Silence**

OwnDay should not attempt to maintain continuous conversation throughout the working day.

---

# 4. High-Level User Flow

**Onboarding**

→ **Choose Today's Result**

→ **Clarify Result**

→ **Optional Decomposition**

→ **Confirm and Start**

→ **Execute**

During execution:

**Normal Execution → Silence**

or:

**User Needs Direction → What Now?**

or:

**Execution Risk → Recovery**

or:

**New Information → Continue or Change Plan**

Eventually:

**Outcome Accounting**

→ **Selective Immediate Reflection**

→ **Adaptation**

→ **Next Work**

---

# 5. Minimal Onboarding

## Trigger

The user starts OwnDay for the first time.

## Goal

Explain the behavioral contract without introducing product terminology or requiring extensive configuration.

## Flow

OwnDay briefly explains:

> OwnDay helps you follow through on work you choose for yourself.
>
> You decide what matters. I'll help turn it into a concrete result for today, help you return if execution breaks down, and make sure the result doesn't silently disappear.

OwnDay asks:

> What are you currently trying to make happen?

Example:

**User**

> I'm building my own SaaS.

OwnDay stores this as lightweight Goal Context.

No comprehensive goal hierarchy, personal vision setup, project configuration, or productivity assessment is required.

## Alternative

The user may skip context and immediately create today's result.

## End State

OwnDay is ready to create a daily Commitment.

---

# 6. Choose Today's Result

## Trigger

The user wants to begin work or create a Commitment.

## Goal

Identify a meaningful outcome for the current day.

## Flow

OwnDay asks:

> What meaningful result should be true by the end of today?

**User**

> Work on persistence.

OwnDay recognizes that the statement describes activity rather than outcome.

It proposes a concrete formulation:

> How about:
>
> **Commitments are stored in PostgreSQL and survive an application restart.**
>
> Is that the result you want today?

The user may:

* confirm;
* correct;
* narrow;
* expand;
* replace the result.

## Rules

OwnDay should prefer outcome-oriented Commitments.

It must not silently convert the user's intention into a confirmed Commitment.

The user must explicitly confirm the result before it becomes Active.

A user may have at most three active daily Commitments.

Three is a maximum, not a target.

---

# 7. Prepare the Next Step

## Trigger

A Draft Commitment exists.

## Goal

Reduce activation energy without creating unnecessary planning work.

## Flow

OwnDay determines whether the immediate execution path is already clear.

If clear, it proposes only the Next Step:

> Start with the persistence model and EF mapping.

If useful, OwnDay may propose a small flat decomposition:

> A likely path:
>
> 1. Persistence model and EF mapping
> 2. Migration
> 3. Repository implementation
> 4. Restart verification
>
> Start with the first one.

The user may accept, edit, reject, or ignore the proposed decomposition.

## Rules

Steps are implementation hypotheses.

They may change during execution without changing the Commitment.

OwnDay should not require detailed decomposition when the Next Step is already obvious.

Planning must stop when there is enough information to begin execution.

---

# 8. Confirm and Start

## Trigger

The result is sufficiently clear and a Next Step is available.

## Goal

Move from intention to execution with minimum ceremony.

## Preferred Flow

OwnDay:

> Today's result:
>
> **Commitments are stored in PostgreSQL and survive an application restart.**
>
> Start with the persistence model and EF mapping.
>
> Start now?

**User**

> Yes.

This single response may:

1. confirm the Commitment;
2. accept the Next Step;
3. start an immediate Focus Session.

OwnDay:

> Started. I won't interrupt unless something needs your attention.

OwnDay returns to silence.

## Alternative — Commit Without Starting

**User**

> I'll start after lunch.

OwnDay confirms the Commitment but does not start a Focus Session.

It may ask for a planned start only if the information is useful for future accountability.

---

# 9. Plan a Focus Session

## Trigger

The user wants to work later rather than immediately.

## Goal

Create a concrete execution opportunity without turning OwnDay into a calendar.

## Flow

**User**

> I'll work on it at 14:00 for about an hour.

OwnDay records:

* planned start;
* expected duration;
* intended Next Step.

OwnDay:

> Okay. At 14:00, start with the EF mapping.

No additional confirmation is necessary if the user's intention is already explicit.

## Rules

Expected duration represents planned capacity, not a deadline for the Commitment.

Focus Sessions are execution structures, not calendar events.

---

# 10. Normal Execution

## Trigger

A Focus Session is Active.

## Default Behavior

OwnDay remains silent.

It does not send periodic:

> Still working?

messages.

It does not infer that the user remains productive.

It does not infer distraction from silence.

## User Updates

The user may voluntarily report progress:

> Mapping done.

> Migration works.

> Found another dependency.

> Taking lunch until 13:30.

OwnDay records useful Execution Evidence.

A response is generated only when it adds value.

---

# 11. Step Discovery During Execution

## Trigger

The user discovers additional work.

Example:

**User**

> Before implementing the repository I need to change the DbContext registration.

## Flow

If this changes only the execution path, OwnDay records a Discovered Step.

The Commitment remains unchanged.

OwnDay may update the Next Step:

> Then do the DbContext registration first.

No renegotiation is required.

## Rule

More work does not automatically mean the Commitment changed.

Only materially changed feasibility or meaning should trigger reconsideration of the Commitment.

---

# 12. What Now?

## Trigger

The user says something equivalent to:

> What now?

> What should I do?

> What's next?

> Help me start.

## Goal

Immediately restore execution orientation.

## Flow

OwnDay considers:

* active Commitments;
* current Focus Session;
* completed Steps;
* Next Step;
* known blockers;
* recent Execution Evidence.

It returns **one recommended action**.

Example:

> Create the EF Core configuration for `Commitment`.
>
> That's the next step toward today's result.
>
> Start now?

## Rules

Do not return a backlog unless explicitly requested.

Do not force the user to choose among several equivalent actions when OwnDay can make a reasonable recommendation.

`What Now?` should work from almost any execution state.

---

# 13. Planned Start Not Observed

## Trigger

A planned Focus Session has a start time and the agreed grace period expires without evidence that execution started.

## Flow

OwnDay asks the minimum necessary question:

> You planned to start with the migration about 10 minutes ago. Did you start?

Possible natural responses:

> Yes.

> Not yet.

> Starting now.

> In 20 minutes.

> I'm stuck.

> I changed my mind.

## If Started

OwnDay records the start if appropriate and returns to silence.

## If Not Started

OwnDay determines whether simple Recovery is useful.

It must not treat silence alone as proof of distraction or failure.

---

# 14. Explicit Difficulty

## Trigger

The user says:

> I'm stuck.

> I can't start.

> I'm avoiding this.

> I keep postponing it.

## Goal

Restore execution rather than analyze the user.

## Flow

OwnDay uses existing context to identify the smallest useful action.

Example:

**User**

> I can't make myself start.

**OwnDay**

> Don't solve persistence yet.
>
> Open the project and create `CommitmentConfiguration`.
>
> Start with that?

**User**

> Yes.

OwnDay records the restart and returns to silence.

## Rule

Recovery should feel like returning to work, not entering a special product mode.

---

# 15. Explicit Drift

## Trigger

There is actual evidence of execution drift.

Example:

**User**

> I've been watching YouTube instead of working.

## Goal

Restore execution while the Commitment remains valid.

## Flow

OwnDay first uses existing evidence.

If the Commitment clearly remains valid:

> The result for today hasn't changed.
>
> Go back to the current Next Step: define the persistence boundary.
>
> Start again?

If validity is uncertain:

> Does today's result still make sense, or did something about the work actually change?

## Rules

OwnDay should not moralize.

OwnDay should distinguish:

* execution drift;
* changed reality.

Distraction alone does not justify rewriting the Commitment.

---

# 16. Temporary Increased Accountability

## Trigger

Confirmed drift occurred and a short follow-up may materially help restart.

## Flow

OwnDay may propose:

> Want me to check once in 20 minutes that you managed to restart?

The user must agree.

If agreed, OwnDay schedules one temporary checkpoint.

At the checkpoint:

> Did you get back into it?

If yes, OwnDay returns to silence.

If no, OwnDay may continue Recovery or move toward a decision.

## Rules

Temporary accountability is exceptional.

The MVP must not turn this into permanent periodic monitoring.

---

# 17. Explicit Break and Missed Return

## Trigger

The user explicitly reports a break and agrees to a return point.

Example:

**User**

> Lunch. Back at 13:30.

OwnDay may acknowledge without further conversation.

If the agreed return point passes without evidence of return, OwnDay may ask:

> You planned to return to persistence after lunch. Back to it?

This intervention is valid because the return point was explicitly established.

Silence without an agreed return point is insufficient evidence.

---

# 18. Blocker

## Trigger

The user reports a blocker.

Example:

> I'm stuck because I don't know where DbContext belongs in the current architecture.

## Goal

Determine whether the problem changes only execution or changes the Commitment itself.

## Flow

If the blocker can reasonably be resolved within the existing Commitment:

> First resolve the persistence boundary. Don't implement the repository yet.

This becomes the Next Step.

If the blocker reveals materially different work:

> It sounds like this is larger than an implementation detail. Does this make the original result unrealistic for today?

The user's response determines whether execution continues or the plan needs to change.

---

# 19. Material New Information

## Trigger

New information materially changes:

* feasibility;
* scope;
* assumptions;
* constraints;
* expected outcome.

Example:

**User**

> The persistence layer depends on an architecture change I didn't know about. I won't finish persistence today.

## Flow

OwnDay should infer what it reasonably can:

> It looks like the morning plan was based on an assumption that persistence could be added directly. We now know the architecture boundary has to change first.
>
> So the original result is probably no longer realistic today. Correct?

The user may:

* confirm;
* correct the interpretation;
* decide to continue anyway.

## Rule

AI interpretation is not the decision.

The user retains authority over whether the Commitment changes.

---

# 20. Change the Plan

## Trigger

The user confirms that material new information invalidates or materially changes the original Commitment.

## Goal

Renegotiate without bureaucratic interaction.

## Preferred Flow

OwnDay:

> Then I suggest changing today's result to:
>
> **Persistence boundary is defined, dependencies are corrected, and the application is ready for PostgreSQL persistence.**
>
> Change the plan?

**User**

> Yes.

OwnDay internally:

1. preserves the Original Commitment;
2. records relevant Execution Evidence;
3. records the material new information;
4. marks the original Commitment Renegotiated;
5. creates the linked revised Commitment;
6. confirms it;
7. determines the new Next Step.

Conversationally, OwnDay may simply respond:

> Changed.
>
> Next: finish the persistence boundary.

## Rules

Never silently rewrite a confirmed Commitment.

Do not expose the complete renegotiation record unless the user asks for it.

---

# 21. User Wants to Abandon Valid Work

## Trigger

The user says:

> Forget it.

> I don't want to do this anymore.

> Maybe tomorrow.

and there is no evidence that the Commitment itself became invalid.

## Goal

Preserve accountability without moral judgment.

## Flow

OwnDay distinguishes unwillingness to continue from materially changed reality.

Example:

> Nothing about the result seems to have changed. Do you want to make one more attempt now, or record that it won't be completed today?

The user chooses.

OwnDay must not invent a renegotiation merely to avoid recording failure.

---

# 22. Focus Session Boundary

## Trigger

The expected Focus Session duration expires.

## Goal

Reorient without assuming success or failure.

## Flow

If recent evidence already makes the state clear, OwnDay may infer and ask only what remains necessary.

Otherwise:

> The hour you planned is up. Continue, or stop here for now?

Possible answers:

> Continue.

> Stop.

> Done.

> I need another 30 minutes.

> I'm stuck.

## Rules

Session duration expiration is not Commitment failure.

Ending a Focus Session does not require a Commitment outcome if another realistic execution opportunity remains.

---

# 23. Focus Session Completion

## Trigger

The user ends a Focus Session.

## Flow

OwnDay records:

* actual end;
* completed Steps;
* discovered Steps;
* relevant evidence;
* current Next Step.

If there is no useful decision to make, no detailed summary is required.

If orientation is useful:

> Good stopping point.
>
> Next time, continue with restart verification.

The Commitment remains Active unless its outcome is explicitly resolved.

---

# 24. Visible Progress

## Trigger

The user asks:

> How far am I?

> What's done?

> Show progress.

## Flow

OwnDay presents evidence rather than percentages.

Example:

> Done:
>
> ✓ Persistence model
> ✓ EF mapping
> ✓ Migration
>
> Next:
>
> → Repository implementation
>
> Then:
>
> → Restart verification

## Rules

Do not fabricate completion percentages.

Unknown work may still exist.

---

# 25. Fulfill Today's Result

## Trigger

Evidence suggests the promised outcome may have been achieved.

Example:

**User**

> Done. Persistence works after restart.

## Flow

OwnDay may infer likely fulfillment but requires confirmation:

> That matches today's result. Mark it done?

**User**

> Yes.

The Commitment becomes Fulfilled.

## Rule

OwnDay may never mark a Commitment Fulfilled solely from inference or silence.

---

# 26. Failed Commitment

## Trigger

The user indicates that the promised outcome will not be achieved and there is no valid renegotiation.

Example:

**User**

> I'm not going to finish it today.

If available evidence does not indicate materially changed circumstances, OwnDay may clarify:

> The result itself still makes sense, but you aren't going to complete it today. Correct?

**User**

> Yes.

The Commitment becomes Failed.

## Rules

Failure is an execution outcome, not a judgment about the user.

OwnDay should not soften a genuine failure into renegotiation merely to make the interaction more comfortable.

---

# 27. Outcome Due

## Trigger

The daily Commitment reaches its accounting boundary while still Active.

## Goal

Prevent silent disappearance.

## Flow

OwnDay asks directly:

> What happened with today's persistence result?

If context strongly suggests an outcome, OwnDay may propose it:

> From your last update it looks unfinished. Is that right?

Possible natural responses may map to:

* Fulfilled;
* Renegotiated;
* Failed.

## Rules

OwnDay should use accumulated evidence before asking broad questions.

Outcome accounting is mandatory for a daily Commitment.

---

# 28. Unaccounted Outcome

## Trigger

The required accounting request receives no response within the defined accounting policy.

## Flow

The Commitment becomes Unaccounted.

OwnDay must not infer:

* Fulfilled;
* Failed;
* Renegotiated.

There is no silent rollover.

If the user later returns, OwnDay may ask for the missing outcome, but history must preserve what was known at the original accounting boundary.

---

# 29. Immediate Reflection

## Trigger

Reflection is useful after:

* Failed Commitment;
* Renegotiated Commitment;
* meaningful Recovery;
* repeated execution pattern.

## Goal

Extract one useful execution lesson with minimal interaction.

## Preferred Flow

OwnDay first constructs a hypothesis from existing evidence.

Example:

> It looks like two different things happened today:
>
> the original persistence estimate missed an architecture dependency, and after lunch you had trouble returning to the work.
>
> Next time we should investigate architecture uncertainty before committing to implementation, while keeping a concrete Next Step for restarting.
>
> Is that accurate?

The user may respond:

> Yes.

or correct the interpretation.

Reflection is then complete.

## Rules

Default Reflection should normally require only:

**one hypothesis → one confirmation or correction.**

Additional questions should be asked only when the evidence is insufficient to produce a useful hypothesis.

Avoid generic interrogation such as:

> Why did you fail?

Do not invent explanations that are unsupported by evidence.

---

# 30. Adapt the Next Attempt

## Trigger

Reflection identifies a potentially useful change.

## Goal

Improve future execution without automatically controlling the user's plans.

Possible recommendations include:

* smaller Commitment;
* clearer outcome;
* earlier investigation of uncertainty;
* better decomposition;
* more concrete Next Step;
* different Focus Session duration;
* different execution time;
* fewer simultaneous Commitments;
* temporary accountability after known drift.

## Flow

OwnDay may propose:

> Next time architecture uncertainty is this high, I suggest making investigation the first result before committing to implementation.

The user may accept, reject, or ignore the recommendation.

## Rule

OwnDay does not automatically rewrite future plans based on inferred behavioral patterns.

---

# 31. Conversation Repair

Users will frequently respond outside the expected flow.

OwnDay must recover from these responses without forcing the user to restart the interaction.

## "Later"

Interpret relative to current context.

If a concrete return point would materially improve accountability:

> When should I bring you back to it?

Do not ask for a time if it provides no execution value.

---

## "Almost"

Do not infer completion.

Example:

**User**

> Almost done.

OwnDay records progress.

The Commitment remains Active.

---

## "Done"

Use context.

If "done" clearly refers to a Step, mark/propose the Step as complete.

If it likely refers to the Commitment:

> You mean today's persistence result is fully done?

Consequential ambiguity requires clarification.

---

## "No"

Interpret against the immediately preceding question.

Do not restart the whole flow.

---

## "I don't know"

Reduce the decision.

Example:

> Then don't solve the whole problem. What's the smallest thing we can inspect first?

Or, if context provides enough evidence, OwnDay should propose the action itself.

---

## "Forget it"

Determine whether the user is:

* abandoning still-valid work;
* reporting changed priorities;
* reporting new information.

Ask only the minimum question required to distinguish them.

---

## Topic Change

The user may discuss unrelated matters.

OwnDay should not force every message into the execution model.

Only create Execution Evidence when the message reasonably relates to current work.

---

# 32. History

## Trigger

The user asks to review previous work.

## Goal

Provide an accountable reconstruction without becoming an activity log.

For each Commitment, OwnDay may show:

**Original result**

→ **meaningful Steps**

→ **Focus Sessions**

→ **important Execution Evidence**

→ **Recovery or plan changes**

→ **Outcome**

→ **Reflection**

The history should answer:

> What did I intend to make happen?

> What actually happened?

> What changed?

> What did I learn?

It should not attempt to reconstruct every minute of the user's day.

---

# 33. Daily Lifecycle — Normal Execution

**Choose result**

→ **Clarify**

→ **Confirm + Start**

→ **Silence**

→ **User reports progress**

→ **Silence**

→ **Result achieved**

→ **Explicit Fulfilled confirmation**

→ **Done**

---

# 34. Daily Lifecycle — Recovery

**Confirm + Start**

→ **Execution**

→ **Evidence of drift or difficulty**

→ **OwnDay proposes smallest useful Next Step**

→ **User restarts**

→ **Optional temporary checkpoint**

→ **Execution resumes**

→ **Silence**

→ **Outcome**

→ **Selective Reflection**

---

# 35. Daily Lifecycle — Changed Reality

**Commit**

→ **Execute**

→ **New information discovered**

→ **OwnDay forms hypothesis**

→ **User confirms material change**

→ **OwnDay proposes revised result**

→ **User explicitly accepts**

→ **Original preserved as Renegotiated**

→ **Revised Commitment**

→ **Continue execution**

→ **Outcome**

→ **Reflection**

---

# 36. Daily Lifecycle — Failure

**Commit**

→ **Execute or fail to execute**

→ **Commitment remains valid**

→ **Outcome cannot be achieved**

→ **User confirms non-completion**

→ **Failed**

→ **Immediate lightweight Reflection**

→ **Adaptation recommendation**

---

# 37. Daily Lifecycle — Silent Abandonment

**Commit**

→ **Insufficient execution evidence**

→ **Outcome accounting boundary**

→ **OwnDay requests outcome**

→ **No response**

→ **Unaccounted**

There is no automatic Fulfilled, Failed, Renegotiated, or rollover.

---

# 38. Conversational UX Invariants

The following rules apply across all flows.

1. OwnDay remains silent when conversation is not useful.

2. OwnDay never claims to know whether the user is working without evidence.

3. OwnDay uses existing evidence before asking questions.

4. AI interpretations are hypotheses, not facts.

5. Consequential state changes remain under user authority.

6. One message should normally require one user decision.

7. One confirmation may perform several obvious low-friction transitions.

8. Internal state-machine terminology should normally remain hidden.

9. Natural language should work wherever intent is sufficiently clear.

10. Buttons accelerate interaction but should not define the conversational model.

11. A user update does not automatically require an assistant response.

12. `What Now?` returns one concrete recommended action by default.

13. Recovery optimizes for restarting execution, not analyzing failure.

14. Renegotiation should preserve accountability without creating bureaucratic interaction.

15. Additional Steps do not automatically change the Commitment.

16. Silence never proves activity, distraction, failure, or fulfillment.

17. Fulfillment always requires explicit user confirmation.

18. A confirmed Commitment is never silently rewritten.

19. Reflection starts from accumulated evidence rather than generic questions.

20. Reflection should normally require one confirmation or correction.

21. OwnDay should distinguish execution drift from materially changed reality.

22. OwnDay should not convert unwillingness into renegotiation without new evidence.

23. Focus Session duration is execution structure, not a Commitment deadline.

24. Temporary increased accountability requires a specific reason and user agreement.

25. Every unsolicited intervention must have an actionable purpose.

26. Progress is represented through evidence, not fabricated percentages.

27. The user should never need to reconstruct OwnDay's internal state before knowing what to do next.

28. When enough context exists, OwnDay should recommend rather than make the user choose from unnecessary options.

29. Interaction should stop as soon as execution can reasonably resume.

30. The success of the conversational UX is measured partly by how little conversation is required to produce useful execution.

---

# 39. Primary UX Success Criterion

A successful OwnDay interaction is not a long or engaging conversation.

It is:

> **The smallest useful interaction that changes what the user actually does next.**

A typical successful working period should therefore look like:

**Commit → Start → Silence → Execute**

with conversation returning only when:

* the user asks for direction;
* execution breaks down;
* reality materially changes;
* an execution boundary requires a decision;
* an outcome must be accounted for;
* reflection can produce a useful adaptation.

OwnDay should know substantially more about the execution state than it requires the user to explicitly manage.
