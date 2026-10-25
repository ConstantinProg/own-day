# Phase 4 — Milestone 1: AI Analysis Foundation

Implemented against the existing Inbox/Action/Project/WaitingFor/Reference/SomedayMaybe model.
The older MVP documents describe a proposed execution model; they do not replace these implemented entities.

## Application use case

`InboxAnalysisService.AnalyzeAsync(UserId, inboxItemId, CancellationToken)` analyzes an existing, owned,
active Inbox item. Missing, foreign, processed or discarded items return `null` without calling AI.
`GetAsync(UserId, inboxItemId, CancellationToken)` reads analysis history, newest version first.
The caller must provide an authenticated/trusted user identity, as with existing application services.

The service reuses `IInboxItemStore` and the existing Inbox processing lock. It never invokes
`InboxProcessingService.ProcessAsync` and has no target-creation store dependencies.
Analysis records are Application workflow history, like the existing Inbox drafts, rather than authoritative
domain state. Domain entities and project assembly dependencies are unchanged.

1. Read the original text and capture time, and prepare bounded project context.
2. Under the existing Inbox row lock, check that the item is still active, allocate the next analysis version,
   insert a `Pending` attempt and commit. Concurrent attempts receive distinct increasing versions.
3. Call `IInboxAnalyzer` outside the PostgreSQL transaction. Existing explicit and ambient transactions are rejected.
4. Independently parse and validate the response in Application.
5. Atomically save a terminal status and individual proposals. This leaves Inbox text, status and all targets unchanged.

No HTTP analysis endpoint, Telegram hook, AI worker, queue or automatic application is added.

## OpenAI configuration and operator command

