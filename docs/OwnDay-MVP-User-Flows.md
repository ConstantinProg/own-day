# OwnDay MVP — User Flows

Version: MVP UX v1

Goal:

Validate the core hypothesis:

> Explicit commitments combined with accountability improve execution consistency.

This flow intentionally minimizes cognitive load and removes non-essential decisions.

---

# 1. Onboarding

## Trigger

User starts OwnDay for the first time.

---

## Steps

### Step 1

System explains the product:

> OwnDay helps you follow through on plans you choose yourself.
>
> Plans become commitments.
>
> Completed tasks must be confirmed.
>
> Ignored tasks count as failed.

---

### Step 2

System asks:

> What would you like to improve right now?

Examples:

* Health
* Career
* Learning
* Business
* Relationships

---

### Step 3

User answers.

---

### Step 4

System stores the answer as the initial direction.

---

### Step 5

System starts planning tomorrow.

---

## Alternative Paths

### User skips answer

Direction:

```text
General Improvement
```

---

## Failure Paths

### User leaves

State:

```text
Onboarding Incomplete
```

---

## End States

### Success

```text
Ready For Planning
```

---

# 2. Daily Planning

## Trigger

Evening planning session.

---

## Steps

### Step 1

System asks:

> How did today go?

User answers briefly.

---

### Step 2

System asks:

> What is the most important thing to move forward tomorrow?

User answers.

---

### Step 3

User enters tasks.

For each task:

* title
* start time
* duration

Example:

```text
Read architecture book
19:00
60 minutes
```

---

### Step 4

System automatically proposes a minimum version.

Example:

```text
Full: 60 min
Minimum: 15 min
```

---

### Step 5

System displays plan.

---

## Alternative Paths

### Unfinished tasks exist

System asks:

```text
Move to tomorrow?
```

Options:

* Yes
* No

---

### Recovery Mode active

System limits plan:

```text
Maximum 3 tasks
```

---

## Failure Paths

### User stops planning

State:

```text
Draft Plan
```

---

## End States

### Success

```text
Plan Ready
```

---

# 3. Commitment Confirmation

## Trigger

Plan completed.

---

## Steps

### Step 1

System displays plan.

---

### Step 2

System asks:

> Do you commit to this plan?

Buttons:

```text
Commit
Edit
```

---

### Step 3

User chooses.

---

### Step 4

If committed:

Plan becomes active.

---

## Alternative Paths

### Edit

Return to Planning.

---

## Failure Paths

### No commitment

Plan remains inactive.

---

## End States

### Success

```text
Committed Plan
```

### Failure

```text
No Active Plan
```

---

# 4. Task Execution

## Trigger

Task start time arrives.

---

## Steps

### T-5 min

Reminder:

> In 5 minutes:
>
> Read architecture book

---

### T=0

Start signal:

> Time to start:
>
> Read architecture book

Buttons:

```text
Started
Can't Start
```

---

### User presses Started

Task state:

```text
In Progress
```

---

## Alternative Paths

### User starts earlier

Task immediately enters:

```text
In Progress
```

---

## Failure Paths

### User presses Can't Start

Transition:

```text
Failure Handling
```

---

### No response

Task remains scheduled.

Completion check still happens.

---

## End States

### Success

```text
In Progress
```

### Failure

```text
Recovery Required
```

---

# 5. Task Completion

## Trigger

Task duration ends.

---

## Steps

System asks:

> Did you complete the task?

Buttons:

```text
Done
Minimum
Not Done
```

---

### Done

Task marked:

```text
Completed
```

---

### Minimum

Task marked:

```text
Minimum Completed
```

---

### Not Done

Transition:

```text
Failure Handling
```

---

## Failure Paths

### No response for 30 minutes

System marks:

```text
Failed
```

Message:

> Task marked as failed because no confirmation was received.

---

## End States

### Completed

```text
Completed
```

### Partial

```text
Minimum Completed
```

### Failed

```text
Failed
```

---

# 6. Failure Handling

## Trigger

Task failed.

---

## Steps

### Step 1

System asks:

> Why didn't it happen?

Options:

```text
Low Energy
Distraction
Avoidance
External
```

---

### Step 2

User selects reason.

---

### Step 3

System proposes a recovery action.

Example:

```text
You reported low energy.

Recommended:
Reduce task to 15 minutes.
```

Buttons:

```text
Accept
Other Options
```

---

### Step 4

If Accept:

Recovery applied automatically.

---

## Alternative Paths

### Other Options

Show:

```text
Reduce
Reschedule
Skip
```

---

## Failure Paths

### No response

System automatically:

```text
Skip
```

---

## End States

### Recovery

```text
Reduced
Rescheduled
```

### Failure

```text
Skipped
```

---

# 7. Recovery Mode

## Trigger

One of:

```text
2 failed tasks in a row
```

or

```text
Completion rate < 50%
```

---

## Steps

System informs user:

> Execution reliability has dropped.
>
> Recovery Mode activated.

---

### Recovery Rules

* Maximum 3 tasks
* Smaller tasks
* Focus on consistency

---

### Recovery Goal

System always displays:

> Complete 2 successful days to restore normal planning.

---

## End States

### Recovery Active

```text
Recovery Mode
```

### Recovery Complete

```text
Normal Planning Restored
```

---

# 8. Day Summary

## Trigger

End of day.

---

## Steps

System generates summary:

```text
Completed: 3
Minimum: 1
Failed: 1

Completion Rate: 80%

Reliability: 72

Streak: 4 days
```

---

### Next Action

System always provides one clear instruction.

Examples:

```text
Tomorrow planning is required.
```

or

```text
Recovery Mode remains active.
```

or

```text
Great work.
Prepare tomorrow's commitments.
```

---

## Alternative Paths

### Successful Day

Reliability increases.

---

### Failed Day

Reliability decreases.

---

## End States

### Day Closed

```text
Awaiting Planning
```

---

# 9. What Now Command

## Trigger

User sends:

```text
what now
```

---

## Steps

System determines:

* current task
* nearest task
* execution state

---

## Response Rules

### Active task exists

Response:

```text
Read architecture book.

35 minutes remaining.

Start now.
```

---

### No active task

Response:

```text
Next task:

Work on OwnDay

15:00

Prepare to begin.
```

---

### No tasks remaining

Response:

```text
No commitments remain today.
```

---

### No active plan

Response:

```text
You do not have a committed plan today.

Create one first.
```

---

## End States

### Guidance Delivered

```text
User Reoriented
```

---

# Complete MVP Lifecycle

```text
Onboarding
    ↓
Planning
    ↓
Commitment
    ↓
Execution
    ↓
In Progress
    ↓
Completion Check
    ↓

 ┌─────────────┬─────────────┐
 │             │             │
Done       Minimum      Not Done
 │             │             │
 └──────┬──────┴──────┬──────┘
        │             │
        ↓             ↓
     Success      Failure
                      ↓
               Recovery Action
                      ↓
               Continue Day
                      ↓
                Day Summary
                      ↓
              Next Planning
```

## UX Principles Applied

* One primary action per screen.
* No dead-end states.
* Visible recovery path after failure.
* Minimal onboarding.
* Reduced decision fatigue.
* Explicit execution states.
* Clear next step after every interaction.
* Telegram-first interaction model.
* Focus on execution, not productivity management.
