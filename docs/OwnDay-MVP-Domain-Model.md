# OwnDay Domain Model (MVP)

Version: 1.0
Status: Approved for MVP

---

# Overview

OwnDay is a behavioral execution system designed to reduce the gap between intention and action.

The MVP focuses on validating a single hypothesis:

> Explicit commitments combined with accountability improve execution consistency.

The domain model intentionally prioritizes simplicity over completeness.

Only concepts required to support the MVP execution loop are included.

Core loop:

```text
Vision
    ↓
Reflection
    ↓
Commitment
    ↓
Execution
    ↓
Accountability
    ↓
Adaptation
```

---

# 1. Ubiquitous Language

| Term               | Definition                                                           |
| ------------------ | -------------------------------------------------------------------- |
| User               | Person using OwnDay.                                                 |
| Vision             | Description of the future the user wants to build.                   |
| Direction          | Area of life associated with tasks (Health, Career, Learning, etc.). |
| Daily Plan         | Plan for a specific day.                                             |
| Draft Plan         | Plan not yet committed.                                              |
| Commitment         | Explicit confirmation that turns a plan into an obligation.          |
| Plan Task          | Action scheduled within a daily plan.                                |
| Minimum Commitment | Reduced acceptable version of a task.                                |
| Execution Result   | Outcome of a task execution.                                         |
| Failure Reason     | Explanation for a failed task.                                       |
| Recovery Action    | Action taken after failure.                                          |
| Recovery Mode      | Simplified execution mode after repeated failures.                   |
| Reliability Score  | Measure of execution consistency.                                    |
| What Now           | Command that returns the next actionable step.                       |

---

# 2. Bounded Contexts

## 2.1 User Direction Context

Purpose:

Manage the user's long-term direction.

Contains:

* UserProfile
* PersonalVision
* Direction

Responsibilities:

* Store user vision.
* Store life directions.
* Maintain reliability state.

---

## 2.2 Daily Execution Context

Core MVP context.

Contains:

* DailyPlan
* PlanTask
* TaskExecution
* RecoveryState
* DaySummary

Responsibilities:

* Planning
* Commitment
* Execution tracking
* Failure handling
* Adaptation
* Summary generation

---

## 2.3 Notification Context

Infrastructure-supporting context.

Contains:

* NotificationOutbox
* ScheduledNotification
* DeliveryAttempt

Responsibilities:

* Reminder delivery
* Start notifications
* Completion checks

---

## 2.4 Audit Context

Contains:

* AuditRecord

Responsibilities:

* Explain system decisions.
* Preserve accountability history.
* Support adaptation transparency.

---

# 3. Aggregates

## 3.1 UserProfile Aggregate

### Aggregate Root

```text
UserProfile
```

### Purpose

Represents the user's long-term execution identity.

### Responsibilities

* Store personal vision.
* Store directions.
* Store timezone.
* Maintain reliability score.
* Manage recovery mode.

### Invariants

* ReliabilityScore must remain between 0 and 100.
* TimeZone is required.
* Recovery mode requires an activation reason.
* Recovery mode ends only after two successful days.
* General Improvement is used when no direction exists.

### Structure

```text
UserProfile
    UserId
    TelegramUserId
    TimeZone
    PersonalVision
    Directions[]
    ReliabilityScore
    RecoveryState
    BaselineExecutionRate
```

---

## 3.2 DailyPlan Aggregate

### Aggregate Root

```text
DailyPlan
```

### Purpose

Represents a complete execution day.

### Responsibilities

* Create draft plans.
* Manage tasks.
* Confirm commitments.
* Track execution.
* Record failures.
* Apply recovery actions.
* Generate summaries.
* Close the day.

### Invariants

* One committed plan per user per local date.
* Draft plans are editable.
* Committed plans cannot be silently modified.
* A committed plan must contain at least one task.
* Every task must contain:

  * title
  * start time
  * duration
  * minimum duration
  * direction
* Minimum duration must not exceed duration.
* No response after timeout becomes failure.
* Day closes only when all tasks reach terminal state.
* Recovery mode limits plans to a maximum of 3 tasks.
* Completion rate below 50% triggers next-day simplification.

