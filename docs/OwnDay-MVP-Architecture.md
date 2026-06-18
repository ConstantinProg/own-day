# OwnDay MVP Architecture

## 1. System Context

OwnDay MVP is a Telegram-based personal execution system.

```text
User
  ↓
Telegram Bot
  ↓
OwnDay ASP.NET Core Application
  ↓
PostgreSQL
```

Optional integration:

```text
OwnDay Application
  ↓
LLM Provider
```

The LLM is not part of the core execution loop. OwnDay must work without it.

Core MVP loop:

```text
Onboarding
  → Reflection
  → Planning
  → Commitment
  → Execution
  → Verification
  → Recovery
  → Summary
  → Next Planning
```

The system focuses on commitments, reminders, task verification, failure handling, and adaptation, which matches the MVP requirements and user flows.  

---

## 2. Containers

For MVP, use one deployable ASP.NET Core application.

```text
OwnDay.App
├── Telegram Webhook API
├── Application Use Cases
├── Domain Model
├── EF Core Persistence
├── Scheduler BackgroundService
├── Outbox BackgroundService
├── Telegram Adapter
├── Optional LLM Adapter
└── Health Checks

PostgreSQL
└── OwnDay database
```

Avoid separate services for MVP:

```text
No RabbitMQ
No Redis
No Kubernetes
No separate worker process
No CQRS
No event sourcing
No distributed scheduler
```

---

## 3. Solution Structure

```text
src/
  OwnDay.Domain/
    Users/
    DailyPlans/
    Policies/
    Events/

  OwnDay.Application/
    Onboarding/
    Planning/
    Commitment/
    Execution/
    Recovery/
    Summary/
    WhatNow/
    Scheduling/
    Notifications/
    Conversations/

  OwnDay.Infrastructure/
    Persistence/
    Telegram/
    Llm/
    Outbox/
    Clock/

  OwnDay.App/
    Controllers/
    BackgroundServices/
    Configuration/
    HealthChecks/
```

For MVP, `OwnDay.App` hosts both the Telegram webhook and background workers.

---

## 4. Main Components

## 4.1 Telegram Webhook

Responsibilities:

```text
Receive Telegram updates.
Validate webhook secret.
Deduplicate updates.
Route messages and callbacks.
Return fast HTTP responses.
```

Main classes:

```text
TelegramWebhookController
TelegramUpdateRouter
TelegramCommandParser
TelegramCallbackParser
```

Rules:

```text
Never trust callback payloads directly.
Always resolve referenced entities from the database.
Reject unknown or expired callbacks.
```

---

## 4.2 Conversation State

Telegram flows are multi-step, so conversation state must be explicit.

Examples:

```text
OnboardingDirectionInput
ReflectionInput
PlanningTaskTitleInput
PlanningTaskTimeInput
PlanningTaskDurationInput
CommitmentConfirmation
FailureReasonSelection
RecoveryActionSelection
```

Persistence:

```text
conversation_states
  id
  user_id
  state
  related_plan_id
  related_task_id
  payload_json
  expires_at
  created_at
  updated_at
```

This prevents fragile command handling.

---

## 4.3 Application Use Cases

Prefer use-case handlers instead of many broad services.

```text
StartOnboardingHandler
SaveVisionHandler
StartPlanningHandler
AddTaskHandler
CommitPlanHandler
StartTaskHandler
CompleteTaskHandler
FailTaskHandler
HandleNoResponseHandler
ApplyRecoveryActionHandler
GenerateDaySummaryHandler
GetWhatNowHandler
```

Application layer responsibilities:

```text
Load aggregates.
Call domain behavior.
Persist changes.
Create outbox messages.
Create audit records.
Call optional LLM suggestions.
```

---

## 4.4 Domain Model

Keep the MVP domain model small.

Aggregates:

```text
UserProfile
DailyPlan
```

This follows the approved MVP domain model, which intentionally keeps only two aggregates. 

Important domain policies:

```text
RecoveryPolicy
ReliabilityPolicy
WhatNowPolicy
MinimumCommitmentPolicy
```

Core invariants:

```text
One committed plan per user per local date.
Committed plan requires explicit confirmation.
Committed plan must contain at least one task.
Minimum duration cannot exceed full duration.
No response after timeout becomes failure.
Recovery mode limits the next plan to 3 tasks.
Reliability score remains between 0 and 100.
```

---

## 4.5 LLM Integration

LLM is optional and non-authoritative.

Allowed MVP uses:

