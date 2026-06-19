# OwnDay Product Requirements Document (PRD)

Version: 1.0
Status: MVP Approved

---

# 1. Product Overview

## Product Summary

OwnDay is a personal execution system that helps people consistently act according to the future they want to build.

The product connects long-term vision with daily execution through a structured accountability process.

Unlike task managers, OwnDay does not stop at planning.

Unlike habit trackers, OwnDay does not merely record behavior.

OwnDay exists to reduce the gap between intention and action.

---

## Product Hypothesis

Users who voluntarily accept accountability for self-defined commitments will execute important actions more consistently than when relying on motivation alone.

---

## Core Product Loop

Vision → Reflection → Commitment → Execution → Accountability → Adaptation

This loop represents the entire MVP.

Every feature must support at least one stage of this cycle.

---

# 2. Business Goals

## BG-1 Validate Accountability Hypothesis

Demonstrate that structured accountability improves execution consistency.

### Success Criteria

* Average execution rate improves compared to user baseline.

---

## BG-2 Validate Retention

Demonstrate that users continue using OwnDay despite accountability pressure.

### Success Criteria

* ≥50% 7-day retention.

---

## BG-3 Validate Product Value

Demonstrate that users perceive accountability as beneficial.

### Success Criteria

* ≥70% of active users report:

  * "OwnDay helped me complete actions I would otherwise avoid."

---

# 3. User Personas

## Persona 1 — Chronic Planner

Characteristics:

* Creates plans regularly.
* Often fails to execute.
* Suffers from procrastination.
* Wants accountability.

Primary Need:

* Execution support.

---

## Persona 2 — Goal-Oriented Professional

Characteristics:

* Has long-term goals.
* Values discipline.
* Wants measurable progress.

Primary Need:

* Consistency.

---

## Persona 3 — Self-Improvement Enthusiast

Characteristics:

* Consumes educational content.
* Starts new systems frequently.
* Struggles with sustainability.

Primary Need:

* Structure and persistence.

---

# 4. User Problems

| ID | Problem                                                  |
| -- | -------------------------------------------------------- |
| P1 | Long-term goals are disconnected from daily actions.     |
| P2 | Users create plans but do not execute them consistently. |
| P3 | Tasks are silently ignored.                              |
| P4 | Users overestimate future motivation and capacity.       |
| P5 | Users abandon plans after failures.                      |
| P6 | Users struggle to see meaningful progress.               |
| P7 | Users lose focus during the day.                         |
| P8 | Users repeat the same failure patterns.                  |

---

# 5. User Stories

## US-1 Define Personal Vision

Problem: P1

As a user

I want to define who I want to become

So that my daily actions have direction.

---

## US-2 Create Daily Plan

Problem: P1

As a user

I want to create a plan for tomorrow

So that I know what I intend to execute.

---

## US-3 Commit To Plan

Problem: P2

As a user

I want to explicitly commit to my plan

So that it becomes an obligation.

---

## US-4 Receive Execution Guidance

Problem: P7

As a user

I want reminders and start signals

So that I begin execution on time.

---

## US-5 Report Task Outcome

Problem: P2

As a user

I want to report task completion

So that execution is measured accurately.

---

## US-6 Explain Failure

Problem: P8

As a user

I want to indicate why a task failed

So that future plans can improve.

---

## US-7 Recover From Failure

Problem: P5

As a user

I want a recovery path after failure

So that I continue moving forward.

---

## US-8 Ask What To Do Now

Problem: P7

As a user

I want to ask "What now?"

So that I immediately know the next action.

---

## US-9 Review Progress

Problem: P6

As a user

I want to see a daily review

So that I understand my progress.

---

## US-10 Improve Reliability

Problem: P4, P5, P8

As a system

I want to adapt future commitments

So that execution becomes more reliable.

---

# 6. Functional Requirements

## FR-1 Personal Vision

The system shall allow the user to define a personal vision.

### Acceptance Criteria

* User can store a vision statement.
* Vision can be edited.
* Vision is displayed during planning.

---

## FR-2 Direction Categories

The system shall connect tasks with future direction.

### Acceptance Criteria

Each task must reference one direction category.

Examples:

* Health
* Career
* Relationships
* Learning

---

## FR-3 Daily Planning

The system shall support daily planning.

### Acceptance Criteria

Each task contains:

* Title
* Start Time
* Duration
* Minimum Duration
* Direction

---

## FR-4 Minimum Commitment

The system shall support degraded success.

### Acceptance Criteria

Each task must include:

* Full Completion
* Minimum Completion

Minimum duration must be explicitly defined when creating the task.

---

## FR-5 Commitment Confirmation

The system shall require explicit commitment.

### Acceptance Criteria

* User must confirm plan.
* Unconfirmed plans never become active.

---

## FR-6 Day Mode Selection

The system shall support execution modes.

### Acceptance Criteria

Supported modes:

* Focus
* Normal
* Light

Mode is stored in plan.

---

## FR-7 Reminder Delivery

The system shall send task reminders.

### Acceptance Criteria

* Reminder sent 5 minutes before task start.

---

## FR-8 Start Notification

The system shall signal execution start.

### Acceptance Criteria

* Notification sent at scheduled start time.

---

## FR-9 Completion Verification

The system shall verify execution.

### Acceptance Criteria

Available responses:

* Full
* Minimum
* Not Done

Response is persisted.

---

## FR-10A Immediate Reflection

The system shall capture failure context at the moment of failure.
Failure reflection begins immediately after:
- Not Done
- Can't Start
- No Response (when user later reviews the failure)

