# OwnDay MVP Architecture

**Status:** Proposed MVP Architecture
**Architecture style:** Modular Monolith
**Application style:** Simple Clean Architecture + Vertical Slices
**Primary interface:** Telegram
**Runtime:** ASP.NET Core
**Persistence:** PostgreSQL + EF Core
**AI:** External LLM provider
**Primary optimization target:** MVP simplicity and fast product validation

---

# 1. Architecture Goals

The OwnDay MVP architecture exists to support one product experiment:

> Can a lightweight accountability system materially improve execution of self-directed work?

The architecture should therefore optimize for:

* fast product iteration;
* deterministic domain behavior;
* reliable handling of Telegram interactions;
* explicit accountability history;
* safe LLM integration;
* simple deployment;
* simple debugging;
* recovery from infrastructure failures;
* enough persisted evidence to evaluate MVP hypotheses.

The architecture should not optimize prematurely for:

* horizontal scale;
* distributed execution;
* large user volumes;
* pluggable workflow engines;
* event sourcing;
* generalized productivity infrastructure;
* advanced analytics.

---

# 2. Core Architectural Principles

## 2.1 Modular Monolith First

OwnDay is deployed as one ASP.NET Core application backed by one PostgreSQL database.

Telegram handling, scheduling, LLM integration, domain execution and notification delivery live in one deployable system.

They are separated by code boundaries, not distributed-system boundaries.

---

## 2.2 Domain State Is Authoritative

Business truth is represented by the domain model.

External mechanisms may provide input, observations or proposals, but may not bypass domain rules.

Only permitted domain operations may change:

* Commitment lifecycle;
* Commitment outcome;
* Steps;
* Focus Session lifecycle;
* execution risk;
* renegotiation state.

---

## 2.3 User Owns Consequential Decisions

The user retains authority over:

* confirming a Commitment;
* confirming fulfillment;
* confirming failure where ambiguity exists;
* accepting renegotiation;
* changing direction;
* correcting AI interpretations.

AI may propose.

Scheduler may trigger evaluation.

Telegram may deliver input.

None of them independently own consequential domain transitions.

---

## 2.4 Evidence Is Not Domain Truth

OwnDay distinguishes:

### Execution Evidence

Observed or reported information about execution.

Examples:

* user reported difficulty;
* user reported drift;
* user reported blocker;
* planned start was not confirmed;
* material new information was reported.

### Domain Event

A fact that became true because a valid domain transition occurred.

Examples:

* CommitmentConfirmed;
* StepCompleted;
* FocusSessionStarted;
* CommitmentRenegotiated;
* CommitmentFulfilled.

The relationship is commonly:

**Evidence → Decision → Domain Transition → Domain Event**

Evidence may justify a decision.

It does not itself imply the decision.

---

## 2.5 Conversation State Is Not Business State

Telegram conversations may require temporary conversational context.

That context may help answer:

> What question is OwnDay currently waiting for?

It must never become the authoritative source for:

* Commitment lifecycle;
* Focus Session lifecycle;
* current outcome;
* execution risk;
* Steps;
* renegotiation state.

Business state must always be reconstructable independently of conversational state.

---

## 2.6 LLM Output Is Provisional

LLM output represents:

* interpretation;
* proposal;
* hypothesis;
* summary.

It does not represent authoritative state.

Any LLM response that may influence consequential behavior must be validated against current domain state before use.

---

# 3. System Context

## 3.1 External Actors and Systems

```text
User
 │
 ▼
Telegram
 │
 │ HTTPS webhook / Bot API
 ▼
OwnDay
 │
 ├── PostgreSQL
 │
 └── LLM Provider
```

### User

Provides:

* goals and context;
* Commitments;
* progress updates;
* execution evidence;
* confirmations;
* corrections;
* consequential decisions.

### Telegram

Provides the user interaction channel.

Telegram is not part of the domain model.

### LLM Provider

Provides probabilistic interpretation and generation.

Examples:

* reformulating a Commitment;
* suggesting Steps;
* identifying a likely Next Step;
* interpreting user-reported blockers;
* proposing Reflection hypotheses.