```text
Suggest minimum commitment.
Improve wording of task titles.
Summarize reflection.
Suggest a simple plan from user input.
```

Forbidden MVP uses:

```text
Automatically commit a plan.
Override domain rules.
Decide task outcome.
Change reliability score directly.
Block planning when unavailable.
```

Interface:

```csharp
public interface ILlmAssistant
{
    Task<MinimumCommitmentSuggestion?> SuggestMinimumCommitmentAsync(
        string taskTitle,
        int durationMinutes,
        CancellationToken cancellationToken);

    Task<ReflectionSummary?> SummarizeReflectionAsync(
        string rawReflection,
        CancellationToken cancellationToken);
}
```

Fallback:

```text
If LLM fails, continue with deterministic rules.
```

Example:

```text
Minimum duration = min(15 minutes, 25% of full duration), rounded safely.
```

---

## 5. Data Flow

## 5.1 Incoming Telegram Update

```text
Telegram
  → TelegramWebhookController
  → Validate secret
  → Check processed_telegram_updates
  → Route update
  → Execute use case
  → Save DB transaction
  → Add outbox messages
  → Mark update as processed
  → Return 200 OK
```

Use one transaction for:

```text
Domain state changes
Outbox messages
Audit records
Processed update marker
```

---

## 5.2 Outgoing Telegram Message

```text
OutboxWorker
  → Select pending messages
  → Mark as Processing
  → Send via Telegram API
  → Mark as Sent
```

On failure:

```text
Increment retry count.
Save last error.
Schedule next attempt.
```

---

## 5.3 Task Execution Flow

```text
DailyPlan committed
  → Scheduler creates reminder
  → Outbox sends reminder
  → Scheduler creates start signal
  → User presses Started or Can't Start
  → Scheduler creates completion check
  → User selects Done / Minimum / Not Done
  → Execution result is persisted
  → RecoveryPolicy evaluates state
  → Summary is generated at end of day
```

---

## 6. Scheduler Design

Use a simple polling scheduler.

```text
SchedulerBackgroundService
Interval: 60 seconds
```

Responsibilities:

```text
Find due reminders.
Find due start signals.
Find due completion checks.
Find expired response deadlines.
Mark no-response tasks as failed.
Generate end-of-day summaries.
Create notification outbox messages.
```

The scheduler must be idempotent.

Use persisted fields:

```text
plan_tasks.reminder_sent_at
plan_tasks.start_signal_sent_at
plan_tasks.completion_check_sent_at
plan_tasks.response_deadline
daily_plans.summary_generated_at
```

Use idempotency keys for outbox:

```text
task:{taskId}:reminder
task:{taskId}:start
task:{taskId}:completion-check
task:{taskId}:no-response
plan:{planId}:summary
```

Outbox table should enforce:

```text
unique(idempotency_key)
```

Time handling:

```text
Store timestamps in UTC.
Store user's timezone.
Calculate user's LocalDate from timezone.
Never use server local time for business rules.
```

---

## 7. Persistence Model

Use PostgreSQL with EF Core.

Recommended tables:

```text
users
daily_plans
plan_tasks
task_executions
daily_reflections
day_summaries
conversation_states
notification_outbox
processed_telegram_updates
audit_records
```

## 7.1 users

```text
id
telegram_user_id
timezone
personal_vision
reliability_score
execution_streak
recovery_mode_status
recovery_mode_reason
baseline_execution_rate
created_at
updated_at
```

Indexes:

```text
unique(telegram_user_id)
```

---

## 7.2 daily_plans

```text
id
user_id
local_date
status
mode
committed_at
closed_at
summary_generated_at
created_at
updated_at
```

Indexes:

```text
unique(user_id, local_date) where status = 'Committed'
index(user_id, local_date)
```

---

## 7.3 plan_tasks

```text
id
daily_plan_id
title
direction
start_time_utc
duration_minutes
minimum_duration_minutes
status
reminder_sent_at
start_signal_sent_at
completion_check_sent_at
response_deadline
created_at
updated_at
```

Indexes:

```text
index(status, start_time_utc)
index(response_deadline)
```

---

## 7.4 task_executions

```text
id
task_id
result
failure_reason
recovery_action
recorded_at
is_late_response
```

Indexes:

```text
unique(task_id)
```

---

## 7.5 notification_outbox

```text
id
user_id
idempotency_key
message_type
payload_json
status
retry_count
next_attempt_at
created_at
processed_at
last_error
```

Indexes:

```text
unique(idempotency_key)
index(status, next_attempt_at)
```

---

## 7.6 processed_telegram_updates