Failure reflection must occur before recovery action selection.
Acceptance Criteria

The system asks:

1. Why did the task fail?
2. What was the primary obstacle?

Responses are stored and linked to the execution record.

---

## FR-11 Recovery Action

The system shall support recovery actions.

### Acceptance Criteria

Available actions:

* Reduce
* Reschedule
* Skip

---

## FR-12 Non-Response Handling

The system shall prevent silent failure.

### Acceptance Criteria

* Timeout = 30 minutes.
* No response → Failed.
* User receives failure notification.

---

## FR-13 What Now Command

The system shall provide immediate guidance.

### Acceptance Criteria

Command:

"What now"

Response includes:

* Current task
* Next task
* Recommended action

---

## FR-14 Evening Reflection

The system shall provide a lightweight end-of-day review.

Acceptance Criteria

The system asks:

1. What matters most tomorrow?
2. Are you ready to commit?

Failure analysis is not repeated because it is collected during execution.
Responses are stored.

---

## FR-15 Reliability Score

The system shall maintain execution reliability.

### Acceptance Criteria

Reliability range:

0–100

Rules:

* Successful day = +2
* Failed day = -5

Score is displayed in summary.

---

## FR-16 Degradation Detection

The system shall detect declining execution.

### Acceptance Criteria

Trigger if:

* Two failed tasks in a row
  OR
* Completion rate <50%

---

## FR-17 Recovery Protocol

The system shall simplify commitments when reliability declines.

### Acceptance Criteria

When degradation is triggered:

* Next day enters Light Mode.
* Maximum 3 tasks allowed.

Recovery condition:

* Two successful days in a row.

---

## FR-18 End-of-Day Summary

The system shall provide a daily review.

### Acceptance Criteria

Summary includes:

* Full completions
* Minimum completions
* Failures
* Completion rate
* Reliability score
* Execution streak
* Progress

####  Progress example

Health: 2 actions completed
Career: 1 action completed

---

# 7. Pressure Model

## Focus Mode

Characteristics:

* Direct language.
* Additional follow-up reminders.
* Strong accountability tone.

---

## Normal Mode

Characteristics:

* Standard reminders.
* Neutral tone.

---

## Light Mode

Characteristics:

* Supportive language.
* Reduced pressure.
* Fewer follow-ups.

---

# 8. Day Lifecycle

## Phase 1 — Reflection

Review previous day.

---

## Phase 2 — Planning

Create tomorrow's tasks.

---

## Phase 3 — Commitment

Confirm plan.

---

## Phase 4 — Execution

Perform scheduled actions.

---

## Phase 5 — Verification

Confirm results.

---

## Phase 6 — Recovery

Handle failures.

---

## Phase 7 — Summary

Review outcomes.

---

# 9. Non-Functional Requirements

## NFR-1 Platform

Telegram-only MVP.

### Test

Entire workflow works inside Telegram.

---

## NFR-2 Reliability

### Test

99% of reminders delivered within ±1 minute.

---

## NFR-3 Data Integrity

### Test

No execution records lost after restart.

---

## NFR-4 Availability

### Test

Monthly uptime ≥95%.

---

## NFR-5 Performance

### Test

Command responses <3 seconds.

---

## NFR-6 Auditability

### Test

User can see why adaptation occurred.

---

# 10. Success Metrics

## Baseline Execution Rate

Collected during onboarding.

Question:

"Out of 10 planned tasks, how many do you usually complete?"

---

## SM-1 Commitment Rate

Target:

≥70%

---

## SM-2 Task Completion Rate

Target:

≥60%

---

## SM-3 Execution Improvement

Target:

Execution rate improves versus baseline.

---

## SM-4 7-Day Retention

Target:

≥50%

---

## SM-5 Active Usage

Target:

User interacts on ≥5 of 7 days.

---

## SM-6 Accountability Perception

Target:

≥70% positive responses.

---

# 11. MVP Scope

Included:

* Personal Vision
* Daily Planning
* Reflection
* Commitment
* Reminders
* Execution Tracking
* Failure Classification
* Recovery Actions
* Reliability Score
* Adaptive Simplification
* End-of-Day Summary
* What Now Command

---

# 12. Non-Goals

MVP excludes:

* AI coaching
* Automatic life planning
* Goal decomposition
* Habit tracking
* Calendar integrations
* Mobile apps
* Web apps
* Social accountability
* Gamification
* Financial penalties
* Analytics dashboards
* Voice interfaces

---

# 13. Risks

## R1 Accountability Rejection

Users may leave because of pressure.

Mitigation:

* Multiple pressure modes.

---

## R2 False Reporting

Users may report dishonest results.

Mitigation:

* Accept self-reporting in MVP.

---

## R3 Notification Fatigue

Users may ignore reminders.

Mitigation:

* Limit communication to execution-critical events.

---

## R4 Over-Simplification

Adaptation may optimize for completion rather than progress.

Mitigation:

* Preserve connection to strategic direction.
* Limit simplification scope.

---

## R5 Insufficient Behavioral Impact

Accountability may not improve execution.

Mitigation:

* Measure baseline and improvement.

---

# MVP Success Definition

The MVP is successful if users demonstrate measurable improvement in execution consistency compared to their baseline while remaining engaged with the accountability process for at least seven consecutive days.

The product succeeds when users increasingly experience:

> I am doing what I believe matters.

# Experimental Areas

The following mechanisms are intentionally unspecified in MVP
and may change based on user feedback:

- Reliability calculation
- Pressure model
- Recovery protocol
- Completion scoring
- Adaptation rules