### PostgreSQL

Provides durable storage and coordination for:

* domain state;
* scheduled triggers;
* notification outbox;
* Telegram deduplication;
* execution evidence;
* accountability history;
* minimal experimental telemetry.

---

# 4. Runtime Containers

The MVP has three conceptual runtime containers.

```text
Internet
   │
   ▼
Reverse Proxy
   │
   ▼
OwnDay ASP.NET Core
   │
   ▼
PostgreSQL
```

The ASP.NET Core process contains:

* Telegram webhook endpoint;
* Application vertical slices;
* Domain model;
* scheduler worker;
* outbox worker;
* LLM adapter.

The LLM provider and Telegram API are external services.

No separate:

* scheduler service;
* worker service;
* AI service;
* notification service;
* message broker

is required for MVP.

---

# 5. Solution Structure

Recommended projects:

```text
src/

OwnDay.Domain/
OwnDay.Application/
OwnDay.Infrastructure/
OwnDay.App/
```

---

# 6. Dependency Rules

```text
OwnDay.App
    │
    ├── OwnDay.Application
    └── OwnDay.Infrastructure

OwnDay.Infrastructure
    │
    └── OwnDay.Application

OwnDay.Application
    │
    └── OwnDay.Domain
```

`OwnDay.Domain` depends on nothing else in the solution.

The Domain must not reference:

* EF Core;
* Telegram.Bot;
* ASP.NET Core;
* OpenAI SDK;
* scheduler infrastructure.

---

# 7. OwnDay.Domain

The MVP domain should remain deliberately small.

Primary domain concepts:

```text
Commitment
Step
FocusSession
```

Supporting domain concepts:

```text
CommitmentLifecycle
CommitmentOutcome
ExecutionRisk
StepStatus
StepOrigin
FocusSessionStatus
FocusSessionEndReason
CommitmentDay
```

`User` may exist as a domain entity/configuration root where useful, but the MVP should not create a rich User Aggregate unless actual invariants require it.

`Reflection` should initially remain a lightweight persisted execution-learning record rather than being forced into a complex Aggregate Root.

---

# 8. Commitment Aggregate

`Commitment` is the primary accountability consistency boundary.

Responsibilities:

* confirmation;
* lifecycle transitions;
* Steps;
* Current / Next Step;
* fulfillment;
* failure;
* renegotiation;
* execution risk.

Typical state:

```text
Id
UserId
CommitmentDay
PromisedOutcome

Lifecycle
Outcome
ExecutionRisk

Steps
CurrentStepId

ConfirmedAt
ClosedAt
Version
```

Core invariants include:

* Draft is not an active Commitment;
* confirmed Commitment cannot be silently rewritten;
* Steps have accountability only through their Commitment;
* Fulfilled requires explicit confirmation;
* silence never means Fulfilled;
* outcome transitions must be valid;
* closed Commitment cannot silently return to Active.

---

# 9. Rule of Three

The user may have at most three active Commitments for one operative day.

This is a cross-aggregate invariant.

The MVP should enforce it transactionally.

Recommended implementation:

```text
PostgreSQL advisory transaction lock
    keyed by UserId + CommitmentDay
```

Flow:

```text
BEGIN

acquire advisory transaction lock

count active Commitments

if count >= 3
    reject command

insert Commitment

COMMIT
```

A `SERIALIZABLE` transaction is an acceptable alternative.

Do not introduce a new aggregate solely to enforce this limit.

---

# 10. FocusSession Aggregate

`FocusSession` is a separate consistency boundary.

It represents one deliberate execution attempt.

Typical data:

```text
Id
UserId
CommitmentId
IntendedStepId

PlannedStartAt
ExpectedDuration

ActualStartAt
ActualEndAt

Status
EndReason
Version
```

A Focus Session may move independently while the Commitment remains Active.

Ending a Focus Session does not determine Commitment outcome.

---

# 11. Reflection Model

Reflection remains intentionally lightweight in the MVP.

A Reflection record may contain:

```text
Id
UserId
CommitmentId
FocusSessionId?

TriggerType

ProposedHypothesis
UserCorrection
ConfirmedConclusion
AdaptationRecommendation

CreatedAt
CompletedAt
```