### Structure

```text
DailyPlan
    DailyPlanId
    UserId
    LocalDate
    Status
    Mode
    Commitment
    Reflection
    Tasks[]
    Executions[]
    RecoveryState
    Summary
```

---

# 4. Entities

## PlanTask

Purpose:

Represents a scheduled action.

Responsibilities:

* Store task definition.
* Store minimum commitment.
* Track notification state.

### Invariants

* Title cannot be empty.
* Duration must be positive.
* Minimum duration must be positive.
* Minimum duration cannot exceed duration.
* Direction is required.
* Committed tasks cannot be removed.

### Structure

```text
PlanTask
    TaskId
    Title
    Direction
    TimeBlock
    MinimumCommitment
    Status
    ReminderSentAt
    StartSignalSentAt
    CompletionCheckSentAt
    ResponseDeadline
```

---

## TaskExecution

Purpose:

Stores the outcome of execution.

Responsibilities:

* Record execution result.
* Record timestamps.
* Record failure information.
* Record recovery actions.

### Invariants

* One final execution result per task.
* Full and Minimum results cannot contain failure reasons.
* Failed, Ignored and Skipped results require a reason.

### Structure

```text
TaskExecution
    ExecutionId
    TaskId
    Result
    FailureReason
    RecoveryAction
    RecordedAt
    IsLateResponse
```

---

## DailyReflection

Purpose:

Stores evening reflection.

Responsibilities:

* Capture lessons learned.
* Capture failures.
* Capture tomorrow's focus.

### Structure

```text
DailyReflection
    WhatWorked
    WhatFailed
    WhatMattersTomorrow
```

---

## DaySummary

Purpose:

Represents the final outcome of the day.

Responsibilities:

* Calculate completion metrics.
* Update reliability.
* Generate next instruction.

### Structure

```text
DaySummary
    FullCompletedCount
    MinimumCompletedCount
    FailedCount
    CompletionRate
    ReliabilityScoreAfter
    ExecutionStreakAfter
    NextInstruction
```

---

# 5. Value Objects

## UserId

Unique user identifier.

---

## TelegramUserId

Telegram account identifier.

Invariant:

```text
Must be unique.
```

---

## LocalDate

Represents a user's day.

Invariant:

```text
Calculated using the user's timezone.
```

---

## UserTimeZone

Represents the user's timezone.

Invariant:

```text
Required.
```

---

## PersonalVision

Represents future direction.

Invariant:

```text
Cannot contain an empty string.
```

---

## Direction

Examples:

```text
Health
Career
Learning
Business
Relationships
General Improvement
```

---

## TaskTitle

Invariant:

```text
Non-empty
Trimmed
Maximum length enforced
```

---

## TimeBlock

Structure:

```text
StartTime
Duration
EndTime
```

Invariant:

```text
Duration > 0
EndTime = StartTime + Duration
```

---

## MinimumCommitment

Invariant:

```text
Duration > 0
Duration <= Full Duration
```

---

## DayMode

```text
Focus
Normal
Light
```

---

## PlanStatus

```text
Draft
Committed
Closed
Abandoned
```

---

## TaskStatus

```text
Scheduled
InProgress
Completed
MinimumCompleted
Failed
Skipped
```

---

## ExecutionResult

```text
Full
Minimum
Failed
Ignored
Skipped
```

---

## FailureReason

```text
LowEnergy
Distraction
Avoidance
External
NoResponse
SystemDeliveryFailure
```

---

## RecoveryAction

```text
Reduce
Reschedule
Skip
```

---

## ReliabilityScore

Range:

```text
0..100
```

MVP Rules:

```text
Successful Day = +2
Failed Day = -5
```

---

## CompletionRate

Range:

```text
0..100%
```

---

## Commitment

Represents a confirmed plan.

Structure:

```text
CommittedAt
CommittedByUser
CostCondition
```

Invariant:

```text
Created only after explicit confirmation.
```

---

# 6. Domain Events

## User Events

```text
UserOnboarded
PersonalVisionDefined
DirectionSelected
ReliabilityChanged
RecoveryModeActivated
RecoveryModeDeactivated
```