The official `OpenAI` .NET SDK 2.8.0 is confined to Infrastructure. The adapter uses Chat Completions
with strict JSON Schema, following [official OpenAI documentation](https://developers.openai.com/api/docs/guides/structured-outputs).
The configured model must support that mode; unsupported models fail the attempt without a JSON-mode fallback.
The default model is `gpt-4.1-mini`. Configure:

| Configuration key / environment variable | Default |
| --- | --- |
| `InboxAnalysis__OpenAI__Enabled` | `false` |
| `InboxAnalysis__OpenAI__ApiKey` | unset; required when enabled |
| `InboxAnalysis__OpenAI__Model` | `gpt-4.1-mini` |
| `InboxAnalysis__OpenAI__MaxOutputTokens` | `4096` (256–16384) |
| `InboxAnalysis__OpenAI__TimeoutSeconds` | `60` (1–300) |

Supply the API key through environment variables or a runtime secret store. Do not put it in source-controlled
settings, command arguments or logs. Enabled configuration is validated on host startup and on direct use.
With analysis disabled the existing host and tests require no OpenAI key. SDK retries are disabled for this milestone.

Apply migrations using the repository's normal EF tooling, for example with `dotnet-ef` installed:

```powershell
dotnet ef database update --project src/OwnDay.Infrastructure --startup-project src/OwnDay.Infrastructure
```

Set `ConnectionStrings__Default` and the OpenAI settings in the process environment, then run:

```powershell
dotnet run --project src/OwnDay.Host --configuration Release --no-launch-profile -- --analyze-inbox-user=123 --analyze-inbox-item=456
```

This trusted local operator command resolves the Application use case and prints its status and structured proposals.
It does not start HTTP or hosted workers, and does not require Telegram credentials. It accepts Ctrl+C cancellation.
`null` fields in the output indicate an unavailable Inbox item. Normal application hosts can also resolve the
service from a DI scope and call `GetAsync` for history. Output includes user proposals: treat it as private data.

## Contract, context and validation

An analysis request contains original text, capture time, supported target types/properties and project context.
One response contains 1–20 items, each with kind, canonical title, optional description, typed properties,
candidate project IDs and textual ambiguity explanations. Multiple project IDs are alternatives, not instructions
to create multiple links; they require an ambiguity explanation. Project proposals cannot have parent project links.

All five types use the existing 200-character title/text limit. WaitingFor additionally supports an exact source
excerpt and an optional `waitingSince` UTC instant with verbatim timestamp evidence from the Inbox text.
Unknown values stay null; there are no invented default dates or deadline properties.
`unresolvedDateText` preserves ambiguous date wording verbatim and requires an ambiguity explanation.
Optional descriptions retain source detail for later review; current targets do not all have description fields.

The prompt requires grammar correction while preserving intent, negation, modality and uncertainty. Entry text
and project data are explicitly untrusted data, never instructions. Application validates required fields,
unknown/duplicate fields, enum values, nulls, collection/text limits, type-specific properties, timestamp evidence,
future waiting instants, project IDs against the exact supplied snapshot, and structural contradictions.
Provider refusals, truncation and errors are handled separately. There is no numeric confidence threshold or Policy Engine.
Structural checks cannot prove that a model preserved meaning; proposals still require future policy/review before use.

The context query filters by user and active status in the database, orders by project ID and reads at most 51 rows.
It sends the first 50 and a truncation flag. Current projects contain titles, IDs and creation times but no separate
descriptions. The snapshot includes those available fields without inventing descriptions. There is no implemented
user timezone store, so `timeZoneId` is null. Relative dates are never converted into instants, even if a future
caller supplies a timezone. Only explicit ISO timestamps with an offset can justify a UTC `waitingSince`.

Input is limited to 16000 characters without truncation; the adapter also caps the serialized request at 48000
characters and validation caps the response at 64000. Oversized input is rejected before recording an attempt;
oversized serialized context produces a failed attempt. Character caps also impose a finite UTF-8 byte bound.
The SHA-256 input fingerprint and deterministic serialized context fingerprint are stored with every attempt.
Later application must reconstruct and compare context and recheck Inbox/project state and ownership; completion
of an analysis alone does not guarantee its context is still current.

## PostgreSQL schema

Migration `20261025091405_AddInboxAnalysis` adds:

- `inbox_analyses`: UUID, Inbox ID, owner, version, lifecycle status, model, schema/prompt versions,
  context snapshot (`jsonb`), context/input fingerprints, creation/completion times and safe diagnostics (`jsonb`).
- `inbox_analysis_proposals`: stable proposal UUID, analysis FK, ordinal, target kind, canonical title and
  structured payload (`jsonb`), including properties, project candidates and ambiguities.
- A composite Inbox key and owner-qualified FK prevent analyses referencing another owner's Inbox item.
  Unique `(inbox_item_id, version)` and `(analysis_id, position)` indexes enforce version/ordinal identity.
  Check constraints enforce lifecycle timing, positive versions, supported kinds and ordinal bounds.

Statuses are `Pending`, `Completed`, `InvalidResponse`, `Failed`. Invalid/failed attempts have no proposals.
Diagnostics contain only fixed failure codes and, for returned responses, completion ID and token usage.
Full rejected provider replies, exception messages, API keys and original user text are not logged or copied into diagnostics.
Stable analysis/proposal IDs and immutable version history allow later confirmation, audit and application references.

Cancellation propagates to persistence and the SDK. If cancellation/process termination occurs after the initial
commit, the attempt can remain `Pending`; it is historical evidence of an interrupted attempt, not a queued job.
A new explicit analysis creates a new version. There is no retry/recovery worker or automatic resumption.
Concurrent successful completions do not supersede or apply one another; all versions remain available.

## Verification and next milestone

Automated Application tests use a fake analyzer. SDK tests use an in-memory HTTP transport with a dummy key.
PostgreSQL tests use `OWNDAY_TEST_POSTGRES_CONNECTION`, create isolated temporary schemas, exercise real migrations,
concurrent version allocation, JSONB persistence and ownership constraints, and drop those schemas afterwards.

```powershell
dotnet restore OwnDay.slnx
dotnet build OwnDay.slnx --configuration Release --no-restore
dotnet test OwnDay.slnx --configuration Release --no-build -m:1
```

Milestone 2 can introduce the authorized Telegram trigger and presentation around this service, with an explicit
decision about durable scheduling outside webhook transactions. Any later application must validate current
context, enforce policy/confirmation and invoke existing domain/application transitions atomically with reply intent.
This milestone adds no automation policy, target mutations, confirmation UI, undo, quotas or alternate provider.