No generalized Reflection graph or multi-aggregate relationship model is required yet.

If future product behavior proves that Reflection needs independent complex invariants, it may later become a richer Aggregate Root.

---

# 12. Application Architecture

The Application layer is organized by Vertical Slices.

Do not build generic domain/application services such as:

```text
CommitmentService
ExecutionService
FocusService
ReflectionService
```

when a specific use case can represent the behavior directly.

Example structure:

```text
Application/

Onboarding/
    StartOnboarding
    UpdateGoalContext

Commitments/
    ProposeCommitment
    ConfirmCommitment
    ConfirmFulfilled
    ConfirmFailed
    RenegotiateCommitment

Steps/
    AddStep
    CompleteStep
    ChangeNextStep

FocusSessions/
    PlanFocusSession
    StartFocusSession
    EndFocusSession

Execution/
    ReportDifficulty
    ReportDrift
    ReportBlocker
    ReportMaterialNewInformation

Recovery/
    StartRecovery
    ConfirmRestart

OutcomeAccounting/
    RequestOutcome
    RecordOutcome
    ExpireAccounting

Reflection/
    BuildReflectionHypothesis
    ConfirmReflection

Guidance/
    WhatNow
```

A slice typically contains:

```text
Command / Query
Handler
Validator
Result
```

---

# 13. Application Handler Responsibility

A command handler coordinates work.

Typical synchronous flow:

```text
load state
→ validate request
→ invoke domain behavior
→ persist domain changes
→ persist relevant history
→ enqueue outbound notification
→ commit transaction
```

The Application layer may invoke:

* domain policies;
* persistence abstractions;
* LLM abstraction;
* scheduler abstraction;
* notification outbox abstraction.

Business invariants remain in the Domain where they naturally belong.

---

# 14. Telegram Adapter

Recommended components:

```text
TelegramWebhookController
TelegramUpdateRouter
TelegramIntentInterpreter
TelegramMessageRenderer
```

Responsibilities:

### WebhookController

* authenticate webhook;
* receive Telegram Update;
* pass it to Application processing;
* return HTTP response.

### UpdateRouter

Maps an Update to the relevant application interaction.

It must not contain domain transition logic.

### Intent Interpreter

May combine deterministic routing with LLM-based interpretation for natural-language input.

### Message Renderer

Transforms application results into Telegram messages/buttons.

Telegram-specific concepts must not leak into Domain.

---

# 15. Telegram Update Idempotency

Telegram delivery is treated as at-least-once.

Persist:

```text
processed_telegram_updates
```

Minimal schema:

```text
update_id PK
received_at
processed_at
```

A Telegram update must be processed atomically with the business mutation it causes.

Transaction:

```text
BEGIN

INSERT processed_telegram_updates(update_id)

execute application command

persist domain changes

persist accountability history

insert notification_outbox records

COMMIT
```

If `update_id` already exists:

```text
treat update as already processed
```

This ensures:

* domain mutation;
* deduplication;
* outbound message intent

cannot diverge because of a process crash.

---

# 16. Conversation State

Persist minimal transient conversation context.

Example:

```text
conversation_states

user_id
intent
context_json
updated_at
expires_at
```

Allowed examples:

```text
AwaitingCommitmentClarification
AwaitingFulfillmentConfirmation
AwaitingRenegotiationConfirmation
AwaitingOutcomeAccounting
```

ConversationState must not duplicate full business workflows.

A hard invariant:

> Deleting ConversationState may harm UX, but must not corrupt or erase authoritative execution state.

---

# 17. Execution Evidence

Persist only observations useful for:

* accountability;
* recovery;
* reflection;
* adaptation;
* product validation.

Schema:

```text
execution_evidence

id
user_id
commitment_id
focus_session_id nullable

evidence_type
details_json nullable

occurred_at
recorded_at
source
```

Examples:

```text
DifficultyReported
DriftReported
BlockerReported
MaterialNewInformationReported
PlannedStartNotConfirmed
ExplicitBreakReported
ReturnPointMissed
```

Execution Evidence should preferably be append-only.