---

## Plan Events

```text
DailyPlanDraftCreated
PlanTaskAdded
DailyPlanCommitted
DailyPlanClosed
```

---

## Execution Events

```text
TaskStarted
TaskCompletedFully
TaskCompletedMinimally
TaskFailed
TaskMarkedFailedDueToNoResponse
```

---

## Recovery Events

```text
FailureReasonRecorded
RecoveryActionApplied
TaskReduced
TaskRescheduled
TaskSkipped
NextDaySimplificationRequired
```

---

## Summary Events

```text
DaySummaryGenerated
ExecutionStreakChanged
```

---

# 7. Domain Services

## RecoveryPolicy

Purpose:

Determine whether recovery mode should activate.

### Rules

```text
2 failed tasks in a row
OR
Completion Rate < 50%
```

---

## ReliabilityPolicy

Purpose:

Calculate the next reliability score.

---

## WhatNowPolicy

Purpose:

Determine the current actionable instruction.

Possible outputs:

```text
Current task
Next task
No remaining tasks
No active plan
```

---

# 8. Application Services

## PlanningService

```text
StartPlanning
AddTask
EditTask
CommitPlan
```

---

## ExecutionService

```text
StartTask
CompleteTaskFull
CompleteTaskMinimum
FailTask
HandleNoResponse
```

---

## RecoveryService

```text
RecordFailureReason
ApplyRecoveryAction
SimplifyRemainingTasks
```

---

## SummaryService

```text
CloseDay
GenerateSummary
UpdateReliability
```

---

## WhatNowService

```text
HandleWhatNowCommand
```

---

## SchedulerService

Runs every minute.

Responsibilities:

```text
Find due reminders
Find due start notifications
Find due completion checks
Find expired deadlines
Generate outbox messages
Handle no-response failures
```

---

# 9. Infrastructure Models

## NotificationOutbox

Purpose:

Reliable message delivery.

```text
OutboxMessage
    Id
    UserId
    MessageType
    Payload
    Status
    RetryCount
    CreatedAt
    ProcessedAt
```

---

## AuditRecord

Purpose:

Explain why the system made a decision.

```text
AuditRecord
    Id
    UserId
    DailyPlanId
    Action
    Reason
    CreatedAt
```

Examples:

```text
Task marked failed because no response was received.
Recovery Mode activated because completion rate fell below 50%.
Tomorrow plan limited to 3 tasks due to Recovery Mode.
```

---

# 10. Relationships

```text
UserProfile
    1 ── many DailyPlan

UserProfile
    owns PersonalVision
    owns Direction[]
    owns ReliabilityScore
    owns RecoveryState

DailyPlan
    owns DailyReflection
    owns PlanTask[]
    owns TaskExecution[]
    owns DaySummary
    owns Commitment

PlanTask
    1 ── 0..1 TaskExecution

DailyPlan
    emits Domain Events

Application Services
    load aggregates
    invoke domain behavior
    persist state
    create outbox messages

SchedulerService
    reads due tasks
    invokes execution logic
    creates notification messages

Telegram Adapter
    consumes outbox
    sends messages
```

---

# 11. Recommended Solution Structure

```text
src/
  OwnDay.Domain/
    Users/
    DailyPlans/
    Policies/
    Events/

  OwnDay.Application/
    Planning/
    Execution/
    Recovery/
    Summary/
    WhatNow/
    Scheduling/

  OwnDay.Infrastructure/
    Persistence/
    Telegram/
    Outbox/
    Clock/

  OwnDay.Worker/
    SchedulerWorker.cs
    OutboxWorker.cs

  OwnDay.Bot/
    TelegramWebhookController.cs
```

---

# MVP Design Principle

The domain model intentionally contains only two aggregates:

```text
UserProfile
DailyPlan
```

Everything else is implemented as:

* Entities
* Value Objects
* Domain Services
* Application Services
* Infrastructure Components

This keeps the architecture aligned with the MVP goal:

> Convert plans into commitments, enforce execution, record outcomes, and influence the next day through accountability.