```text
update_id
processed_at
```

Indexes:

```text
primary key(update_id)
```

---

## 7.7 audit_records

```text
id
user_id
daily_plan_id
action
reason
created_at
```

Audit is required because users must understand why adaptation happened. 

---

## 8. Failure Handling

## 8.1 Telegram retries duplicate update

Problem:

```text
Telegram may resend the same update.
```

Solution:

```text
processed_telegram_updates table.
Ignore already processed update_id.
```

---

## 8.2 App restarts

Problem:

```text
Scheduler may stop between reminder and completion check.
```

Solution:

```text
Persist all scheduler state.
Make scheduler idempotent.
Recover from database state after restart.
```

---

## 8.3 Telegram send failure

Solution:

```text
Use notification_outbox.
Retry with backoff.
Do not lose messages.
```

Retry policy:

```text
1 minute
5 minutes
15 minutes
30 minutes
Then mark Failed
```

---

## 8.4 LLM failure

Solution:

```text
Continue without LLM.
Use deterministic fallback.
Log error.
Do not expose technical details to user.
```

---

## 8.5 User does not respond

This is normal domain behavior.

Rule:

```text
No response for 30 minutes → Failed.
FailureReason = NoResponse.
```

This is part of the MVP accountability model. 

---

## 8.6 Database failure

Solution:

```text
Do not confirm success before transaction commit.
Return HTTP 500 to Telegram webhook.
Let Telegram retry.
```

---

## 8.7 Partial command failure

Example:

```text
Task marked completed but reply message not sent.
```

Solution:

```text
Persist state and outbox in one transaction.
Telegram response can be retried separately.
```

---

## 9. Security

Minimum MVP security:

```text
Validate Telegram webhook secret token.
Store bot token in environment variables.
Store LLM API key in environment variables.
Do not log secrets.
Limit message length.
Validate callback data.
Authorize admin commands by Telegram user id.
Use HTTPS for webhook.
Use database migrations, not manual schema edits.
```

Input limits:

```text
Vision max length: 2000 characters
Task title max length: 200 characters
Reflection answer max length: 2000 characters
Failure comment max length: 500 characters
```

Callback safety:

```text
Callback contains short action id.
Server loads actual entity from DB.
Server verifies ownership.
Server verifies state transition is allowed.
```

---

## 10. Operational Simplicity

Deployment:

```text
One VPS
Docker Compose
One ASP.NET Core container
One PostgreSQL container
```

Example:

```text
docker-compose.yml
├── ownday-app
└── postgres
```

Operational features:

```text
/health
/ready
/version
Serilog console logs
EF Core migrations
PostgreSQL volume backup
```

Useful protected admin commands:

```text
/admin user_state <telegram_id>
/admin pending_outbox
/admin force_summary <telegram_id>
/admin version
```

For MVP, SQL queries are acceptable for metrics and diagnostics.

---

## 11. Deployment Model

```text
GitHub
  → GitHub Actions
  → Build Docker image
  → Push image
  → SSH to VPS
  → docker compose pull
  → docker compose up -d
```

Runtime:

```text
VPS
├── OwnDay.App container
│   ├── Webhook API
│   ├── Scheduler BackgroundService
│   └── Outbox BackgroundService
│
└── PostgreSQL container
    └── persistent volume
```

Environment variables:

```text
ConnectionStrings__Default
Telegram__BotToken
Telegram__WebhookSecret
Telegram__AdminUserIds
Llm__Enabled
Llm__Provider
Llm__ApiKey
App__BaseUrl
```

---

## 12. MVP Scalability Position

This architecture is not designed for large scale.

It is designed for correctness and fast validation.

Expected MVP capacity:

```text
1 developer
1 VPS
1 PostgreSQL database
Telegram-only UI
Tens to hundreds of users
```

Scaling later:

```text
Move workers to separate process.
Add distributed lock for scheduler.
Add Redis only if needed.
Add queue only if outbox pressure grows.
Add analytics read model only after product validation.
```

Do not add these before validation.

---

## Final Architecture Decision

Use:

```text
Modular monolith
Clean Architecture
ASP.NET Core
PostgreSQL
EF Core
Telegram webhook
BackgroundService scheduler
Transactional outbox
Explicit conversation state
Optional LLM adapter
Docker Compose deployment
```

This is the right MVP architecture because it keeps OwnDay simple while protecting the most important risks:

```text
duplicate Telegram updates
lost reminders
incorrect timeouts
LLM failure
unclear conversation state
restart recovery
unexplainable adaptation
```