---

# 18. Accountability History

Do not implement event sourcing.

Instead persist a narrow append-only history of meaningful accepted domain facts.

Recommended name:

```text
accountability_events
```

Schema:

```text
id
user_id
aggregate_type
aggregate_id
event_type
details_json nullable
occurred_at
```

Examples:

```text
CommitmentConfirmed
StepCompleted
FocusSessionStarted
FocusSessionEnded
CommitmentRenegotiated
CommitmentFulfilled
CommitmentFailed
CommitmentUnaccounted
```

Not every internal domain event needs persistence.

Only retain events that are useful for:

* accountability reconstruction;
* Reflection;
* experimental metrics;
* debugging meaningful user-visible behavior.

---

# 19. Evidence vs Accountability Event

The distinction is mandatory.

Example:

```text
User says:
"I discovered that persistence requires an architecture change."

↓

ExecutionEvidence:
MaterialNewInformationReported

↓

OwnDay forms hypothesis

↓

User confirms that original Commitment should change

↓

Domain transition:
Commitment.Renegotiate(...)

↓

AccountabilityEvent:
CommitmentRenegotiated
```

Never convert observations directly into historical conclusions without the appropriate policy/user decision.

---

# 20. LLM Integration

Use one infrastructure client:

```text
ILlmClient
```

and task-oriented Application-level functions such as:

```text
CommitmentAssistant
ExecutionInterpreter
ReflectionAssistant
```

Avoid prematurely creating separate provider abstractions for every AI task.

---

# 21. Structured LLM Responses

LLM responses should be structured.

Examples:

```text
CommitmentProposal

SuggestedOutcome
ReasoningSummary
SuggestedSteps[]
```

or:

```text
ExecutionInterpretation

PossibleMeaning
Confidence
RequiresUserConfirmation
SuggestedNextAction
```

Avoid parsing arbitrary free-form prose when structured output is available.

---

# 22. LLM Context Versioning

LLM calls occur outside database transactions.

Therefore every state-dependent LLM result must be associated with the state version from which it was produced.

Example:

```text
CommitmentVersion = 7
```

Flow:

```text
load Commitment v7

↓

call LLM

↓

response generated for v7

↓

reload current state

↓

if current version == 7
    proposal may be used
else
    proposal is stale
```

A stale proposal must not automatically mutate domain state.

Depending on the use case OwnDay may:

* discard it;
* regenerate it;
* use it only as non-authoritative text if still harmless.

This is particularly important for:

* What Now?;
* Next Step proposals;
* Renegotiation;
* Reflection;
* blocker interpretation.

---

# 23. LLM Failure Policy

LLM integration must be optional to core accountability correctness.

### Optional AI assistance

Examples:

* Commitment wording;
* Step decomposition;
* summaries.

On failure:

```text
use deterministic/manual fallback
```

### Interpretation

If OwnDay cannot safely interpret input:

```text
ask the user a minimal clarifying question
```

### Consequential transitions

Never infer automatically because of an LLM timeout or ambiguous result.

The MVP should remain capable of:

* confirming Commitments;
* starting Focus Sessions;
* recording outcomes;
* recording explicit evidence

even if the LLM provider is unavailable.

---

# 24. Scheduler Design

Use PostgreSQL + ASP.NET Core `BackgroundService`.

Do not introduce:

* Quartz;
* Hangfire;
* Temporal;
* RabbitMQ;
* Kafka.

The scheduler persists minimal temporal triggers.

Recommended table:

```text
scheduled_triggers
```

Schema:

```text
id
trigger_type
user_id
aggregate_id
due_at_utc
processed_at nullable
attempt_count
next_attempt_at nullable
last_error nullable
```

Avoid generic arbitrary payloads unless strictly needed.

A trigger should primarily identify:

> what state should now be re-evaluated?

not:

> what action should definitely happen?

---

# 25. Scheduled Trigger Types

Initial examples:

```text
PlannedFocusStartBoundary
FocusSessionExpectedEnd
TemporaryRecoveryCheckpoint
ExplicitReturnPoint
CommitmentOutcomeDue
OutcomeAccountingExpiration
```

Each trigger invokes an Application use case.

---

# 26. Scheduler Processing

Conceptual query:

```text
find due unprocessed triggers
claim a bounded batch
```

Multiple application instances are not required for MVP, but processing should remain safe if the same trigger is observed more than once.

Handler flow:

```text
load current state

↓

verify that trigger is still relevant

↓

if no longer relevant
    complete as no-op

else
    execute use case

↓

mark processed
```

Example:

A `PlannedFocusStartBoundary` trigger is due.

If the user already started the Focus Session, no intervention is required.

---

# 27. Scheduler Authority Boundary

Scheduler may establish temporal facts.

For example:

```text
planned start grace boundary has passed
```

Scheduler may not conclude:

```text
user is distracted
user failed
user is procrastinating
```

Those conclusions require additional evidence and/or user confirmation.

---

# 28. Transactional Outbox

Telegram notifications that matter to the workflow are persisted in:

```text
notification_outbox
```

Schema:

```text
id
user_id
chat_id
message_type
payload_json

idempotency_key

status
attempt_count
next_attempt_at

created_at
sent_at nullable
last_error nullable
```

`idempotency_key` should be unique.

---

# 29. Outbox Flow

Within the same transaction as domain mutation:

```text
domain state change
+
accountability history
+
outbox message
```

are committed together.

Later:

```text
OutboxWorker
→ Telegram Bot API
→ mark sent
```

If Telegram fails, domain state remains correct and delivery can retry.

---

# 30. Persistence Model

Use PostgreSQL and EF Core.

Prefer normal relational columns for business data.

Core tables:

```text
users
telegram_identities

commitments
commitment_steps
focus_sessions

execution_evidence
accountability_events
reflections

conversation_states

scheduled_triggers
notification_outbox
processed_telegram_updates

experiment_events
```

---

# 31. Users

Minimal schema:

```text
users

id
time_zone_id
goal_context nullable
created_at
updated_at
```

Do not introduce a complete Goal subsystem in the MVP.

---

# 32. Telegram Identity

Telegram identity belongs to Infrastructure integration state.

```text
telegram_identities

telegram_user_id PK
user_id UNIQUE FK
telegram_chat_id
created_at
```

Telegram identifiers should not become domain identity.

---

# 33. Commitments

Suggested schema:

```text
commitments

id
user_id
commitment_day

promised_outcome

lifecycle
outcome
execution_risk

current_step_id nullable

confirmed_at nullable
closed_at nullable

renegotiated_to_commitment_id nullable

version

created_at
updated_at
```

`version` is used for optimistic concurrency and stale LLM detection.

---

# 34. Steps

```text
commitment_steps

id
commitment_id

description
status
origin

created_at
completed_at nullable
removed_at nullable
```

No:

```text
parent_step_id
dependency_graph
subtasks
```

The MVP decomposition remains flat.

---

# 35. Focus Sessions

```text
focus_sessions

id
user_id
commitment_id
intended_step_id nullable

planned_start_at nullable
expected_duration_minutes nullable

actual_start_at nullable
actual_end_at nullable

status
end_reason nullable

version

created_at
updated_at
```

---

# 36. Time Model

Store physical timestamps in UTC.

Persist the user's IANA/compatible timezone identifier.

`CommitmentDay` is derived using the user's operative timezone.

Business rules must not depend on VPS local timezone.

An active daily Commitment does not silently roll into the next operative day.

---

# 37. Experimental Telemetry

The MVP must produce enough data to evaluate whether OwnDay changes actual behavior.

Do not introduce a separate analytics platform.

Use PostgreSQL.

Hard requirement:

> Every major MVP behavioral outcome must be reconstructable from persisted domain state, evidence, accountability history or explicit experiment telemetry.

Persist `experiment_events` only for facts that cannot be reconstructed cleanly elsewhere.

Examples:

```text
InterventionDelivered
InterventionIgnored
InterventionResponseReceived
InterventionReportedHelpful
InterventionReportedNotHelpful
```

Metrics such as:

```text
Fulfilled Commitment Rate
Silent Abandonment Rate
Focus Session Start Rate
Recovery Success Rate
Assisted Completion
```

should primarily be derived from persisted operational history.

---

# 38. Failure Handling

Infrastructure failure may delay interaction.

It must not silently alter domain truth.

---

# 39. Duplicate Telegram Update

Handled through:

```text
processed_telegram_updates
```

Duplicate processing becomes a no-op.

---

# 40. Database Failure

Do not acknowledge successful execution before the transaction commits.

If PostgreSQL is unavailable:

```text
do not persist partial state
return failure to Telegram
allow retry
```

---

# 41. Telegram Send Failure

Domain transaction remains committed.

Outbox retries delivery using bounded exponential backoff.

If the retry policy is exhausted:

```text
mark outbox entry Failed
log operational error
```

Exact backoff intervals remain configuration, not architecture.

---

# 42. Scheduler Duplicate or Late Execution

Expected and harmless.

Scheduled handlers always reload current state.

A stale trigger becomes a no-op.

---

# 43. Application Restart

No execution-critical state should exist only in memory.

Persist:

* domain state;
* conversational context where needed;
* scheduled triggers;
* outbox;
* Telegram deduplication;
* execution evidence.

Workers resume from PostgreSQL.

---

# 44. Optimistic Concurrency

Use version columns or PostgreSQL concurrency tokens for mutable aggregates.

When concurrent modifications conflict:

```text
reload
re-evaluate command
retry only where semantically safe
```

Do not blindly overwrite newer aggregate state.

---

# 45. Partial Success

Example:

```text
Commitment becomes Fulfilled
DB transaction commits
Telegram API is unavailable
```

Correct behavior:

```text
Commitment remains Fulfilled
outbox retries user notification
```

Never reverse the domain transition merely because delivery failed.

---

# 46. Deployment Model

MVP target:

```text
one Debian VPS
one OwnDay application instance
one PostgreSQL instance
```

Possible deployment:

```text
Internet
   │
   ▼
Caddy / nginx
   │
   ▼
OwnDay ASP.NET Core
   │
   ▼
PostgreSQL
```

Docker is optional infrastructure, not an architectural requirement.

Two reasonable deployments are acceptable:

### Option A

```text
Docker Compose
    OwnDay
    PostgreSQL
```

### Option B

```text
systemd
    OwnDay

host-installed
    PostgreSQL
    Caddy
```

Choose whichever keeps current operations simpler.

---

# 47. CI/CD

Minimal pipeline:

```text
GitHub push
→ restore
→ test
→ build/publish
→ deploy
→ database migration
→ restart application
→ health check
```

Database migrations should be explicit during deployment.

Do not let multiple application instances automatically compete to execute migrations.

---

# 48. Configuration

Secrets and environment-specific configuration must remain outside source control.

Examples:

```text
ConnectionStrings__Default

Telegram__BotToken
Telegram__BotUsername
Telegram__WebhookSecret

Llm__ApiKey
Llm__Model

App__BaseUrl
```

Never log:

```text
BotToken
WebhookSecret
LLM API key
```

---

# 49. Health and Operations

Minimum endpoints:

```text
/health
/ready
```

Logging:

```text
structured application logs
```

Useful operational fields include:

```text
UserId
CommitmentId
FocusSessionId
TelegramUpdateId
ScheduledTriggerId
CorrelationId
```

Do not log full sensitive conversation content by default.

---

# 50. Backups

PostgreSQL is the only durable state store.

Minimum production requirement:

* regular database backup;
* backup outside the active PostgreSQL data volume;
* documented restoration procedure;
* occasional restore verification.

---

# 51. Explicit Non-Goals

The MVP architecture deliberately excludes:

* microservices;
* event sourcing;
* CQRS infrastructure;
* message broker;
* Redis;
* distributed cache;
* workflow engine;
* generic scheduler framework;
* Temporal;
* Kafka;
* RabbitMQ;
* Kubernetes;
* service mesh;
* separate AI service;
* separate notification service;
* generic repositories over EF Core;
* custom Unit of Work abstraction over DbContext;
* analytics warehouse;
* real-time activity monitoring;
* Personal Execution Model infrastructure;
* machine-learned intervention policy.

---

# 52. Architecture Invariants

The following invariants should be treated as architectural constraints.

## A1 — Domain Authority

Only valid domain operations may change authoritative execution state.

## A2 — User Authority

Consequential user-owned decisions cannot be silently made by AI or scheduler.

## A3 — LLM Boundary

LLM output is interpretation or proposal, never authoritative state.

## A4 — LLM Freshness

State-dependent LLM output must be checked against the state version from which it was generated.

## A5 — Scheduler Boundary

Scheduler creates temporal triggers, not behavioral conclusions.

## A6 — Evidence Boundary

Execution Evidence represents observations or reports.

## A7 — Historical Facts

Accountability Events represent accepted domain facts.

## A8 — Conversation Boundary

Conversation state improves UX but never becomes authoritative domain state.

## A9 — Atomic Telegram Processing

Telegram deduplication, resulting domain mutation and outbound message intent commit atomically.

## A10 — Idempotent Async Handling

Scheduled triggers and outbox processing are safe under at-least-once execution.

## A11 — Explicit Outcomes

Silence cannot imply Fulfilled, Failed or Renegotiated.

## A12 — Timezone Correctness

Daily Commitment boundaries use the user's operative timezone, not server local time.

## A13 — Reconstructable Experiment

MVP validation metrics must be reconstructable from persisted product evidence.

---

# 53. Final Architecture

```text
                          Telegram
                              │
                              ▼
                    Telegram Webhook
                              │
                              ▼
                    Application Slices
                              │
               ┌──────────────┼──────────────┐
               │              │              │
               ▼              ▼              ▼
             Domain       LLM Assistant   Persistence
               │              │              │
               │              ▼              ▼
               │         LLM Provider    PostgreSQL
               │                             │
               └─────────────────────────────┤
                                             │
                       ┌─────────────────────┴──────────────┐
                       ▼                                    ▼
               Scheduler Worker                      Outbox Worker
                       │                                    │
                       └──── Application Slices              ▼
                                                       Telegram API
```

The application remains one modular monolith.

PostgreSQL is used not only for persistence, but also as the MVP coordination mechanism for:

* scheduled triggers;
* notification delivery;
* update deduplication;
* concurrency protection;
* experimental reconstruction.

The design intentionally prefers:

**simple durable state + idempotent processing**

over:

**distributed infrastructure + exactly-once illusions**.

---

# 54. Architecture Summary

The recommended OwnDay MVP architecture is:

**Architecture style**

Modular Monolith

**Business architecture**

Personal Execution domain

**Application organization**

Simple Clean Architecture + Vertical Slices

**Primary domain aggregates**

Commitment
FocusSession

**Supporting records/entities**

User
Reflection
ExecutionEvidence
AccountabilityEvent

**Interface**

Telegram webhook

**Persistence**

PostgreSQL + EF Core

**Scheduling**

Persistent Scheduled Triggers + ASP.NET Core BackgroundService

**Reliable notification delivery**

Transactional Outbox

**Telegram reliability**

Atomic update deduplication + domain transaction + outbox

**LLM**

Proposal and interpretation only, with context-version validation

**Concurrency**

Optimistic aggregate concurrency plus explicit serialization for cross-aggregate invariants

**Analytics**

Operational PostgreSQL data plus minimal experiment events

**Deployment**

One application instance + one PostgreSQL instance on one VPS

---

# 55. Main Architectural Thesis

The main technical problem of the OwnDay MVP is not scale.

It is maintaining a correct and auditable execution model while interacting with unreliable and probabilistic external mechanisms:

* users may respond late or ambiguously;
* Telegram may retry requests;
* Telegram delivery may fail;
* scheduler triggers may become stale;
* LLM responses may arrive against obsolete context;
* processes may restart;
* several messages may modify the same execution state.

The MVP therefore uses one core strategy:

> **Keep domain truth deterministic and durable. Treat Telegram, timers, LLM output and message delivery as potentially stale, duplicated or unreliable inputs around that truth.**

This provides enough robustness for a real behavioral experiment without building infrastructure the MVP does not yet need.
